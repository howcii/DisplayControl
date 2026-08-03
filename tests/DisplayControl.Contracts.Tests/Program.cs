using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DisplayControl.Contracts;
using DisplayControl.Server;
using DisplayControl.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var tests = new (string Name, Func<Task> Run)[]
{
    ("协议版本边界", TestProtocolVersions),
    ("设备注册与心跳", TestRegistrationAndHeartbeat),
    ("旧连接断开不覆盖新连接", TestStaleDisconnect),
    ("未注册连接不能伪造心跳", TestUnregisteredHeartbeat),
    ("并发设备注册与心跳", TestConcurrentRegistryLoad),
    ("V10 登录响应字段兼容", TestV10AuthContract),
    ("CSRF 请求头校验", TestCsrfValidation),
    ("Cookie 密钥保存在数据目录", TestDataProtectionPath),
    ("管理员改密不会死锁", TestPasswordChangeDoesNotDeadlock),
    ("单一管理员会话替换旧设备", TestSingleAdminSessionReplacement),
    ("验证码变形且不区分大小写", TestCaptchaWarpAndSingleUse)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failures.Count} failed={failures.Count}");
return failures.Count == 0 ? 0 : 1;

static Task TestProtocolVersions()
{
    Assert(Protocol.IsSupported(Protocol.LegacyV15Version), "V15 protocol must be accepted.");
    Assert(Protocol.IsSupported(Protocol.CurrentVersion), "Current protocol must be accepted.");
    Assert(!Protocol.IsSupported(0), "Protocol 0 must be rejected.");
    Assert(!Protocol.IsSupported(4), "Unknown future protocol must be rejected.");
    return Task.CompletedTask;
}

static Task TestRegistrationAndHeartbeat()
{
    var registry = new DeviceRegistry();
    registry.Register("connection-1", Registration("screen-01"));
    var observedAt = DateTimeOffset.UtcNow;
    var snapshot = registry.UpdateHeartbeat(
        "connection-1",
        new AgentHeartbeat(
            "screen-01",
            observedAt,
            DisplayState.BlackOverlay,
            true));

    Assert(snapshot.IsOnline, "Registered device must be online.");
    Assert(snapshot.State == DisplayState.BlackOverlay, "Observed state must be stored.");
    Assert(snapshot.IsStateVerified, "Verified observation must remain verified.");
    Assert(snapshot.LastSeenUtc == observedAt, "Heartbeat timestamp must be preserved.");
    return Task.CompletedTask;
}

static Task TestStaleDisconnect()
{
    var registry = new DeviceRegistry();
    registry.Register("old", Registration("screen-01"));
    registry.Register("new", Registration("screen-01"));

    var staleResult = registry.Disconnect("old");
    Assert(staleResult is null, "Old connection must not mark replacement offline.");
    Assert(registry.GetRequiredSnapshot("screen-01").IsOnline, "Replacement must stay online.");

    var currentResult = registry.Disconnect("new");
    Assert(currentResult is { IsOnline: false }, "Current disconnect must mark device offline.");
    return Task.CompletedTask;
}

static Task TestUnregisteredHeartbeat()
{
    var registry = new DeviceRegistry();
    var rejected = false;
    try
    {
        registry.UpdateHeartbeat(
            "attacker",
            new AgentHeartbeat(
                "screen-01",
                DateTimeOffset.UtcNow,
                DisplayState.On,
                true));
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }

    Assert(rejected, "Unregistered connection heartbeat must be rejected.");
    return Task.CompletedTask;
}

static async Task TestConcurrentRegistryLoad()
{
    const int deviceCount = 500;
    const int heartbeatsPerDevice = 20;
    var registry = new DeviceRegistry();
    var errors = new ConcurrentQueue<Exception>();

    await Parallel.ForEachAsync(
        Enumerable.Range(0, deviceCount),
        async (index, _) =>
        {
            try
            {
                var deviceId = $"screen-{index:D4}";
                var connectionId = $"connection-{index:D4}";
                registry.Register(connectionId, Registration(deviceId));
                for (var heartbeat = 0; heartbeat < heartbeatsPerDevice; heartbeat++)
                {
                    registry.UpdateHeartbeat(
                        connectionId,
                        new AgentHeartbeat(
                            deviceId,
                            DateTimeOffset.UtcNow,
                            heartbeat % 2 == 0 ? DisplayState.On : DisplayState.Standby,
                            true));
                    await Task.Yield();
                }
            }
            catch (Exception exception)
            {
                errors.Enqueue(exception);
            }
        });

    Assert(errors.IsEmpty, $"Concurrent registry operations produced {errors.Count} errors.");
    Assert(registry.GetSnapshots().Count == deviceCount, "All concurrent devices must remain registered.");
    Assert(registry.GetSnapshots().All(device => device.IsOnline), "All load-test devices must be online.");
}

