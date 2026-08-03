using DisplayControl.Contracts;

namespace DisplayControl.Server;

public sealed record DeviceView(
    string Id,
    string Name,
    string? Note,
    string Ip,
    bool Online,
    string ScreenStatus,
    bool ScreenStatusVerified,
    string ScreenStatusSource,
    string? ScreenStatusDetails,
    DateTimeOffset? ScreenStatusObservedAt,
    string AgentVersion,
    int ProtocolVersion,
    DateTimeOffset LastHeartbeat,
    bool NameLocked)
{
    public bool IsOnline => Online;

    public DisplayState State => ScreenStatus switch
    {
        "on" => DisplayState.On,
        "standby" => DisplayState.Standby,
        "blackout" => DisplayState.BlackOverlay,
        "dimmed" => DisplayState.Dimmed,
        _ => DisplayState.Unknown
    };

    public bool IsStateVerified => ScreenStatusVerified;

    public DateTimeOffset LastSeenUtc => LastHeartbeat;
}

public sealed record DeviceUpdateRequest(string? Name, string? Note);

public sealed record BatchCommandRequest(IReadOnlyList<string> DeviceIds, string? Mode);

public sealed record CommandStartResponse(
    string RequestId,
    IReadOnlyList<CommandExecutionView> Executions);

public sealed record CommandExecutionView(
    string RequestId,
    string DeviceId,
    string DeviceName,
    string Command,
    string Stage,
    bool? Success,
    string? Message,
    string? ObservedState,
    bool ObservedStateVerified,
    DateTimeOffset UpdatedAt);

public sealed record OperationLogView(
    long Id,
    string? DeviceId,
    string DeviceName,
    string Command,
    bool Success,
    string Message,
    DateTimeOffset ExecuteTime);

public static class DeviceApiMapping
{
    public static string ToApiState(DisplayState state) => state switch
    {
        DisplayState.On => "on",
        DisplayState.Standby => "standby",
        DisplayState.BlackOverlay => "blackout",
        DisplayState.Dimmed => "dimmed",
        _ => "unknown"
    };

    public static string ToApiCommand(CommandKind kind) => kind switch
    {
        CommandKind.TurnOn => "screen_on",
        CommandKind.Standby => "screen_off_standby",
        CommandKind.ShowBlackOverlay => "screen_off_blackout",
        CommandKind.RemoveBlackOverlay => "screen_on",
        _ => "unknown"
    };
}
