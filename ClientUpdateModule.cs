using GeneralUpdate.ClientCore;
using GeneralUpdate.Common.Download;
using GeneralUpdate.Common.Internal;
using GeneralUpdate.Common.Internal.Bootstrap;
using GeneralUpdate.Common.Shared.Object;
using Prism.Ioc;
using Prism.Modularity;
using PrismModulerTest.Core.Logging;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ClientUpdate
{
    /// <summary>
    /// 更新结果上报 DTO
    /// 客户端完成更新后向服务端上报结果
    /// </summary>
    public class ReportDTO
    {
        /// <summary>
        /// 版本记录 ID（对应 VerificationResultDTO.RecordId）
        /// </summary>
        public int RecordId { get; set; }

        /// <summary>
        /// 更新状态
        /// 1=成功, 2=失败, 3=取消
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// 更新类型
        /// 1=手动升级, 2=静默推送
        /// </summary>
        public int Type { get; set; }

        /// <summary>
        /// 客户端版本号（更新前的版本）
        /// </summary>
        public string? ClientVersion { get; set; }

        /// <summary>
        /// 目标版本号（更新后的版本）
        /// </summary>
        public string? TargetVersion { get; set; }

        /// <summary>
        /// 错误信息（如果更新失败）
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 客户端机器唯一标识（用于统计活跃用户数）
        /// </summary>
        public string? MachineId { get; set; }

        /// <summary>
        /// 客户端 IP 地址
        /// </summary>
        public string? ClientIp { get; set; }
    }
    /// <summary>
    /// 客户端更新模块 - Prism模块实现
    /// </summary>
    public class ClientUpdateModule : IModule
    {
        private IMyLogServiceFactory? _logFactory;
        private Configinfo? _configinfo;
        private readonly HttpClient _httpClient;

        public ClientUpdateModule()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);// 设置合理的超时时间，避免上报时长时间挂起
        }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 注册更新服务
            // containerRegistry.RegisterSingleton<IUpdateService, UpdateService>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            // 获取日志工厂
            _logFactory = containerProvider.Resolve<IMyLogServiceFactory>();
            var log = _logFactory.CreateLogService<ClientUpdateModule>();

            try
            {
                log.Info("ClientUpdateModule 初始化开始");
                Console.WriteLine($"[ClientUpdate] 模块初始化，{DateTime.Now}");
                Console.WriteLine($"[ClientUpdate] 当前目录: {Environment.CurrentDirectory}");

                // 配置更新参数
                _configinfo = new Configinfo
                {
                    // 更新验证API地址
                    UpdateUrl = "http://localhost:5000/Upgrade/Verification",
                    // 更新报告API地址
                    ReportUrl = "http://localhost:5000/Upgrade/Report",
                    // 主程序应用名称（升级完成后要启动的程序）
                    MainAppName = "PrismModulerTest.exe",
                    // 升级程序名称（关闭主程序后要启动的升级助手）
                    AppName = "Upgrade.exe",
                    // 当前客户端版本（从 version-config.json 读取，可独立配置）
                    ClientVersion = VersionManager.GetClientVersion(),
                    // 升级端版本直接获取程序集版本
                    UpgradeClientVersion = VersionManager.GetUpgradeClientVersion(),
                    // 安装路径
                    InstallPath = Thread.GetDomain().BaseDirectory,
                    // 黑名单配置 - 这些文件不会被更新覆盖
                    BlackFiles = new List<string> { "appsettings.json", "user.config" },
                    BlackFormats = new List<string> { ".log", ".tmp", ".lock" },
                    // 跳过目录
                    SkipDirectorys = new List<string> { "Logs", "Data", "Backups" },
                    // 产品 ID(用于多产品分支管理,自己随便写一个就行，只要和服务端能匹配上就可以)
                    ProductId = "2d974e2a-31e6-4887-9bb1-b4689e98c77a",
                    // 应用密钥(用于服务器验证，自己随便写一个就行，只要和服务端能匹配上就可以)
                    AppSecretKey = "dfeb5833-975e-4afb-88f1-6278ee9aeff6"
                };

                log.Info("更新配置完成，版本: {Version}", _configinfo.ClientVersion);

                // 启动更新检查（异步，不阻塞UI）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 延迟3秒再检查更新，避免影响程序启动速度
                        await Task.Delay(3000);
                        await StartUpdateAsync();
                    }
                    catch (Exception ex)
                    {
                        log.Error("启动更新检查失败", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                log.Error("ClientUpdateModule 初始化失败", ex);
                // 更新模块初始化失败不应影响主程序运行
            }
        }

        private string? _targetVersion; // 存储目标版本号

        /// <summary>
        /// 启动更新流程
        /// </summary>
        public async Task StartUpdateAsync()
        {
            if (_logFactory == null || _configinfo == null) return;

            var log = _logFactory.CreateLogService<ClientUpdateModule>();
            log.Info("开始检查更新...");

            try
            {
                var bootstrap = new GeneralClientBootstrap()
                    // 监听下载统计（速度、进度、剩余时间）
                    .AddListenerMultiDownloadStatistics(OnMultiDownloadStatistics)
                    // 监听单个更新包下载完成
                    .AddListenerMultiDownloadCompleted(OnMultiDownloadCompleted)
                    // 监听所有下载任务完成
                    .AddListenerMultiAllDownloadCompleted(OnMultiAllDownloadCompleted)
                    // 监听下载错误
                    .AddListenerMultiDownloadError(OnMultiDownloadError)
                    // 监听所有异常
                    .AddListenerException(OnException)
                    // 监听服务端返回的更新信息
                    .AddListenerUpdateInfo(OnUpdateInfo)
                    // 更新预检回调
                    .AddListenerUpdatePrecheck(OnUpdatePrecheck)
                    // 设置配置
                    .SetConfig(_configinfo)
                    // 设置选项
                    .Option(UpdateOption.DownloadTimeOut, 120)
                    .Option(UpdateOption.Encoding, Encoding.UTF8)
                    .Option(UpdateOption.Patch, true)              // 启用差量更新
                    .Option(UpdateOption.BackUp, true);              // 启用备份
                    //.Option(UpdateOption.EnableSilentUpdate, true); // 启用静默更新
                await Task.Delay(2000);

                // 启动异步更新
                await bootstrap.LaunchAsync();
                log.Info("更新流程启动完成");
            }
            catch (Exception ex)
            {
                log.Error("更新流程异常", ex);
                Console.WriteLine($"[ClientUpdate] 更新检查失败: {ex.Message}");
            }
        }
        private async void ReportUpdateResult(bool success, string? version)
        {
            var log = _logFactory.CreateLogService<ClientUpdateModule>();
            try
            {
                // 1. 获取机器码
                var machineId = GetMachineId();
                var report = new
                {
                    RecordId = 0, // 服务端会自动处理
                    Status = success ? 1 : 2,
                    Type = 1,
                    ClientVersion = _configinfo?.ClientVersion,
                    TargetVersion = version,
                    MachineId = machineId,
                    ErrorMessage = success ? null : "更新失败"
                };

                var json = JsonSerializer.Serialize(report);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                using var client = new HttpClient();
                await client.PostAsync(_configinfo?.ReportUrl ?? "", content);
            }
            catch (Exception ex)
            {
                log.Error("上报更新结果失败", ex);
            }
        }

        // 提取出来的获取 ID 方法
        private string GetMachineId()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    return key?.GetValue("MachineGuid")?.ToString() ?? Guid.NewGuid().ToString();
                }
            }
            catch
            {
                return Guid.NewGuid().ToString();
            }
        }

        #region 事件处理

        void OnMultiDownloadStatistics(object? sender, MultiDownloadStatisticsEventArgs e)
        {
            var version = e.Version as VersionInfo;
            var msg = $"[下载中] 版本 {version?.Version}, 速度: {e.Speed}, 进度: {e.BytesReceived}/{e.TotalBytesToReceive}";
            Console.WriteLine(msg);
        }

        void OnMultiDownloadCompleted(object? sender, MultiDownloadCompletedEventArgs e)
        {
            var version = e.Version as VersionInfo;
            var msg = e.IsComplated
                ? $"[下载完成] 版本 {version?.Version}"
                : $"[下载失败] 版本 {version?.Version}";
            Console.WriteLine(msg);

            if (_logFactory != null)
            {
                var log = _logFactory.CreateLogService<ClientUpdateModule>();
                log.Info(msg);
            }
        }

        void OnMultiAllDownloadCompleted(object? sender, MultiAllDownloadCompletedEventArgs e)
        {
            var msg = e.IsAllDownloadCompleted
                ? "[更新] 所有下载任务已完成，即将启动升级程序"
                : $"[更新] 下载任务失败，失败数量: {e.FailedVersions?.Count ?? 0}";
            Console.WriteLine(msg);
            
            // ⚠️ 注意：不要在这里更新版本号！
            // 版本号应该在 Upgrade 程序成功完成文件替换后再更新
            // 否则如果 Upgrade 失败，下次启动会跳过更新检查
            
            ReportUpdateResult(e.IsAllDownloadCompleted, e.IsAllDownloadCompleted ? _targetVersion : null!);

            if (_logFactory != null)
            {
                var log = _logFactory.CreateLogService<ClientUpdateModule>();
                log.Info(msg);
            }
            // 注意：此事件触发后，Client 会自动关闭自己并启动 Upgrade 进程
        }

        void OnMultiDownloadError(object? sender, MultiDownloadErrorEventArgs e)
        {
            var version = e.Version as VersionInfo;
            var msg = $"[下载错误] 版本 {version?.Version}: {e.Exception?.Message}";
            Console.WriteLine(msg);

            if (_logFactory != null)
            {
                var log = _logFactory.CreateLogService<ClientUpdateModule>();
                log.Error(msg, e.Exception);
            }
        }

        void OnException(object? sender, ExceptionEventArgs e)
        {
            var msg = $"[更新异常] {e.Exception?.Message}";
            Console.WriteLine(msg);

            if (_logFactory != null)
            {
                var log = _logFactory.CreateLogService<ClientUpdateModule>();
                log.Error("更新流程异常", e.Exception);
            }
        }

        void OnUpdateInfo(object? sender, UpdateInfoEventArgs e)
        {
            var msg = $"[更新信息] 服务端返回: Code={e.Info?.Code}, 版本数量={e.Info?.Body?.Count ?? 0}";
            Console.WriteLine(msg);
            var log = _logFactory.CreateLogService<ClientUpdateModule>();

            // 存储目标版本号（用于更新完成后写入本地版本文件，实际这里不写放在upgrade里面写）
            if (e.Info?.Body?.Count > 0)
            {
                _targetVersion = e.Info.Body
                    .OrderByDescending(v => new Version(v.Version))
                    .FirstOrDefault()
                    ?.Version;
                Console.WriteLine($"[更新信息] 目标版本: {_targetVersion}");
                log.Info("目标版本已设置为: {Version}", _targetVersion);
            }

            if (_logFactory != null)
            {
                
                log.Info(msg);
            }
        }

        bool OnUpdatePrecheck(UpdateInfoEventArgs e)
        {
            var count = e.Info?.Body?.Count ?? 0;
            Console.WriteLine($"[更新预检] 发现 {count} 个可用版本");

            if (count == 0)
            {
                Console.WriteLine("[更新预检] 无需更新");
                return true; // 跳过更新
            }

            // 可以在这里添加用户确认逻辑
            // 例如：弹出对话框询问用户是否更新
            return false; // false = 继续更新
        }

        #endregion

        
    }
}