static Task TestV10AuthContract()
{
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var sessionJson = JsonSerializer.Serialize(
        new AuthSessionResponse(true, "admin", false, "csrf-value"),
        jsonOptions);
    using var sessionDocument = JsonDocument.Parse(sessionJson);
    var session = sessionDocument.RootElement;

    Assert(session.GetProperty("authenticated").GetBoolean(), "V10 expects authenticated.");
    Assert(session.GetProperty("username").GetString() == "admin", "V10 expects username.");
    Assert(!session.GetProperty("mustChangePassword").GetBoolean(), "V10 expects mustChangePassword.");
    Assert(session.GetProperty("csrfToken").GetString() == "csrf-value", "V10 expects csrfToken.");
    Assert(!session.TryGetProperty("isAuthenticated", out _), "Do not replace the V10 field name.");

    var captchaJson = JsonSerializer.Serialize(
        new CaptchaResponse("captcha-id", "data:image/svg+xml;base64,AA==", 300),
        jsonOptions);
    using var captchaDocument = JsonDocument.Parse(captchaJson);
    var captcha = captchaDocument.RootElement;
    Assert(captcha.GetProperty("id").GetString() == "captcha-id", "V10 expects captcha id.");
    Assert(captcha.GetProperty("imageUrl").GetString() is not null, "V10 expects imageUrl.");
    Assert(captcha.GetProperty("expiresInSeconds").GetInt32() == 300, "V10 expects expiresInSeconds.");
    return Task.CompletedTask;
}

static Task TestCsrfValidation()
{
    const string token = "valid-csrf-token";
    var context = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(CsrfEndpointFilter.ClaimType, token)],
                "test-cookie"))
    };

    context.Request.Method = HttpMethods.Post;
    Assert(!CsrfEndpointFilter.IsValid(context), "Missing CSRF header must be rejected.");

    context.Request.Headers[CsrfEndpointFilter.HeaderName] = "wrong-token";
    Assert(!CsrfEndpointFilter.IsValid(context), "Wrong CSRF header must be rejected.");

    context.Request.Headers[CsrfEndpointFilter.HeaderName] = token;
    Assert(CsrfEndpointFilter.IsValid(context), "Matching CSRF header must be accepted.");
    Assert(CsrfEndpointFilter.RequiresValidation(context.Request), "POST must require CSRF.");

    context.Request.Method = HttpMethods.Get;
    Assert(!CsrfEndpointFilter.RequiresValidation(context.Request), "GET must not require CSRF.");
    return Task.CompletedTask;
}

static Task TestDataProtectionPath()
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = "test-data"
            })
        .Build();
    var dataPath = StoragePaths.ResolveDataPath(configuration);
    var keyPath = StoragePaths.ResolveDataProtectionKeyPath(configuration);

    Assert(
        keyPath == Path.Combine(dataPath, "data-protection-keys"),
        "Data Protection keys must stay under Storage:DataPath.");
    return Task.CompletedTask;
}

static async Task TestPasswordChangeDoesNotDeadlock()
{
    var testDataPath = Path.Combine(
        AppContext.BaseDirectory,
        "test-data",
        $"auth-{Guid.NewGuid():N}");
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = testDataPath
            })
        .Build();
    using var loggerFactory = LoggerFactory.Create(_ => { });
    using var credentials = new AdminCredentialStore(
        configuration,
        loggerFactory.CreateLogger<AdminCredentialStore>());

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await credentials.EnsureInitializedAsync(timeout.Token);
        var initialCredentialText = await File.ReadAllLinesAsync(
            Path.Combine(testDataPath, "initial-admin-credentials.txt"),
            timeout.Token);
        var temporaryPasswordLine = initialCredentialText.Single(
            line => line.StartsWith("Temporary password:", StringComparison.Ordinal));
        var temporaryPassword = temporaryPasswordLine["Temporary password:".Length..].Trim();
        Assert(
            temporaryPassword.Length == 9 &&
            temporaryPassword.StartsWith("DC-", StringComparison.Ordinal),
            "Temporary password must use the human-readable random DC-XXXXXX format.");

        const string replacementPassword = "DisplayControl-Test-2026";
        await credentials.ChangePasswordAsync(
            temporaryPassword,
            replacementPassword,
            timeout.Token);
        var session = await credentials.ValidatePasswordAndStartSessionAsync(
            "admin",
            replacementPassword,
            timeout.Token);

        Assert(session is not null, "Replacement password must authenticate.");
        Assert(!session!.RequiresPasswordChange, "Password change flag must be cleared.");
        Assert(!string.IsNullOrWhiteSpace(session.SecurityStamp), "Credential security stamp must be present.");
        Assert(!string.IsNullOrWhiteSpace(session.SessionStamp), "Single admin session stamp must be present.");
    }
    finally
    {
        if (Directory.Exists(testDataPath))
        {
            Directory.Delete(testDataPath, recursive: true);
        }
    }
}

