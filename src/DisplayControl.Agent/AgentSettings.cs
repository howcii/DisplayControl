using System.Text.Json;

namespace DisplayControl.Agent;

/// <summary>
/// Windows Agent 的可持久化配置；文件始终位于 Agent 程序包目录。
/// </summary>
public sealed record AgentSettings(
    string ServerUrl,
    string DeviceId,
    string DeviceName,
    int HeartbeatSeconds,
    bool SimulationMode,
    bool UseBlackOverlay,
    bool UseMonitorStandby,
    bool PreventSystemSleep,
    int OverlayOperationTimeoutSeconds,
    int DisplayConfirmationTimeoutSeconds,
    int BlackPixelThreshold)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>当前 Agent 程序目录中的配置文件路径。</summary>
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json");

    /// <summary>读取配置；首次运行时创建一份可直接编辑的默认配置。</summary>
    public static AgentSettings Load()
    {
        var legacySettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        if (!File.Exists(SettingsPath) && File.Exists(legacySettingsPath))
        {
            File.Copy(legacySettingsPath, SettingsPath, overwrite: false);
        }

        if (!File.Exists(SettingsPath))
        {
            var defaults = CreateDefault();
            defaults.Save();
            return defaults;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var root = document.RootElement.TryGetProperty("Agent", out var agent)
            ? agent
            : document.RootElement;

        return new AgentSettings(
            ReadString(root, "ServerUrl", "http://127.0.0.1:8080"),
            ReadString(root, "DeviceId", "screen-01"),
            ReadString(root, "DeviceName", Environment.MachineName),
            ReadInt(root, "HeartbeatSeconds", 30),
            ReadBool(root, "SimulationMode", false),
            ReadBool(root, "UseBlackOverlay", true),
            ReadBool(root, "UseMonitorStandby", true),
            ReadBool(root, "PreventSystemSleep", true),
            ReadInt(root, "OverlayOperationTimeoutSeconds", 8),
            ReadInt(root, "DisplayConfirmationTimeoutSeconds", 8),
            ReadInt(root, "BlackPixelThreshold", 24))
            .Normalize();
    }

    /// <summary>把当前配置写回 Agent 程序目录。</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        using var stream = File.Create(SettingsPath);
        JsonSerializer.Serialize(stream, new AgentSettingsFile(this), JsonOptions);
    }

    /// <summary>清理文本并把可调数值限制在安全范围内。</summary>
    public AgentSettings Normalize()
    {
        var heartbeat = HeartbeatSeconds is < 5 or > 300 ? 30 : HeartbeatSeconds;
        var serverUrl = string.IsNullOrWhiteSpace(ServerUrl)
            ? "http://127.0.0.1:8080"
            : ServerUrl.Trim().TrimEnd('/');
        var deviceId = string.IsNullOrWhiteSpace(DeviceId)
            ? "screen-01"
            : DeviceId.Trim();
        var deviceName = string.IsNullOrWhiteSpace(DeviceName)
            ? Environment.MachineName
            : DeviceName.Trim();

        return this with
        {
            ServerUrl = serverUrl,
            DeviceId = deviceId,
            DeviceName = deviceName,
            HeartbeatSeconds = heartbeat,
            OverlayOperationTimeoutSeconds = Math.Clamp(OverlayOperationTimeoutSeconds, 3, 60),
            DisplayConfirmationTimeoutSeconds = Math.Clamp(DisplayConfirmationTimeoutSeconds, 3, 60),
            BlackPixelThreshold = Math.Clamp(BlackPixelThreshold, 0, 255)
        };
    }

    private static AgentSettings CreateDefault() =>
        new(
            "http://127.0.0.1:8080",
            "screen-01",
            Environment.MachineName,
            30,
            false,
            true,
            true,
            true,
            8,
            8,
            24);

    private static string ReadString(JsonElement root, string name, string defaultValue) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;

    private static int ReadInt(JsonElement root, string name, int defaultValue) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : defaultValue;

    private static bool ReadBool(JsonElement root, string name, bool defaultValue) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private sealed record AgentSettingsFile(AgentSettings Agent);
}
