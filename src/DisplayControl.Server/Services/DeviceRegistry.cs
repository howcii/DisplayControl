using System.Collections.Concurrent;
using System.Text.Json;
using DisplayControl.Contracts;
using DisplayControl.Server;

namespace DisplayControl.Server.Services;

/// <summary>
/// 维护设备与当前 SignalR 连接的对应关系，并持久化可恢复的设备资料和最后状态。
/// </summary>
public sealed class DeviceRegistry(IConfiguration configuration)
{
    private readonly ConcurrentDictionary<string, DeviceEntry> _byDevice =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _deviceByConnection =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private bool _loaded;

    /// <summary>供独立核心测试使用，数据写入每次测试专用的临时目录。</summary>
    public DeviceRegistry()
        : this(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = Path.Combine(Path.GetTempPath(), "DisplayControlTests", Guid.NewGuid().ToString("N"))
            })
            .Build())
    {
    }

    private string DataPath => StoragePaths.ResolveDataPath(configuration);

    private string DevicesPath => Path.Combine(DataPath, "devices.json");

    /// <summary>生成只接收指定设备命令的 SignalR 组名。</summary>
    public static string GroupName(string deviceId) => $"device:{deviceId}";

    /// <summary>
    /// 登记 Agent 当前连接；管理员锁定过的设备名称不会被后续注册信息覆盖。
    /// </summary>
    public void Register(string connectionId, AgentRegistration registration, string? remoteIp = null)
    {
        EnsureLoaded();
        var now = DateTimeOffset.UtcNow;
        _byDevice.AddOrUpdate(
            registration.DeviceId,
            _ => new DeviceEntry(
                registration.DeviceId,
                registration.DeviceName,
                null,
                remoteIp ?? "unknown",
                connectionId,
                true,
                DisplayState.Unknown,
                false,
                "legacy-unverified",
                null,
                null,
                registration.AgentVersion,
                registration.ProtocolVersion,
                now,
                false),
            (_, existing) => existing with
            {
                DeviceName = existing.NameLocked ? existing.DeviceName : registration.DeviceName,
                Ip = remoteIp ?? existing.Ip,
                ConnectionId = connectionId,
                IsOnline = true,
                AgentVersion = registration.AgentVersion,
                ProtocolVersion = registration.ProtocolVersion,
                LastSeenUtc = now
            });
        _deviceByConnection[connectionId] = registration.DeviceId;
        Save();
    }

    /// <summary>
    /// 保存 Agent 心跳，并拒绝设备编号与当前连接绑定关系不一致的上报。
    /// </summary>
    public DeviceView UpdateHeartbeat(string connectionId, AgentHeartbeat heartbeat)
    {
        EnsureLoaded();
        if (!_deviceByConnection.TryGetValue(connectionId, out var deviceId) ||
            !string.Equals(deviceId, heartbeat.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Heartbeat was received before a matching registration.");
        }

        _byDevice.AddOrUpdate(
            deviceId,
            _ => throw new InvalidOperationException("Registered device was not found."),
            (_, existing) => existing with
            {
                IsOnline = true,
                State = heartbeat.State,
                IsStateVerified = heartbeat.IsStateVerified,
                ScreenStatusSource = heartbeat.IsStateVerified
                    ? SourceFor(heartbeat.State)
                    : "agent-reported-unverified",
                ScreenStatusDetails = heartbeat.Detail,
                ScreenStatusObservedAt = heartbeat.ObservedAtUtc,
                LastSeenUtc = heartbeat.ObservedAtUtc
            });

        Save();
        return GetRequiredView(deviceId);
    }

    /// <summary>
    /// 只在断开的连接仍是设备当前连接时标记离线，避免旧连接覆盖重连后的新状态。
    /// </summary>
    public DeviceView? Disconnect(string connectionId)
    {
        EnsureLoaded();
        if (!_deviceByConnection.TryRemove(connectionId, out var deviceId) ||
            !_byDevice.TryGetValue(deviceId, out var entry) ||
            entry.ConnectionId != connectionId)
        {
            return null;
        }

        _byDevice[deviceId] = entry with
        {
            IsOnline = false,
            IsStateVerified = false,
            ScreenStatusSource = "agent-offline"
        };
        Save();
        return GetRequiredView(deviceId);
    }

    public IReadOnlyList<DeviceSnapshot> GetSnapshots() =>
        GetEntries()
            .OrderBy(entry => entry.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(ToSnapshot)
            .ToArray();

    public IReadOnlyList<DeviceView> GetViews() =>
        GetEntries()
            .OrderBy(entry => entry.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(ToView)
            .ToArray();

    public bool TryGetSnapshot(string deviceId, out DeviceSnapshot snapshot)
    {
        EnsureLoaded();
        if (_byDevice.TryGetValue(deviceId, out var entry))
        {
            snapshot = ToSnapshot(entry);
            return true;
        }

        snapshot = default!;
        return false;
    }

    public bool TryGetView(string deviceId, out DeviceView view)
    {
        EnsureLoaded();
        if (_byDevice.TryGetValue(deviceId, out var entry))
        {
            view = ToView(entry);
            return true;
        }

        view = default!;
        return false;
    }

    public DeviceView GetRequiredView(string deviceId) =>
        TryGetView(deviceId, out var view)
            ? view
            : throw new KeyNotFoundException($"Device '{deviceId}' was not found.");

    public DeviceSnapshot GetRequiredSnapshot(string deviceId) =>
        TryGetSnapshot(deviceId, out var snapshot)
            ? snapshot
            : throw new KeyNotFoundException($"Device '{deviceId}' was not found.");

    public bool IsCurrentConnection(string connectionId, string deviceId)
    {
        EnsureLoaded();
        return _byDevice.TryGetValue(deviceId, out var entry) &&
            entry.ConnectionId == connectionId;
    }

    /// <summary>
    /// 命令回执来自 Agent 当前连接时，用回执内容立即刷新“Agent 已上报”的屏幕状态。
    /// 这不是物理亮度验证，只用于避免命令回执已到但下一次心跳尚未到达时网页仍显示旧状态。
    /// </summary>
    public bool ApplyCommandObservedState(
        string connectionId,
        string deviceId,
        DisplayState state,
        bool verified,
        string? detail,
        DateTimeOffset observedAtUtc)
    {
        EnsureLoaded();
        if (!_byDevice.TryGetValue(deviceId, out var existing) ||
            existing.ConnectionId != connectionId)
        {
            return false;
        }

        _byDevice[deviceId] = existing with
        {
            IsOnline = true,
            State = state,
            IsStateVerified = verified,
            ScreenStatusSource = verified
                ? SourceFor(state)
                : "agent-command-reported-unverified",
            ScreenStatusDetails = detail,
            ScreenStatusObservedAt = observedAtUtc,
            LastSeenUtc = observedAtUtc
        };
        Save();
        return true;
    }

    /// <summary>更新管理员可编辑的设备名称和备注。</summary>
    public DeviceView UpdateDevice(string deviceId, DeviceUpdateRequest request)
    {
        EnsureLoaded();
        if (!_byDevice.TryGetValue(deviceId, out var existing))
        {
            throw new KeyNotFoundException($"Device '{deviceId}' was not found.");
        }

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? existing.DeviceName
            : request.Name.Trim();
        var note = string.IsNullOrWhiteSpace(request.Note)
            ? null
            : request.Note.Trim();
        _byDevice[deviceId] = existing with
        {
            DeviceName = name,
            Note = note,
            NameLocked = true
        };
        Save();
        return GetRequiredView(deviceId);
    }

    /// <summary>删除持久化设备资料。</summary>
    public bool DeleteDevice(string deviceId)
    {
        EnsureLoaded();
        var removed = _byDevice.TryRemove(deviceId, out var entry);
        if (removed && entry is not null)
        {
            if (!string.IsNullOrEmpty(entry.ConnectionId) &&
                _deviceByConnection.TryGetValue(entry.ConnectionId, out var mappedDeviceId) &&
                string.Equals(mappedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                _deviceByConnection.TryRemove(entry.ConnectionId, out _);
            }

            Save();
        }

        return removed;
    }

    private DeviceEntry[] GetEntries()
    {
        EnsureLoaded();
        return _byDevice.Values.ToArray();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            Directory.CreateDirectory(DataPath);
            if (File.Exists(DevicesPath))
            {
                using var stream = File.OpenRead(DevicesPath);
                var entries = JsonSerializer.Deserialize<DeviceEntry[]>(stream, _jsonOptions) ?? [];
                foreach (var entry in entries)
                {
                    // 进程重启后没有任何旧连接仍然有效，持久化设备一律先恢复为离线。
                    _byDevice[entry.DeviceId] = entry with
                    {
                        ConnectionId = "",
                        IsOnline = false,
                        IsStateVerified = false,
                        ScreenStatusSource = "agent-offline"
                    };
                }
            }

            _loaded = true;
        }
    }

    private void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DataPath);
            var entries = _byDevice.Values
                .OrderBy(entry => entry.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // 临时文件与正式文件位于同一目录，完成序列化后再进行原子替换。
            var tempPath = DevicesPath + ".tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, entries, _jsonOptions);
            }

            if (File.Exists(DevicesPath))
            {
                File.Replace(tempPath, DevicesPath, null);
            }
            else
            {
                File.Move(tempPath, DevicesPath);
            }
        }
    }

    private static string SourceFor(DisplayState state) => state switch
    {
        DisplayState.BlackOverlay => "overlay_window_and_pixels",
        DisplayState.On => "overlay_closed_and_windows_power",
        DisplayState.Standby => "windows_power_event",
        DisplayState.Dimmed => "windows_power_event",
        _ => "legacy-unverified"
    };

    private static DeviceSnapshot ToSnapshot(DeviceEntry entry) =>
        new(
            entry.DeviceId,
            entry.DeviceName,
            entry.IsOnline,
            entry.State,
            entry.IsStateVerified,
            entry.AgentVersion,
            entry.ProtocolVersion,
            entry.LastSeenUtc);

    private static DeviceView ToView(DeviceEntry entry) =>
        new(
            entry.DeviceId,
            entry.DeviceName,
            entry.Note,
            entry.Ip,
            entry.IsOnline,
            DeviceApiMapping.ToApiState(entry.State),
            entry.IsStateVerified,
            entry.ScreenStatusSource,
            entry.ScreenStatusDetails,
            entry.ScreenStatusObservedAt,
            entry.AgentVersion,
            entry.ProtocolVersion,
            entry.LastSeenUtc,
            entry.NameLocked);

    private sealed record DeviceEntry(
        string DeviceId,
        string DeviceName,
        string? Note,
        string Ip,
        string ConnectionId,
        bool IsOnline,
        DisplayState State,
        bool IsStateVerified,
        string ScreenStatusSource,
        string? ScreenStatusDetails,
        DateTimeOffset? ScreenStatusObservedAt,
        string AgentVersion,
        int ProtocolVersion,
        DateTimeOffset LastSeenUtc,
        bool NameLocked);
}
