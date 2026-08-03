using DisplayControl.Contracts;
using DisplayControl.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace DisplayControl.Server.Hubs;

/// <summary>
/// 接收 Agent 注册、心跳和命令回执，并把变化实时广播给已登录网页。
/// </summary>
public sealed partial class AgentHub(
    DeviceRegistry registry,
    CommandCoordinator commands,
    IHubContext<DashboardHub> dashboard,
    ILogger<AgentHub> logger) : Hub
{
    /// <summary>校验并登记设备，将当前连接加入该设备专用命令组。</summary>
    public async Task Register(AgentRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.DeviceId) ||
            registration.DeviceId.Length > 128 ||
            !Protocol.IsSupported(registration.ProtocolVersion))
        {
            throw new HubException("Unsupported or invalid Agent registration.");
        }

        registry.Register(
            Context.ConnectionId,
            registration,
            Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString());
        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            DeviceRegistry.GroupName(registration.DeviceId));
        await dashboard.Clients.All.SendAsync("DeviceChanged", registry.GetRequiredView(registration.DeviceId));
        await dashboard.Clients.All.SendAsync("DevicesChanged");

        LogAgentRegistered(
            logger,
            registration.DeviceId,
            registration.AgentVersion,
            registration.ProtocolVersion);
    }

    /// <summary>更新设备的实际观测状态并通知网页刷新。</summary>
    public async Task Heartbeat(AgentHeartbeat heartbeat)
    {
        var snapshot = registry.UpdateHeartbeat(Context.ConnectionId, heartbeat);
        await dashboard.Clients.All.SendAsync("DeviceChanged", snapshot);
        await dashboard.Clients.All.SendAsync("DevicesChanged");
    }

    /// <summary>保存一次命令的最新阶段并通知网页。</summary>
    public async Task ReportCommand(CommandReceipt receipt)
    {
        commands.RecordReceipt(Context.ConnectionId, receipt);
        if (registry.TryGetView(receipt.DeviceId, out var device))
        {
            await dashboard.Clients.All.SendAsync("DeviceChanged", device);
            await dashboard.Clients.All.SendAsync("DevicesChanged");
        }

        await dashboard.Clients.All.SendAsync("CommandChanged", receipt);
        await dashboard.Clients.All.SendAsync("CommandCompleted", receipt);
    }

    /// <summary>连接结束时仅将仍绑定该连接的设备标记为离线。</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var snapshot = registry.Disconnect(Context.ConnectionId);
        if (snapshot is not null)
        {
            await dashboard.Clients.All.SendAsync("DeviceChanged", snapshot);
            await dashboard.Clients.All.SendAsync("DevicesChanged");
        }

        await base.OnDisconnectedAsync(exception);
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Agent {DeviceId} registered with version {AgentVersion}, protocol {ProtocolVersion}")]
    private static partial void LogAgentRegistered(
        ILogger logger,
        string deviceId,
        string agentVersion,
        int protocolVersion);
}
