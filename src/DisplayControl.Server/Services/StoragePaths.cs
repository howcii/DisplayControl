namespace DisplayControl.Server.Services;

/// <summary>统一解析 Server 的持久化数据目录。</summary>
public static class StoragePaths
{
    private const string DataProtectionDirectoryName = "data-protection-keys";

    /// <summary>
    /// 优先使用配置路径；相对路径始终以程序包目录为基准，避免写入用户目录。
    /// </summary>
    public static string ResolveDataPath(IConfiguration configuration)
    {
        var configured = configuration["Storage:DataPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured),
                AppContext.BaseDirectory);
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /// <summary>Cookie 签名密钥与业务数据一起保存在程序包的数据目录。</summary>
    public static string ResolveDataProtectionKeyPath(IConfiguration configuration) =>
        Path.Combine(ResolveDataPath(configuration), DataProtectionDirectoryName);
}
