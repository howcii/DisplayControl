using System.Threading.RateLimiting;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using DisplayControl.Contracts;
using DisplayControl.Server;
using DisplayControl.Server.Hubs;
using DisplayControl.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
const string authReasonHeaderName = "X-DisplayControl-Auth-Reason";

builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();
builder.Logging.AddDebug();

if (OperatingSystem.IsWindows() &&
    WindowsServiceHelpers.IsWindowsService() &&
    !Environment.UserInteractive)
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "DisplayControlCenterV11";
    });
}

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 64 * 1024;
});
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<OperationLogStore>();
builder.Services.AddSingleton<CommandCoordinator>();
builder.Services.AddSingleton<AdminCredentialStore>();
builder.Services.AddSingleton<CaptchaStore>();

// Windows 服务默认可能没有稳定的用户配置目录。把 Cookie 签名密钥保存到
// Storage:DataPath，服务重启后仍能验证已有会话，且不会写入 LocalAppData。
var dataProtectionKeyPath = StoragePaths.ResolveDataProtectionKeyPath(builder.Configuration);
Directory.CreateDirectory(dataProtectionKeyPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("DisplayControlCenter")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = builder.Configuration["Security:CookieName"] ?? "DisplayControl.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Configuration.GetValue(
            "Security:RequireSecureCookies",
            false)
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var securityStamp = context.Principal?.FindFirstValue(AdminCredentialStore.SecurityStampClaimType);
            var sessionStamp = context.Principal?.FindFirstValue(AdminCredentialStore.SessionStampClaimType);
            var credentials = context.HttpContext.RequestServices.GetRequiredService<AdminCredentialStore>();
            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var requireCurrentSession = configuration.GetValue("Security:SingleAdminSession", true);
            var validation = await credentials.ValidateSessionDetailedAsync(
                securityStamp,
                sessionStamp,
                requireCurrentSession,
                context.HttpContext.RequestAborted);
            if (validation != AdminSessionValidationResult.Valid)
            {
                context.HttpContext.Response.Headers[authReasonHeaderName] = validation switch
                {
                    AdminSessionValidationResult.SessionReplaced => "session-replaced",
                    AdminSessionValidationResult.CredentialsChanged => "credentials-changed",
                    _ => "invalid-session"
                };
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();
var agentHubRequestsPerMinute = builder.Configuration.GetValue(
    "RateLimits:AgentHubRequestsPerMinute",
    120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("agent-connect", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                // A SignalR connection normally consumes both a negotiate request
                // and a transport request. Count requests rather than devices.
                PermitLimit = agentHubRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
        }));
});
builder.Services.AddHostedService<CommandMaintenanceService>();

var app = builder.Build();

await app.Services
    .GetRequiredService<AdminCredentialStore>()
    .EnsureInitializedAsync(CancellationToken.None);

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (ShouldDisableShellCache(context.Request.Path))
    {
        context.Response.OnStarting(() =>
        {
            // The control page is a single HTML shell with inline JavaScript.
            // Do not let browsers keep an old login script after a local upgrade.
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            return Task.CompletedTask;
        });
    }

    await next();
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapGet("/version", VersionInfo);
app.MapMethods("/api/android-agent/poll", ["GET", "POST"], AndroidAgentPollAsync);
app.MapMethods("/api/android-agent/result", ["GET", "POST"], AndroidAgentResultAsync);

var auth = app.MapGroup("/api/auth");
auth.MapGet("/captcha", (CaptchaStore captcha) => TypedResults.Ok(captcha.Create()));
auth.MapGet("/session", async (
    ClaimsPrincipal user,
    AdminCredentialStore credentials,
    CancellationToken cancellationToken) =>
{
    if (user.Identity?.IsAuthenticated != true)
    {
        return Results.Ok(new AuthSessionResponse(false, null, false, null));
    }

    var session = await credentials.GetSessionInfoAsync(cancellationToken);
    return Results.Ok(new AuthSessionResponse(
        true,
        session.UserName,
        session.RequiresPasswordChange,
        user.FindFirstValue(CsrfEndpointFilter.ClaimType)));
});
auth.MapPost("/login", async (
    LoginRequest request,
    HttpContext httpContext,
    AdminCredentialStore credentials,
    CaptchaStore captcha,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.EffectiveUserName) ||
        string.IsNullOrEmpty(request.Password) ||
        string.IsNullOrWhiteSpace(request.CaptchaId) ||
        string.IsNullOrWhiteSpace(request.CaptchaCode))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "登录信息不完整",
            detail: "请输入用户名、密码和验证码。");
    }

    if (!captcha.Consume(request.CaptchaId, request.CaptchaCode))
    {
        // 登录本身尚未建立会话，不能用 401 表示输入错误；旧网页会把
        // 401 解释为“已有登录状态失效”。
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "登录失败",
            detail: "验证码错误或已过期，请刷新验证码后重试。");
    }

    var session = await credentials.ValidatePasswordAndStartSessionAsync(
        request.EffectiveUserName,
        request.Password,
        cancellationToken);
    if (session is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "登录失败",
            detail: "用户名或密码错误。");
    }

    var csrfToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, session.UserName),
            new Claim(CsrfEndpointFilter.ClaimType, csrfToken),
            new Claim(AdminCredentialStore.SecurityStampClaimType, session.SecurityStamp),
            new Claim(AdminCredentialStore.SessionStampClaimType, session.SessionStamp ?? "")
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Ok(new AuthSessionResponse(
        true,
        session.UserName,
        session.RequiresPasswordChange,
        csrfToken));
});
auth.MapPost("/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
})
    .RequireAuthorization()
    .AddEndpointFilter<CsrfEndpointFilter>();
