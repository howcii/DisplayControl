using System.Collections.Concurrent;
using DisplayControl.Contracts;
using DisplayControl.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;

namespace DisplayControl.Server.Services;

/// <summary>
/// 将网页控制请求转换为逐设备命令，并聚合 Agent 回执、网页轮询结果和操作日志。
/// </summary>
public sealed class CommandCoordinator(
    DeviceRegistry devices,
    IHubContext<AgentHub> agents,
    OperationLogStore logs,
    IConfiguration configuration)
{
    private readonly ConcurrentDictionary<Guid, CommandStatus> _commands = new();
    private readonly ConcurrentDictionary<Guid, string> _requestByCommand = new();
    private readonly ConcurrentDictionary<Guid, byte> _loggedFinalCommands = new();
    private readonly ConcurrentDictionary<string, Guid> _activeCommandByDevice =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CommandExecutionView>> _byRequest =
        new(StringComparer.Ordinal);
    private readonly TimeSpan _executionTimeout = TimeSpan.FromSeconds(Math.Clamp(
        configuration.GetValue("CommandExecutionTimeoutSeconds", 20),
        5,
        600));
    private readonly TimeSpan _pollingExecutionTimeout = TimeSpan.FromSeconds(Math.Clamp(
        configuration.GetValue("AndroidPollingCommandTimeoutSeconds", 75),
        20,
        600));
    private readonly TimeSpan _terminalRetention = TimeSpan.FromHours(1);

    /// <summary>
    /// 使用协议枚举名称向在线设备批量下发命令，主要供内部兼容调用使用。
    /// </summary>
    public async Task<IReadOnlyList<CommandStatus>> DispatchBatchAsync(
        IEnumerable<string> deviceIds,
        string commandName,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CommandKind>(
                commandName,
                ignoreCase: true,
                out var kind))
        {
            throw new ArgumentException("Unsupported command.", nameof(commandName));
        }

        var statuses = new List<CommandStatus>();
        foreach (var deviceId in deviceIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!devices.TryGetSnapshot(deviceId, out var device) || !device.IsOnline)
            {
                continue;
            }

            var command = new DisplayCommand(
                Guid.NewGuid(),
                deviceId,
                kind,
                DateTimeOffset.UtcNow);
            var status = new CommandStatus(
                command,
                CommandStage.Dispatched,
                true,
                null,
                DateTimeOffset.UtcNow);
            _commands[command.CommandId] = status;
            statuses.Add(status);
            await agents.Clients
                .Group(DeviceRegistry.GroupName(deviceId))
                .SendAsync("ExecuteCommand", command, cancellationToken);
        }

        return statuses;
    }

    /// <summary>
    /// 接收网页的亮屏、息屏和批量请求，返回同一批操作的请求编号和逐设备初始状态。
    /// </summary>
    public async Task<CommandStartResponse> DispatchWebCommandAsync(
        IEnumerable<string> deviceIds,
        string commandName,
        string? mode,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var kind = ResolveWebCommand(commandName, mode);
        var executions = new ConcurrentDictionary<string, CommandExecutionView>(StringComparer.OrdinalIgnoreCase);
        _byRequest[requestId] = executions;

        foreach (var deviceId in deviceIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!devices.TryGetView(deviceId, out var device) || !device.Online)
            {
                continue;
            }

            var command = new DisplayCommand(
                Guid.NewGuid(),
                deviceId,
                kind,
                DateTimeOffset.UtcNow);
            if (!TryReserveDevice(deviceId, command.CommandId))
            {
                const string busyDetail = "设备正在执行上一条命令，请稍后再试。";
                executions[deviceId] = ToExecution(
                    requestId,
                    device,
                    command,
                    CommandStage.Failed,
                    false,
                    busyDetail);
                logs.Add(
                    device.Id,
                    device.Name,
                    DeviceApiMapping.ToApiCommand(kind),
                    false,
                    busyDetail);
                continue;
            }

            var status = new CommandStatus(
                command,
                CommandStage.Dispatched,
                true,
                "已发送",
                DateTimeOffset.UtcNow);
            _commands[command.CommandId] = status;
            _requestByCommand[command.CommandId] = requestId;
            executions[deviceId] = ToExecution(requestId, device, command, CommandStage.Dispatched, true, "已发送");

            try
            {
                await agents.Clients
                    .Group(DeviceRegistry.GroupName(deviceId))
                    .SendAsync("ExecuteCommand", command, cancellationToken);
            }
            catch (Exception ex)
            {
                var detail = $"命令下发失败：{ex.Message}";
                var failed = status with
                {
                    Stage = CommandStage.Failed,
                    Succeeded = false,
                    Detail = detail,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                _commands[command.CommandId] = failed;
                ReleaseActiveCommand(command);
                executions[deviceId] = ToExecution(
                    requestId,
                    device,
                    command,
                    CommandStage.Failed,
                    false,
                    detail);
                logs.Add(device.Id, device.Name, DeviceApiMapping.ToApiCommand(kind), false, detail);
                _requestByCommand.TryRemove(command.CommandId, out _);
            }
        }

        return new CommandStartResponse(requestId, executions.Values.ToArray());
    }

    /// <summary>
    /// 只接受来自设备当前连接、且与已下发命令匹配的回执。
    /// </summary>
    public void RecordReceipt(string connectionId, CommandReceipt receipt)
    {
        if (!devices.IsCurrentConnection(connectionId, receipt.DeviceId) ||
            !_commands.TryGetValue(receipt.CommandId, out var current) ||
            !string.Equals(
                current.Command.DeviceId,
                receipt.DeviceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Command receipt does not match its Agent.");
        }

        var effectiveDetail = FinalDetail(current.Command.Kind, receipt.Stage, receipt.Detail);
        _commands[receipt.CommandId] = current with
        {
            Stage = receipt.Stage,
            Succeeded = receipt.Succeeded,
            Detail = effectiveDetail,
            UpdatedAtUtc = receipt.OccurredAtUtc
        };

        if (TryResolveReportedState(
                current.Command.Kind,
                receipt.Stage,
                receipt.Succeeded,
                out var reportedState,
                out var reportedVerified))
        {
            devices.ApplyCommandObservedState(
                connectionId,
                receipt.DeviceId,
                reportedState,
                reportedVerified,
                effectiveDetail,
                receipt.OccurredAtUtc);
        }

        if (_requestByCommand.TryGetValue(receipt.CommandId, out var requestId))
        {
            if (devices.TryGetView(receipt.DeviceId, out var device))
            {
                var executions = _byRequest.GetOrAdd(
                    requestId,
                    _ => new ConcurrentDictionary<string, CommandExecutionView>(StringComparer.OrdinalIgnoreCase));
                executions[receipt.DeviceId] = ToExecution(
                    requestId,
                    device,
                    current.Command,
                    receipt.Stage,
                    receipt.Succeeded,
                    effectiveDetail);

                if (IsFinalForCommand(current.Command.Kind, receipt.Stage, receipt.Succeeded))
                {
                    ReleaseActiveCommand(current.Command);
                    if (_loggedFinalCommands.TryAdd(receipt.CommandId, 0))
                    {
                        logs.Add(
                            receipt.DeviceId,
                            device.Name,
                            DeviceApiMapping.ToApiCommand(current.Command.Kind),
                            receipt.Succeeded,
                            effectiveDetail ?? (receipt.Succeeded ? "执行完成" : "执行或验证失败"));
                    }
                }
            }

            if (IsTerminal(receipt.Stage))
            {
                _requestByCommand.TryRemove(receipt.CommandId, out _);
            }
        }
    }

    /// <summary>按命令编号读取最新阶段。</summary>
    public bool TryGet(Guid commandId, out CommandStatus status) =>
        _commands.TryGetValue(commandId, out status!);

    /// <summary>按网页请求编号读取该批操作的逐设备结果。</summary>
    public IReadOnlyList<CommandExecutionView> GetExecutions(string requestId) =>
        _byRequest.TryGetValue(requestId, out var executions)
            ? executions.Values.OrderBy(execution => execution.DeviceName, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    /// <summary>
    /// 供 Android V1.5 HTTP 轮询兼容接口读取该设备尚未完成的网页命令。
    /// 旧 Android APK 不使用 SignalR，只能通过轮询拿命令。
    /// </summary>
    public bool TryGetPollingCommand(string deviceId, out CommandStatus status)
    {
        status = _commands.Values
            .Where(current =>
                string.Equals(current.Command.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                !IsFinalForCommand(current.Command.Kind, current.Stage, current.Succeeded))
            .OrderBy(current => current.Command.CreatedAtUtc)
            .FirstOrDefault()!;
        return status is not null;
    }

    /// <summary>
    /// 由后台定时任务调用，把未按时回执的命令收束为超时，并清理保留期以外的旧状态。
    /// </summary>
    public void RunMaintenance()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _commands.ToArray())
        {
            var current = pair.Value;
            if (IsFinalForCommand(current.Command.Kind, current.Stage, current.Succeeded))
            {
                ReleaseActiveCommand(current.Command);
                if (now - current.UpdatedAtUtc > _terminalRetention)
                {
                    _commands.TryRemove(pair.Key, out _);
                    _requestByCommand.TryRemove(pair.Key, out _);
                    _loggedFinalCommands.TryRemove(pair.Key, out _);
                }

                continue;
            }

            if (now - current.Command.CreatedAtUtc <= TimeoutFor(current.Command.DeviceId))
            {
                continue;
            }

            var detail = "命令执行超时：Agent 未在限定时间内返回确认。";
            var timedOut = current with
            {
                Stage = CommandStage.TimedOut,
                Succeeded = false,
                Detail = detail,
                UpdatedAtUtc = now
            };
            if (!_commands.TryUpdate(pair.Key, timedOut, current))
            {
                continue;
            }

            ReleaseActiveCommand(timedOut.Command);
            if (_requestByCommand.TryRemove(pair.Key, out var requestId))
            {
                UpdateRequestExecution(requestId, timedOut, detail, now);
            }
        }

        foreach (var pair in _byRequest.ToArray())
        {
            var values = pair.Value.Values.ToArray();
            if (values.Length > 0 &&
                values.All(IsFinalExecution) &&
                values.All(value => now - value.UpdatedAt > _terminalRetention))
            {
                _byRequest.TryRemove(pair.Key, out _);
            }
        }
    }

    private static CommandKind ResolveWebCommand(string commandName, string? mode)
    {
        if (string.Equals(commandName, "on", StringComparison.OrdinalIgnoreCase))
        {
            return CommandKind.TurnOn;
        }

        if (string.Equals(commandName, "off", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(mode, "blackout", StringComparison.OrdinalIgnoreCase)
                ? CommandKind.ShowBlackOverlay
                : CommandKind.Standby;
        }

        if (Enum.TryParse<CommandKind>(commandName, ignoreCase: true, out var kind))
        {
            return kind;
        }

        throw new ArgumentException("Unsupported command.", nameof(commandName));
    }

    private static CommandExecutionView ToExecution(
        string requestId,
        DeviceView device,
        DisplayCommand command,
        CommandStage stage,
        bool? success,
        string? message) =>
        new(
            requestId,
            device.Id,
            device.Name,
            DeviceApiMapping.ToApiCommand(command.Kind),
            stage.ToString().ToLowerInvariant(),
            success,
            message,
            device.ScreenStatus,
            device.ScreenStatusVerified,
            DateTimeOffset.UtcNow);

    private void UpdateRequestExecution(
        string requestId,
        CommandStatus status,
        string detail,
        DateTimeOffset updatedAt)
    {
        var executions = _byRequest.GetOrAdd(
            requestId,
            _ => new ConcurrentDictionary<string, CommandExecutionView>(StringComparer.OrdinalIgnoreCase));
        if (executions.TryGetValue(status.Command.DeviceId, out var existing))
        {
            executions[status.Command.DeviceId] = existing with
            {
                Stage = CommandStage.TimedOut.ToString().ToLowerInvariant(),
                Success = false,
                Message = detail,
                UpdatedAt = updatedAt
            };
            logs.Add(existing.DeviceId, existing.DeviceName, existing.Command, false, detail);
            return;
        }

        var deviceName = status.Command.DeviceId;
        var observedState = DeviceApiMapping.ToApiState(DisplayState.Unknown);
        var observedVerified = false;
        if (devices.TryGetView(status.Command.DeviceId, out var device))
        {
            deviceName = device.Name;
            observedState = device.ScreenStatus;
            observedVerified = device.ScreenStatusVerified;
        }

        executions[status.Command.DeviceId] = new CommandExecutionView(
            requestId,
            status.Command.DeviceId,
            deviceName,
            DeviceApiMapping.ToApiCommand(status.Command.Kind),
            CommandStage.TimedOut.ToString().ToLowerInvariant(),
            false,
            detail,
            observedState,
            observedVerified,
            updatedAt);
        logs.Add(status.Command.DeviceId, deviceName, DeviceApiMapping.ToApiCommand(status.Command.Kind), false, detail);
    }

    private static bool IsTerminal(CommandStage stage) =>
        stage is CommandStage.Verified or CommandStage.Failed or CommandStage.TimedOut;

    private TimeSpan TimeoutFor(string deviceId) =>
        devices.TryGetView(deviceId, out var device) && IsPollingAgent(device)
            ? _pollingExecutionTimeout
            : _executionTimeout;

    private static bool IsPollingAgent(DeviceView device) =>
        device.Id.StartsWith("android-", StringComparison.OrdinalIgnoreCase) ||
        device.AgentVersion.Contains("Android", StringComparison.OrdinalIgnoreCase);

    private bool TryReserveDevice(string deviceId, Guid commandId)
    {
        while (true)
        {
            if (_activeCommandByDevice.TryGetValue(deviceId, out var existingCommandId))
            {
                if (_commands.TryGetValue(existingCommandId, out var existing) &&
                    !IsFinalForCommand(existing.Command.Kind, existing.Stage, existing.Succeeded))
                {
                    return false;
                }

                _activeCommandByDevice.TryRemove(deviceId, out _);
                continue;
            }

            if (_activeCommandByDevice.TryAdd(deviceId, commandId))
            {
                return true;
            }
        }
    }

    private void ReleaseActiveCommand(DisplayCommand command)
    {
        if (_activeCommandByDevice.TryGetValue(command.DeviceId, out var activeCommandId) &&
            activeCommandId == command.CommandId)
        {
            _activeCommandByDevice.TryRemove(command.DeviceId, out _);
        }
    }

    private static bool IsFinalForCommand(CommandKind kind, CommandStage stage, bool succeeded) =>
        IsTerminal(stage) ||
        (succeeded && stage is CommandStage.Executed && IsExecutedEnoughForWeb(kind));

    private static string? FinalDetail(CommandKind kind, CommandStage stage, string? detail)
    {
        if (stage is not CommandStage.Executed || !IsWindowsPowerCommand(kind))
        {
            return detail;
        }

        const string refreshNote = "状态已按 Agent 回执即时刷新，现场人工操作优先。";
        if (string.IsNullOrWhiteSpace(detail))
        {
            return refreshNote;
        }

        return detail.Contains(refreshNote, StringComparison.Ordinal)
            ? detail
            : $"{detail}（{refreshNote}）";
    }

    private static bool IsWindowsPowerCommand(CommandKind kind) =>
        kind is CommandKind.TurnOn or CommandKind.Standby;

    private static bool IsExecutedEnoughForWeb(CommandKind kind) =>
        kind is CommandKind.TurnOn
            or CommandKind.Standby
            or CommandKind.ShowBlackOverlay
            or CommandKind.RemoveBlackOverlay;

    private static bool TryResolveReportedState(
        CommandKind kind,
        CommandStage stage,
        bool succeeded,
        out DisplayState state,
        out bool verified)
    {
        state = DisplayState.Unknown;
        verified = false;
        if (!succeeded || stage is not (CommandStage.Executed or CommandStage.Verified))
        {
            return false;
        }

        state = kind switch
        {
            CommandKind.TurnOn => DisplayState.On,
            CommandKind.Standby => DisplayState.Standby,
            CommandKind.ShowBlackOverlay => DisplayState.BlackOverlay,
            CommandKind.RemoveBlackOverlay => DisplayState.On,
            _ => DisplayState.Unknown
        };
        verified = stage is CommandStage.Verified;
        return state is not DisplayState.Unknown;
    }

    private static bool IsTerminal(string stage) =>
        string.Equals(stage, CommandStage.Verified.ToString(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stage, CommandStage.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stage, CommandStage.TimedOut.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsFinalExecution(CommandExecutionView execution) =>
        IsTerminal(execution.Stage) ||
        (execution.Success is true &&
            string.Equals(execution.Stage, CommandStage.Executed.ToString(), StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(execution.Command, DeviceApiMapping.ToApiCommand(CommandKind.TurnOn), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(execution.Command, DeviceApiMapping.ToApiCommand(CommandKind.Standby), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(execution.Command, DeviceApiMapping.ToApiCommand(CommandKind.ShowBlackOverlay), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Server 内部保存的一次命令及其最新生命周期状态。</summary>
public sealed record CommandStatus(
    DisplayCommand Command,
    CommandStage Stage,
    bool Succeeded,
    string? Detail,
    DateTimeOffset UpdatedAtUtc);
