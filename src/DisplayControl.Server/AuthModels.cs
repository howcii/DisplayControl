namespace DisplayControl.Server;

public sealed record LoginRequest(
    string? Username,
    string Password,
    string? CaptchaId = null,
    string? CaptchaCode = null)
{
    public string EffectiveUserName => Username ?? "";
}

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthSessionResponse(
    bool Authenticated,
    string? Username,
    bool MustChangePassword,
    string? CsrfToken);

public sealed record CaptchaResponse(
    string Id,
    string ImageUrl,
    int ExpiresInSeconds);