auth.MapPost("/change-password", async (
    ChangePasswordRequest request,
    HttpContext httpContext,
    AdminCredentialStore credentials,
    CancellationToken cancellationToken) =>
{
    await credentials.ChangePasswordAsync(
        request.CurrentPassword,
        request.NewPassword,
        cancellationToken);
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
})
    .RequireAuthorization()
    .AddEndpointFilter<CsrfEndpointFilter>();

var api = app.MapGroup("/api")
    .RequireAuthorization()
    .AddEndpointFilter<CsrfEndpointFilter>();
api.MapGet("/version", () => TypedResults.Ok(VersionInfo()));
api.MapGet("/screens", (DeviceRegistry registry) =>
    TypedResults.Ok(registry.GetViews()));
api.MapGet("/screens/{deviceId}", (string deviceId, DeviceRegistry registry) =>
    registry.TryGetView(deviceId, out var snapshot)
        ? Results.Ok(snapshot)
        : Results.NotFound());
api.MapPatch("/screens/{deviceId}", (
    string deviceId,
    DeviceUpdateRequest request,
    DeviceRegistry registry) =>
{
    try
    {
        return Results.Ok(registry.UpdateDevice(deviceId, request));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});
api.MapDelete("/screens/{deviceId}", (
    string deviceId,
    DeviceRegistry registry,
    OperationLogStore logs) =>
{
    if (!registry.DeleteDevice(deviceId))
    {
        return Results.NotFound();
    }

    logs.Add(deviceId, deviceId, "delete_device", true, "已删除");
    return Results.NoContent();
});
api.MapPost("/screens/{command}", async (
    string command,
    string? mode,
    DeviceRegistry registry,
    CommandCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var deviceIds = registry.GetViews().Where(device => device.Online).Select(device => device.Id);
    try
    {
        var result = await coordinator.DispatchWebCommandAsync(
            deviceIds,
            command,
            mode,
            cancellationToken);
        return CommandStartResult(result);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "不支持的控制命令",
            detail: ex.Message);
    }
});
api.MapPost("/screens/{deviceId}/{command}", async (
    string deviceId,
    string command,
    string? mode,
    DeviceRegistry registry,
    CommandCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!registry.TryGetView(deviceId, out var device))
    {
        return Results.NotFound();
    }

    if (!device.Online)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "设备离线",
            detail: "该设备当前离线，无法下发控制命令。");
    }

    try
    {
        var result = await coordinator.DispatchWebCommandAsync(
            [deviceId],
            command,
            mode,
            cancellationToken);
        return CommandStartResult(result);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "不支持的控制命令",
            detail: ex.Message);
    }
});
api.MapPost("/screens/batch/{command}", async (
    string command,
    BatchCommandRequest request,
    CommandCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await coordinator.DispatchWebCommandAsync(
            request.DeviceIds,
            command,
            request.Mode,
            cancellationToken);
        return CommandStartResult(result);
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "不支持的控制命令",
            detail: ex.Message);
    }
});
api.MapGet("/logs", (int? limit, OperationLogStore logs) =>
    TypedResults.Ok(logs.GetRecent(limit ?? 200)));
api.MapGet("/command-requests/{requestId}", (
    string requestId,
    CommandCoordinator coordinator) =>
    TypedResults.Ok(coordinator.GetExecutions(requestId)));
api.MapGet("/commands/{commandId:guid}", (
    Guid commandId,
    CommandCoordinator coordinator) =>
    coordinator.TryGet(commandId, out var command)
        ? Results.Ok(command)
        : Results.NotFound());