static async Task TestSingleAdminSessionReplacement()
{
    var testDataPath = Path.Combine(
        AppContext.BaseDirectory,
        "test-data",
        $"single-session-{Guid.NewGuid():N}");
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = testDataPath
            })
        .Build();
    using var loggerFactory = LoggerFactory.Create(_ => { });
    using var credentials = new AdminCredentialStore(
        configuration,
        loggerFactory.CreateLogger<AdminCredentialStore>());

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await credentials.EnsureInitializedAsync(timeout.Token);
        var temporaryPassword = await ReadTemporaryPasswordAsync(testDataPath, timeout.Token);
        var firstSession = await credentials.ValidatePasswordAndStartSessionAsync(
            "admin",
            temporaryPassword,
            timeout.Token);
        var secondSession = await credentials.ValidatePasswordAndStartSessionAsync(
            "admin",
            temporaryPassword,
            timeout.Token);

        Assert(firstSession is not null && secondSession is not null, "Both valid logins must create sessions.");
        Assert(
            firstSession!.SessionStamp != secondSession!.SessionStamp,
            "A new login must rotate the single-session stamp.");
        Assert(
            await credentials.ValidateSessionDetailedAsync(
                firstSession.SecurityStamp,
                firstSession.SessionStamp,
                requireCurrentSession: true,
                timeout.Token) == AdminSessionValidationResult.SessionReplaced,
            "The previous device must be reported as replaced.");
        Assert(
            await credentials.ValidateSessionDetailedAsync(
                secondSession.SecurityStamp,
                secondSession.SessionStamp,
                requireCurrentSession: true,
                timeout.Token) == AdminSessionValidationResult.Valid,
            "The newest device must keep the active session.");
        Assert(
            await credentials.ValidateSessionAsync(
                firstSession.SecurityStamp,
                firstSession.SessionStamp,
                requireCurrentSession: false,
                timeout.Token),
            "Disabling single-session mode must preserve a credential-valid older session.");
    }
    finally
    {
        if (Directory.Exists(testDataPath))
        {
            Directory.Delete(testDataPath, recursive: true);
        }
    }
}

static Task TestCaptchaWarpAndSingleUse()
{
    const string prefix = "data:image/svg+xml;base64,";
    var captchaStore = new CaptchaStore();
    for (var attempt = 0; attempt < 8; attempt++)
    {
        var captcha = captchaStore.Create();
        Assert(captcha.ImageUrl.StartsWith(prefix, StringComparison.Ordinal), "Captcha must remain an inline SVG image.");
        var svg = Encoding.UTF8.GetString(Convert.FromBase64String(captcha.ImageUrl[prefix.Length..]));
        Assert(svg.Contains("<feDisplacementMap", StringComparison.Ordinal), "Captcha characters must use wave displacement.");
        Assert(svg.Contains("skewX(", StringComparison.Ordinal), "Captcha characters must use skew distortion.");
        Assert(svg.Contains(" scale(", StringComparison.Ordinal), "Captcha characters must use non-uniform scaling.");

        var glyphs = Regex.Matches(svg, @">([A-Za-z0-9])</text>")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert(glyphs.Length == 15, "Captcha must render the five-character code in three clipped bands.");
        var code = string.Concat(glyphs.Take(5));
        Assert(code.Length == 5, "Captcha must contain five readable characters.");
        Assert(captchaStore.Consume(captcha.Id, code.ToLowerInvariant()), "Captcha comparison must ignore letter case.");
        Assert(!captchaStore.Consume(captcha.Id, code), "Captcha must be single-use.");
    }
    return Task.CompletedTask;
}

static async Task<string> ReadTemporaryPasswordAsync(
    string testDataPath,
    CancellationToken cancellationToken)
{
    var lines = await File.ReadAllLinesAsync(
        Path.Combine(testDataPath, "initial-admin-credentials.txt"),
        cancellationToken);
    const string prefix = "Temporary password:";
    return lines.Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();
}

static AgentRegistration Registration(string deviceId) =>
    new(deviceId, deviceId, "16.0.0", Protocol.CurrentVersion, "test-token");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
