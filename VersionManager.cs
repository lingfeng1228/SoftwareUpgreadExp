using System.IO;
using System.Text.Json;

namespace ClientUpdate;

/// <summary>
/// 版本配置数据
/// 只存储客户端版本，升级端版本从程序集读取
/// </summary>
public class VersionConfig
{
    public string ClientVersion { get; set; } = "1.0.0.0";
}

/// <summary>
/// 版本管理器
/// Client（主程序/模块）：版本从配置文件读取，支持模块化更新
/// Upgrade（升级程序）：版本从程序集读取，保持独立性
/// </summary>
public static class VersionManager
{
    private static readonly string VersionConfigFilePath = 
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version-config.json");

    /// <summary>
    /// 获取客户端版本（从配置文件读取）
    /// 用于 ClientUpdate 模块，支持模块化更新场景
    /// </summary>
    public static string GetClientVersion()
    {
        var config = GetVersionConfig();
        return config.ClientVersion;
    }

    /// <summary>
    /// 获取升级端版本（从 Upgrade.exe 程序集读取）
    /// 读取同级目录下 Upgrade.exe 的文件版本
    /// </summary>
    public static string GetUpgradeClientVersion()
    {
        try
        {
            var upgradeExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Upgrade.exe");
            if (File.Exists(upgradeExePath))
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(upgradeExePath);
                if (!string.IsNullOrEmpty(versionInfo.FileVersion))
                {
                    return versionInfo.FileVersion;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取 Upgrade.exe 版本失败: {ex.Message}");
        }
        
        // 如果读取失败，返回默认版本
        return "1.0.0.0";
    }

    /// <summary>
    /// 设置客户端版本
    /// 在更新完成后调用，更新 version-config.json 中的 ClientVersion
    /// </summary>
    public static void SetClientVersion(string version)
    {
        try
        {
            var config = GetVersionConfig();
            config.ClientVersion = version;
            SaveVersionConfig(config);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"写入客户端版本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从程序集获取版本号
    /// </summary>
    private static string GetAssemblyVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version?.ToString() ?? "1.0.0.0";
    }

    /// <summary>
    /// 读取版本配置
    /// 从 version-config.json 文件中读取客户端版本
    /// </summary>
    public static VersionConfig GetVersionConfig()
    {
        try
        {
            if (File.Exists(VersionConfigFilePath))
            {
                var json = File.ReadAllText(VersionConfigFilePath);
                var config = JsonSerializer.Deserialize<VersionConfig>(json);
                if (config != null)
                {
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取版本配置文件失败: {ex.Message}");
        }
        
        // 如果配置文件不存在或读取失败，返回默认配置
        return new VersionConfig();
    }

    /// <summary>
    /// 保存版本配置
    /// </summary>
    public static void SaveVersionConfig(VersionConfig config)
    {
        try
        {
            // 确保目录存在
            var directory = Path.GetDirectoryName(VersionConfigFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            var json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(VersionConfigFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"写入版本配置文件失败: {ex.Message}");
        }
    }
}
