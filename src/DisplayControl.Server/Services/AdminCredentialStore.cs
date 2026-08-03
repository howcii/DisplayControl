using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DisplayControl.Server.Services;

public sealed partial class AdminCredentialStore(IConfiguration configuration, ILogger<AdminCredentialStore> logger) : IDisposable
{
    public const string SecurityStampClaimType = "credential_stamp";
    public const string SessionStampClaimType = "admin_session_stamp";
    private const string UserName = "admin";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;
    private const string TemporaryPasswordPrefix = "DC-";
    private const string TemporaryPasswordAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const int TemporaryPasswordRandomLength = 6;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string DataPath { get; } = ResolveDataPath(configuration);

    private string CredentialPath => Path.Combine(DataPath, "admin-credentials.json");

    private string InitialCredentialPath => Path.Combine(DataPath, "initial-admin-credentials.txt");

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DataPath);
            if (File.Exists(CredentialPath))
            {
                var existingDocument = await LoadDocumentCoreAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(existingDocument.SecurityStamp))
                {
                    existingDocument = WithSecurityStamp(existingDocument);
                    await SaveDocumentAsync(existingDocument, cancellationToken);
                }

                if (existingDocument.RequiresPasswordChange &&
                    await RequiresInitialCredentialRefreshAsync(existingDocument, cancellationToken))
                {
                    var replacementPassword = GenerateTemporaryPassword();
                    existingDocument = CreateDocument(replacementPassword, requiresPasswordChange: true);
                    await SaveDocumentAsync(existingDocument, cancellationToken);
                    await WriteInitialCredentialFileAsync(replacementPassword, cancellationToken);
                    LogInitialCredentialsGenerated(logger, InitialCredentialPath);
                }

                return;
            }

            var temporaryPassword = GenerateTemporaryPassword();
            var document = CreateDocument(temporaryPassword, requiresPasswordChange: true);
            await SaveDocumentAsync(document, cancellationToken);
            await WriteInitialCredentialFileAsync(temporaryPassword, cancellationToken);
            LogInitialCredentialsGenerated(logger, InitialCredentialPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdminSessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(cancellationToken);
        return new AdminSessionInfo(
            UserName,
            document.RequiresPasswordChange,
            document.SecurityStamp!,
            document.ActiveSessionStamp);
    }

    public async Task<AdminSessionInfo?> ValidatePasswordAndStartSessionAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(userName, UserName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = WithSecurityStamp(await LoadDocumentCoreAsync(cancellationToken));
            if (!PasswordMatches(document, password))
            {
                return null;
            }

            var sessionStamp = CreateSecurityStamp();
            document = document with
            {
                ActiveSessionStamp = sessionStamp,
                ActiveSessionIssuedAtUtc = DateTimeOffset.UtcNow
            };
            await SaveDocumentAsync(document, cancellationToken);
            return new AdminSessionInfo(
                UserName,
                document.RequiresPasswordChange,
                document.SecurityStamp!,
                sessionStamp);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdminSessionValidationResult> ValidateSessionDetailedAsync(
        string? securityStamp,
        string? sessionStamp,
        bool requireCurrentSession,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(securityStamp))
        {
            return AdminSessionValidationResult.Invalid;
        }

        var document = await LoadDocumentAsync(cancellationToken);
        if (!FixedTimeEquals(securityStamp, document.SecurityStamp!))
        {
            return AdminSessionValidationResult.CredentialsChanged;
        }

        if (!requireCurrentSession)
        {
            return AdminSessionValidationResult.Valid;
        }

        if (string.IsNullOrWhiteSpace(sessionStamp) ||
            string.IsNullOrWhiteSpace(document.ActiveSessionStamp) ||
            !FixedTimeEquals(sessionStamp, document.ActiveSessionStamp))
        {
            return AdminSessionValidationResult.SessionReplaced;
        }

        return AdminSessionValidationResult.Valid;
    }

    public async Task<bool> ValidateSessionAsync(
        string? securityStamp,
        string? sessionStamp,
        bool requireCurrentSession,
        CancellationToken cancellationToken) =>
        await ValidateSessionDetailedAsync(
            securityStamp,
            sessionStamp,
            requireCurrentSession,
            cancellationToken) == AdminSessionValidationResult.Valid;

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        if (newPassword.Length < 8 || newPassword.Length > 128)
        {
            throw new InvalidOperationException("New password must be 8 to 128 characters long.");
        }

        // 先完成可能需要初始化文件的步骤，再进入更新锁，避免锁内重入
        // EnsureInitializedAsync 导致同一个 SemaphoreSlim 永久等待。
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var currentDocument = WithSecurityStamp(await LoadDocumentCoreAsync(cancellationToken));
            if (!PasswordMatches(currentDocument, currentPassword))
            {
                throw new InvalidOperationException("Current password is incorrect.");
            }

            var document = CreateDocument(newPassword, requiresPasswordChange: false);
            await SaveDocumentAsync(document, cancellationToken);
            if (File.Exists(InitialCredentialPath))
            {
                File.Delete(InitialCredentialPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool PasswordMatches(AdminCredentialDocument document, string password)
    {
        var salt = Convert.FromBase64String(document.Salt);
        var expectedHash = Convert.FromBase64String(document.PasswordHash);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            document.Iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private async Task<AdminCredentialDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadDocumentCoreAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(document.SecurityStamp))
            {
                return document;
            }

            document = WithSecurityStamp(document);
            await SaveDocumentAsync(document, cancellationToken);
            return document;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AdminCredentialDocument> LoadDocumentCoreAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(CredentialPath);
        return await JsonSerializer.DeserializeAsync<AdminCredentialDocument>(stream, _jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Administrator credential file is empty.");
    }

    private async Task SaveDocumentAsync(AdminCredentialDocument document, CancellationToken cancellationToken)
    {
        var tempPath = CredentialPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, cancellationToken);
        }

        if (File.Exists(CredentialPath))
        {
            File.Replace(tempPath, CredentialPath, null);
        }
        else
        {
            File.Move(tempPath, CredentialPath);
        }
    }

    private static AdminCredentialDocument CreateDocument(string password, bool requiresPasswordChange)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return new AdminCredentialDocument(
            UserName,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash),
            Iterations,
            requiresPasswordChange,
            DateTimeOffset.UtcNow,
            CreateSecurityStamp());
    }

    private static AdminCredentialDocument WithSecurityStamp(AdminCredentialDocument document) =>
        string.IsNullOrWhiteSpace(document.SecurityStamp)
            ? document with { SecurityStamp = CreateSecurityStamp() }
            : document;

    private static string CreateSecurityStamp() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string GenerateTemporaryPassword()
    {
        Span<char> password = stackalloc char[
            TemporaryPasswordPrefix.Length + TemporaryPasswordRandomLength];
        TemporaryPasswordPrefix.AsSpan().CopyTo(password);
        for (var index = TemporaryPasswordPrefix.Length; index < password.Length; index++)
        {
            password[index] = TemporaryPasswordAlphabet[
                RandomNumberGenerator.GetInt32(TemporaryPasswordAlphabet.Length)];
        }

        return password.ToString();
    }

    private async Task<bool> RequiresInitialCredentialRefreshAsync(
        AdminCredentialDocument document,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(InitialCredentialPath))
        {
            return true;
        }

        var password = await ReadInitialPasswordAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(password) ||
            !IsTemporaryPasswordFormat(password) ||
            !PasswordMatches(document, password);
    }

    private static bool IsTemporaryPasswordFormat(string password)
    {
        if (password.Length != TemporaryPasswordPrefix.Length + TemporaryPasswordRandomLength ||
            !password.StartsWith(TemporaryPasswordPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return password[TemporaryPasswordPrefix.Length..]
            .All(TemporaryPasswordAlphabet.Contains);
    }

    private async Task<string?> ReadInitialPasswordAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(InitialCredentialPath, cancellationToken);
        const string prefix = "Temporary password:";
        var line = lines.FirstOrDefault(static value =>
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[prefix.Length..].Trim();
    }

    private async Task WriteInitialCredentialFileAsync(
        string temporaryPassword,
        CancellationToken cancellationToken)
    {
        var text = string.Join(
            Environment.NewLine,
            "Display Control Center initial administrator credentials",
            "Username: admin",
            FormattableString.Invariant($"Temporary password: {temporaryPassword}"),
            FormattableString.Invariant($"Generated at UTC: {DateTimeOffset.UtcNow:O}"),
            "Sign in and change this password immediately.");
        await File.WriteAllTextAsync(InitialCredentialPath, text, cancellationToken);
    }

    private static string ResolveDataPath(IConfiguration configuration) =>
        StoragePaths.ResolveDataPath(configuration);

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Initial administrator credentials were generated at {CredentialPath}")]
    private static partial void LogInitialCredentialsGenerated(ILogger logger, string credentialPath);

    private sealed record AdminCredentialDocument(
        string UserName,
        string Salt,
        string PasswordHash,
        int Iterations,
        bool RequiresPasswordChange,
        DateTimeOffset UpdatedAtUtc,
        string? SecurityStamp,
        string? ActiveSessionStamp = null,
        DateTimeOffset? ActiveSessionIssuedAtUtc = null);
}

public sealed record AdminSessionInfo(
    string UserName,
    bool RequiresPasswordChange,
    string SecurityStamp,
    string? SessionStamp);

public enum AdminSessionValidationResult
{
    Valid,
    Invalid,
    CredentialsChanged,
    SessionReplaced
}
