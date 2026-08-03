namespace DisplayControl.Contracts;

/// <summary>
/// 定义 Server 与 Agent 共同遵守的协议版本边界。
/// </summary>
public static class Protocol
{
    /// <summary>当前 Windows Agent V16 使用的协议版本。</summary>
    public const int CurrentVersion = 3;

    /// <summary>迁移期允许接入的 Windows Agent V15 协议版本。</summary>
    public const int LegacyV15Version = 2;

    /// <summary>判断指定协议版本是否允许连接当前 Server。</summary>
    public static bool IsSupported(int version) =>
        version is LegacyV15Version or CurrentVersion;
}

/// <summary>
/// Agent 实际观测到的显示状态；Unknown 表示没有足够证据判断。
/// </summary>
public enum DisplayState
{
    Unknown = 0,
    On = 1,
    Standby = 2,
    BlackOverlay = 3,
    Dimmed = 4
}

/// <summary>Server 可以发给显示设备的控制命令。</summary>
public enum CommandKind
{
    TurnOn = 1,
    Standby = 2,
    ShowBlackOverlay = 3,
    RemoveBlackOverlay = 4
}

/// <summary>
/// 命令生命周期；只有 Verified 才表示执行结果经过独立状态观测确认。
/// TurnOn 与 Standby 的 Executed 表示 Windows 已接受电源指令，但不代表物理亮度已验证。
/// </summary>
public enum CommandStage
{
    Created = 0,
    Dispatched = 1,
    Accepted = 2,
    Executed = 3,
    Verified = 4,
    Failed = 5,
    TimedOut = 6
}

/// <summary>Agent 建立连接后提交的设备身份与版本信息。</summary>
public sealed record AgentRegistration(
    string DeviceId,
    string DeviceName,
    string AgentVersion,
    int ProtocolVersion,
    string? RegistrationToken = null);

/// <summary>Agent 周期上报的连接存活信息与显示状态观测。</summary>
public sealed record AgentHeartbeat(
    string DeviceId,
    DateTimeOffset ObservedAtUtc,
    DisplayState State,
    bool IsStateVerified,
    string? Detail = null);

/// <summary>Server 发给指定设备的一次唯一控制命令。</summary>
public sealed record DisplayCommand(
    Guid CommandId,
    string DeviceId,
    CommandKind Kind,
    DateTimeOffset CreatedAtUtc);

/// <summary>Agent 对命令接收、执行或验证阶段的回执。</summary>
public sealed record CommandReceipt(
    Guid CommandId,
    string DeviceId,
    CommandStage Stage,
    DateTimeOffset OccurredAtUtc,
    bool Succeeded,
    string? Detail = null);

/// <summary>供核心逻辑使用的不可变设备状态快照。</summary>
public sealed record DeviceSnapshot(
    string DeviceId,
    string DeviceName,
    bool IsOnline,
    DisplayState State,
    bool IsStateVerified,
    string AgentVersion,
    int ProtocolVersion,
    DateTimeOffset LastSeenUtc);