app.MapHub<DashboardHub>("/hubs/dashboard").RequireAuthorization();

// V15 compatibility is deliberately isolated. It remains enabled only while the
// recovered protocol is being verified against a real V15 Agent.
var allowLegacyAgents = builder.Configuration.GetValue(
    "Compatibility:AllowUnauthenticatedV15Agents",
    true);
var agentHub = app.MapHub<AgentHub>("/hubs/agent")
    .RequireRateLimiting("agent-connect");
if (!allowLegacyAgents)
{
    agentHub.RequireAuthorization();
}

app.MapFallbackToFile("index.html");
app.Run();

static IResult CommandStartResult(CommandStartResponse result) =>
    result.Executions.Count == 0
        ? Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "没有可控制的在线设备",
            detail: "请先确认 Agent 在线，再重新下发控制命令。")
        : Results.Accepted(value: result);

static object VersionInfo() => new
{
    product = "Display Control Center",
    serverVersion = "11.7.0",
    releaseVersion = "2026.08.03.1",
    protocolVersion = DisplayControl.Contracts.Protocol.CurrentVersion
};

static async Task<IResult> AndroidAgentPollAsync(
    HttpContext httpContext,
    DeviceRegistry registry,
    CommandCoordinator coordinator,
    IHubContext<DashboardHub> dashboard,
    CancellationToken cancellationToken)
{
    var body = await ReadJsonBodyAsync(httpContext, cancellationToken);
    var deviceId = ReadString(body, httpContext.Request.Query, "deviceId", "device_id", "androidId", "android_id");
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Android Agent 缺少设备编号",
            detail: "请在 Android Agent 中填写设备编号后重新连接。");
    }

    deviceId = deviceId.Trim();
    var connectionId = AndroidConnectionId(deviceId);
    var deviceName = ReadString(body, httpContext.Request.Query, "deviceName", "device_name", "name") ??
        $"Android 展示屏 {deviceId}";
    var agentVersion = ReadString(body, httpContext.Request.Query, "agentVersion", "agent_version") ??
        "Android Agent V1.5";
    var secret = ReadString(body, httpContext.Request.Query, "agentSecret", "agent_secret") ??
        httpContext.Request.Headers["X-Agent-Secret"].FirstOrDefault();
    var observedAtUtc = DateTimeOffset.UtcNow;
    var reportedState = ReadDisplayState(body, httpContext.Request.Query);
    var hasUsefulReportedState = reportedState.HasValue && reportedState.Value is not DisplayState.Unknown;
    var previousKnownState = registry.TryGetView(deviceId, out var existingView) &&
        existingView.State is not DisplayState.Unknown
            ? existingView.State
            : DisplayState.On;
    var state = hasUsefulReportedState
        ? reportedState!.Value
        : previousKnownState;
    var verified = hasUsefulReportedState &&
        (ReadBool(body, httpContext.Request.Query, "screenStatusVerified", "screen_status_verified", "isStateVerified", "verified") ?? false);
    var detail = ReadString(body, httpContext.Request.Query, "screenStatusDetails", "statusDetails", "detail", "message");
    if (!hasUsefulReportedState)
    {
        detail = existingView is { State: not DisplayState.Unknown }
            ? "Android Agent 轮询在线，未报告新屏幕状态，已沿用上一次状态。"
            : "Android Agent 已连接；旧 APK 尚未产生屏幕状态，先按亮屏在线显示。";
    }
    detail ??= "Android Agent V1.5 HTTP 轮询在线。";

    registry.Register(
        connectionId,
        new AgentRegistration(deviceId, deviceName.Trim(), agentVersion.Trim(), Protocol.CurrentVersion, secret),
        httpContext.Connection.RemoteIpAddress?.ToString());
    var view = registry.UpdateHeartbeat(
        connectionId,
        new AgentHeartbeat(deviceId, observedAtUtc, state, verified, detail));
    await dashboard.Clients.All.SendAsync("DeviceChanged", view, cancellationToken);
    await dashboard.Clients.All.SendAsync("DevicesChanged", cancellationToken);

    if (coordinator.TryGetPollingCommand(deviceId, out var pending))
    {
        if (pending.Stage is CommandStage.Created or CommandStage.Dispatched)
        {
            coordinator.RecordReceipt(
                connectionId,
                new CommandReceipt(
                    pending.Command.CommandId,
                    deviceId,
                    CommandStage.Accepted,
                    observedAtUtc,
                    true,
                    "Android Agent 已轮询接收命令。"));
        }

        var androidCommand = ToAndroidCommandName(pending.Command.Kind);
        var apiCommand = DeviceApiMapping.ToApiCommand(pending.Command.Kind);
        var mode = pending.Command.Kind is CommandKind.ShowBlackOverlay
            ? "blackout"
            : pending.Command.Kind is CommandKind.Standby
                ? "standby"
                : "on";
        var commandIdText = pending.Command.CommandId.ToString("D");
        return Results.Ok(new
        {
            success = true,
            connected = true,
            hasCommand = true,
            delayMs = 1000,
            requestId = commandIdText,
            commandId = commandIdText,
            command = new
            {
                requestId = commandIdText,
                commandId = commandIdText,
                command = androidCommand,
                commandName = androidCommand,
                apiCommand,
                mode,
                kind = (int)pending.Command.Kind
            },
            commandName = androidCommand,
            apiCommand,
            mode,
            kind = (int)pending.Command.Kind,
            legacy = new
            {
                requestId = commandIdText,
                commandId = commandIdText,
                command = androidCommand,
                commandName = androidCommand,
                apiCommand,
                mode,
                kind = (int)pending.Command.Kind
            },
            message = "Android Agent 已连接，存在待执行命令。"
        });
    }

    return Results.Ok(new
    {
        success = true,
        connected = true,
        hasCommand = false,
        delayMs = 3000,
        command = (object?)null,
        commandName = "",
        message = "Android Agent 已连接，暂无待执行命令。"
    });
}

