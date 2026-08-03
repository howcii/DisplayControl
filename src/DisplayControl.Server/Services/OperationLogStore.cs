using System.Text.Json;
using DisplayControl.Server;

namespace DisplayControl.Server.Services;

/// <summary>
/// 在 Server 数据目录中保存有限数量的设备操作日志。
/// </summary>
public sealed class OperationLogStore(IConfiguration configuration)
{
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private string DataPath => StoragePaths.ResolveDataPath(configuration);

    private string LogsPath => Path.Combine(DataPath, "operation-logs.json");

    /// <summary>按时间倒序读取最近的日志，单次最多返回 1000 条。</summary>
    public IReadOnlyList<OperationLogView> GetRecent(int limit)
    {
        lock (_sync)
        {
            return Load()
                .OrderByDescending(log => log.ExecuteTime)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray();
        }
    }

    /// <summary>追加一条日志，并把本地历史上限控制在 2000 条。</summary>
    public void Add(string? deviceId, string deviceName, string command, bool success, string message)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DataPath);
            var logs = Load().ToList();
            var id = logs.Count == 0 ? 1 : logs.Max(log => log.Id) + 1;
            logs.Add(new OperationLogView(
                id,
                deviceId,
                deviceName,
                command,
                success,
                message,
                DateTimeOffset.UtcNow));
            while (logs.Count > 2_000)
            {
                logs.RemoveAt(0);
            }

            Save(logs);
        }
    }

    private OperationLogView[] Load()
    {
        if (!File.Exists(LogsPath))
        {
            return [];
        }

        using var stream = File.OpenRead(LogsPath);
        return JsonSerializer.Deserialize<OperationLogView[]>(stream, _jsonOptions) ?? [];
    }

    private void Save(IReadOnlyList<OperationLogView> logs)
    {
        // 先完整写入临时文件再替换正式文件，避免中断时留下半个 JSON。
        var tempPath = LogsPath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, logs, _jsonOptions);
        }

        if (File.Exists(LogsPath))
        {
            File.Replace(tempPath, LogsPath, null);
        }
        else
        {
            File.Move(tempPath, LogsPath);
        }
    }
}