static async Task<IResult> AndroidAgentResultAsync(
    HttpContext httpContext,
    DeviceRegistry registry,
    CommandCoordinator coordinator,
    IHubContext<DashboardHub> dashboard,
    CancellationToken cancellationToken)
{
    var body = await ReadJsonBodyAsync(httpContext, cancellationToken);
    var deviceId = ReadString(body, httpContext.Request.Query, "deviceId", "device_id", "androidId", "android_id");
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Android Agent 缺少设备编号",
            detail: "结果回执必须包含 deviceId。");
    }

    deviceId = deviceId.Trim();
    var connectionId = AndroidConnectionId(deviceId);
    var deviceName = ReadString(body, httpContext.Request.Query, "deviceName", "device_name", "name") ??
        $"Android 展示屏 {deviceId}";
    var agentVersion = ReadString(body, httpContext.Request.Query, "agentVersion", "agent_version") ??
        "Android Agent V1.5";
    var secret = ReadString(body, httpContext.Request.Query, "agentSecret", "agent_secret") ??
        httpContext.Request.Headers["X-Agent-Secret"].FirstOrDefault();
    var observedAtUtc = DateTimeOffset.UtcNow;
    var succeeded = ReadBool(body, httpContext.Request.Query, "success", "succeeded", "ok") ?? true;
    var commandId = ReadGuid(body, httpContext.Request.Query, "commandId", "command_id", "requestId", "request_id", "id");
    var commandKind = commandId.HasValue && coordinator.TryGet(commandId.Value, out var status)
        ? status.Command.Kind
        : FromAndroidCommandName(ReadString(body, httpContext.Request.Query, "commandName", "command", "apiCommand"));
    var state = ReadDisplayState(body, httpContext.Request.Query) ??
        (succeeded && commandKind.HasValue ? StateFor(commandKind.Value) : DisplayState.Unknown);
    var verified = ReadBool(body, httpContext.Request.Query, "screenStatusVerified", "screen_status_verified", "isStateVerified", "verified") ??
        state is DisplayState.BlackOverlay;
    var detail = ReadString(body, httpContext.Request.Query, "screenStatusDetails", "statusDetails", "detail", "message") ??
        (succeeded ? "Android Agent 已完成命令。" : "Android Agent 执行命令失败。");

    registry.Register(
        connectionId,
        new AgentRegistration(deviceId, deviceName.Trim(), agentVersion.Trim(), Protocol.CurrentVersion, secret),
        httpContext.Connection.RemoteIpAddress?.ToString());
    var view = registry.UpdateHeartbeat(
        connectionId,
        new AgentHeartbeat(deviceId, observedAtUtc, state, verified, detail));

    if (commandId.HasValue && coordinator.TryGet(commandId.Value, out _))
    {
        var explicitStage = ReadCommandStage(body, httpContext.Request.Query, "stage", "commandStage");
        var stage = explicitStage ??
            (succeeded
                ? (verified ? CommandStage.Verified : CommandStage.Executed)
                : CommandStage.Failed);
        coordinator.RecordReceipt(
            connectionId,
            new CommandReceipt(commandId.Value, deviceId, stage, observedAtUtc, succeeded, detail));
    }

    if (registry.TryGetView(deviceId, out view))
    {
        await dashboard.Clients.All.SendAsync("DeviceChanged", view, cancellationToken);
        await dashboard.Clients.All.SendAsync("DevicesChanged", cancellationToken);
    }

    return Results.Ok(new
    {
        success = true,
        connected = true,
        accepted = true,
        message = "Android Agent 结果已接收。"
    });
}

static async Task<JsonElement?> ReadJsonBodyAsync(HttpContext httpContext, CancellationToken cancellationToken)
{
    if (HttpMethods.IsGet(httpContext.Request.Method) ||
        httpContext.Request.ContentLength is 0)
    {
        return null;
    }

    try
    {
        using var document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }
    catch (JsonException)
    {
        return null;
    }
}

static string AndroidConnectionId(string deviceId) => $"android-http:{deviceId}";

static string? ReadString(JsonElement? body, IQueryCollection query, params string[] names)
{
    foreach (var name in names)
    {
        if (body.HasValue && TryGetProperty(body.Value, name, out var property))
        {
            var value = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (query.TryGetValue(name, out var queryValue) &&
            !string.IsNullOrWhiteSpace(queryValue.FirstOrDefault()))
        {
            return queryValue.FirstOrDefault();
        }
    }

    return null;
}

static bool? ReadBool(JsonElement? body, IQueryCollection query, params string[] names)
{
    var value = ReadString(body, query, names);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    return value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "y" or "ok" or "success" => true,
        "0" or "false" or "no" or "n" or "failed" or "error" => false,
        _ => null
    };
}

static Guid? ReadGuid(JsonElement? body, IQueryCollection query, params string[] names)
{
    var value = ReadString(body, query, names);
    return Guid.TryParse(value, out var guid) ? guid : null;
}

static CommandStage? ReadCommandStage(JsonElement? body, IQueryCollection query, params string[] names)
{
    var value = ReadString(body, query, names);
    if (int.TryParse(value, out var numeric) &&
        Enum.IsDefined(typeof(CommandStage), numeric))
    {
        return (CommandStage)numeric;
    }

    return Enum.TryParse<CommandStage>(value, ignoreCase: true, out var stage)
        ? stage
        : null;
}

static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var item in element.EnumerateObject())
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = item.Value;
                return true;
            }
        }
    }

    property = default;
    return false;
}

static DisplayState? ReadDisplayState(JsonElement? body, IQueryCollection query)
{
    var value = ReadString(
        body,
        query,
        "screenStatus",
        "screen_status",
        "lastStatus",
        "last_status",
        "status",
        "state");
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (int.TryParse(value, out var numeric) &&
        Enum.IsDefined(typeof(DisplayState), numeric))
    {
        return (DisplayState)numeric;
    }

    return value.Trim().ToLowerInvariant() switch
    {
        "on" or "screen_on" or "android-agent-on" or "connected" => DisplayState.On,
        "standby" or "off" or "screen_off" or "screen_off_standby" or "android-safe-overlay-standby" => DisplayState.Standby,
        "blackout" or "screen_off_blackout" or "android-overlay-blackout" => DisplayState.BlackOverlay,
        "dimmed" or "dim" or "android-overlay-low-brightness" => DisplayState.Dimmed,
        _ => DisplayState.Unknown
    };
}

static string ToAndroidCommandName(CommandKind kind) => kind switch
{
    CommandKind.TurnOn or CommandKind.RemoveBlackOverlay => "screen_on",
    CommandKind.Standby or CommandKind.ShowBlackOverlay => "screen_off",
    _ => "unknown"
};

static CommandKind? FromAndroidCommandName(string? commandName) =>
    commandName?.Trim().ToLowerInvariant() switch
    {
        "screen_on" or "on" => CommandKind.TurnOn,
        "screen_off" or "off" or "screen_off_standby" => CommandKind.Standby,
        "screen_off_blackout" or "blackout" => CommandKind.ShowBlackOverlay,
        _ => null
    };

static DisplayState StateFor(CommandKind kind) => kind switch
{
    CommandKind.TurnOn or CommandKind.RemoveBlackOverlay => DisplayState.On,
    CommandKind.Standby => DisplayState.Standby,
    CommandKind.ShowBlackOverlay => DisplayState.BlackOverlay,
    _ => DisplayState.Unknown
};

static bool ShouldDisableShellCache(PathString requestPath)
{
    var path = requestPath.Value ?? "/";
    if (path.Equals("/", StringComparison.Ordinal) ||
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/version", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return !Path.HasExtension(path);
}

public partial class Program;
