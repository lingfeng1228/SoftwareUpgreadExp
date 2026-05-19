using GeneralUpdate.Common.Download;
using GeneralUpdate.Common.Internal;
using GeneralUpdate.Common.Shared.Object;
using GeneralUpdate.Core;
using GeneralUpdate.Differential;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Upgrade
{
    /// <summary>
    /// 升级程序主窗口 - 纯WPF实现，不依赖Prism/MVVM框架
    /// </summary>
    public partial class MainWindow : Window
    {
        #region 字段

        private readonly List<LogEntry> _logs = new();
        private bool _isUpdating = false;
        private DateTime _startTime;
         private string? _targetVersion; // 存储目标版本号

        #endregion

        #region 构造函数

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            // 初始化日志
            AddLog("INFO", "升级程序启动");
            AddLog("INFO", $"运行目录: {Thread.GetDomain().BaseDirectory}");
        }

        #endregion

        #region 窗口事件

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 窗口加载完成后自动开始更新
            _ = StartUpdateAsync();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // 关闭时保存日志
            SaveLogsToFile();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                // 双击标题栏不最大化（固定大小窗口）
                return;
            }
            DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdating)
            {
                var result = MessageBox.Show("更新正在进行中，确定要退出吗？", "确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.No) return;
            }
            Close();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUpdating)
            {
                _ = StartUpdateAsync();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion

        #region 更新逻辑

        /// <summary>
        /// 启动更新流程
        /// </summary>
        public async Task StartUpdateAsync()
        {
            if (_isUpdating) return;

            _isUpdating = true;
            _startTime = DateTime.Now;
            UpdateUIState(true);

            AddLog("INFO", "开始检查更新...");

            try
            {
                var bootstrap = new GeneralUpdateBootstrap()
                    // 监听下载统计信息
                    .AddListenerMultiDownloadStatistics(OnMultiDownloadStatistics)
                    // 监听单个下载完成
                    .AddListenerMultiDownloadCompleted(OnMultiDownloadCompleted)
                    // 监听所有下载完成
                    .AddListenerMultiAllDownloadCompleted(OnMultiAllDownloadCompleted)
                    // 监听下载错误
                    .AddListenerMultiDownloadError(OnMultiDownloadError)
                    // 监听异常
                    .AddListenerException(OnException);

                
                // 启动异步升级
                await bootstrap.LaunchAsync();

                AddLog("INFO", "升级流程已启动");
            }
            catch (Exception ex)
            {
                AddLog("ERROR", $"启动更新失败: {ex.Message}");
                UpdateUIState(false, true);
            }
        }

        #endregion

        #region 事件处理

        /// <summary>
        /// 下载进度统计
        /// </summary>
        private void OnMultiDownloadStatistics(object? sender, MultiDownloadStatisticsEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var version = e.Version as VersionInfo;
                double progress = e.ProgressPercentage;
                double receivedMB = e.BytesReceived / 1024.0 / 1024.0;
                double totalMB = e.TotalBytesToReceive / 1024.0 / 1024.0;

                // 更新进度条
                DownloadProgressBar.Value = progress;

                // 更新版本信息
                VersionInfoText.Text = $"正在下载版本 {version?.Version ?? "未知"}";

                // 更新速度
                SpeedText.Text = $"速度: {e.Speed}";

                // 更新大小
                SizeText.Text = $"{receivedMB:F2} / {totalMB:F2} MB";

                // 更新百分比
                PercentageText.Text = $"{progress:F0}%";

                // 记录日志（每10%记录一次，避免日志过多）
                if (Math.Abs(progress % 10) < 1)
                {
                    AddLog("INFO", $"下载进度: {progress:F1}%, 速度: {e.Speed}");
                }
            });
        }

        /// <summary>
        /// 单个版本下载完成
        /// </summary>
        private void OnMultiDownloadCompleted(object? sender, MultiDownloadCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var version = e.Version as VersionInfo;
                if (e.IsComplated)
                {
                    AddLog("INFO", $"版本 {version?.Version} 下载完成");
                    // 存储目标版本号，用于后续更新 version-config.json
                    if (version != null && !string.IsNullOrEmpty(version.Version))
                    {
                        _targetVersion = version.Version;
                    }
                }
                else
                {
                    AddLog("WARN", $"版本 {version?.Version} 下载失败");
                }
            });
        }

        /// <summary>
        /// 所有下载完成
        /// </summary>
        private async void OnMultiAllDownloadCompleted(object? sender, MultiAllDownloadCompletedEventArgs e)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (e.IsAllDownloadCompleted)
                {
                    var duration = DateTime.Now - _startTime;
                    AddLog("INFO", $"所有下载任务已完成，耗时: {duration.TotalSeconds:F1}秒");
                    AddLog("INFO", "正在安装更新...");
                    // 应用补丁(还原)

                        // 1. 框架已经自动应用了补丁，这里再验证结果
                        var appDir = AppDomain.CurrentDomain.BaseDirectory; // 实际的应用根目录

                        // 2. 检查补丁解压后的目录（通常由框架放在临时目录，可从事件或日志推断）
                        //    但 GeneralUpdateBootstrap 不会直接暴露，查询临时目录结构来辅助
                        var tempRoot = Path.Combine(Path.GetTempPath(), "generalupdate_*");
                        // 找到最新的解压目录（仅用于调试，生产可去掉）
                        var dirs = Directory.GetDirectories(Path.GetTempPath(), "generalupdate_*")
                                           .OrderByDescending(d => new DirectoryInfo(d).LastWriteTime);
                        if (dirs.Any())
                        {
                            var patchDir = Path.Combine(dirs.First(), "patchs"); // 日志中是 generalupdate_2026-05-19_patchs
                            Console.WriteLine($"[调试] 补丁解压目录: {patchDir}");
                        //输出添加到日志中，方便查看
                                AddLog("INFO", $"补丁解压目录: {patchDir}");
                        if (Directory.Exists(patchDir))
                            {
                                var files = Directory.GetFiles(patchDir, "*", SearchOption.AllDirectories);
                                Console.WriteLine($"[调试] 补丁内文件列表: {string.Join(",", files)}");
                                AddLog("INFO", $"补丁内文件列表: {string.Join(", ", files)}");
                        }
                        }

                        // 3. 验证目标目录是否真的有新文件（比如你要新增的 DLL）
                        string expectedNewFile = Path.Combine(appDir, "Modules", "ModuleB.dll");
                        if (File.Exists(expectedNewFile))
                        {
                            Console.WriteLine($" 新增文件已存在: {expectedNewFile}");
                            AddLog("INFO", $"新增文件已存在: {expectedNewFile}");
                    }
                        else
                        {
                            Console.WriteLine($"新增文件未找到: {expectedNewFile}");
                            AddLog("ERROR", $"新增文件未找到: {expectedNewFile}");
                        var patchDir = Path.Combine(dirs.First(), "patchs");
                        // 4. 如果确实缺失，且你确认补丁正确，可以在此手动补救（不推荐作为长期方案）
                        //    但为了调试，可以在这里尝试手动应用一次
                        if (Directory.Exists(patchDir))
                            {
                                Console.WriteLine("尝试手动应用补丁进行补救...");
                            AddLog("INFO", "尝试手动应用补丁进行补救...");
                                await DifferentialCore.Dirty(appDir, patchDir);
                                Console.WriteLine("手动应用完成。");
                                AddLog("INFO", "手动应用完成。");
                            }
                        }

                    //  写入 Client 的 version-config.json，避免下次启动重复更新
                    try
                    {
                        // 使用从 OnMultiDownloadCompleted 回调中存储的目标版本号
                        if (!string.IsNullOrEmpty(_targetVersion))
                        {
                            UpdateClientVersionConfig(_targetVersion);
                            AddLog("INFO", $"客户端版本记录已更新: {_targetVersion}");
                        }
                        else
                        {
                            AddLog("WARN", "无法确定目标版本号，version-config.json 未更新");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog("WARN", $"写入版本记录失败: {ex.Message}");
                    }

                    // 更新UI为完成状态
                    UpdateUIState(false, false, true);
                }
                else
                {
                    AddLog("ERROR", $"下载任务失败，失败数量: {e.FailedVersions?.Count ?? 0}");
                    UpdateUIState(false, true);
                }
            });
        }

        /// <summary>
        /// 更新 Client 的 version-config.json
        /// Upgrade 程序更新完成后，需要更新 Client 的配置文件版本
        /// </summary>
        private void UpdateClientVersionConfig(string version)
        {
            try
            {
                // Client 的 version-config.json 在同级目录
                var configFile = Path.Combine(
                    Thread.GetDomain().BaseDirectory, "version-config.json");

                // 如果文件存在，读取并更新
                if (File.Exists(configFile))
                {
                    var json = File.ReadAllText(configFile);
                    var config = System.Text.Json.JsonSerializer.Deserialize<VersionConfig>(json);
                    if (config != null)
                    {
                        config.ClientVersion = version;
                        var options = new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true
                        };
                        var newJson = System.Text.Json.JsonSerializer.Serialize(config, options);
                        File.WriteAllText(configFile, newJson);
                        return;
                    }
                }

                // 如果文件不存在或读取失败，创建新文件
                var newConfig = new VersionConfig { ClientVersion = version };
                var defaultOptions = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var defaultJson = System.Text.Json.JsonSerializer.Serialize(newConfig, defaultOptions);
                File.WriteAllText(configFile, defaultJson);
            }
            catch (Exception ex)
            {
                AddLog("WARN", $"更新 Client 版本配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 版本配置数据（与 ClientUpdate 模块保持一致）
        /// </summary>
        private class VersionConfig
        {
            public string ClientVersion { get; set; } = "1.0.0.0";
        }

        /// <summary>
        /// 下载错误
        /// </summary>
        private void OnMultiDownloadError(object? sender, MultiDownloadErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var version = e.Version as VersionInfo;
                AddLog("ERROR", $"版本 {version?.Version} 下载错误: {e.Exception?.Message}");
            });
        }

        /// <summary>
        /// 异常处理
        /// </summary>
        private void OnException(object? sender, GeneralUpdate.Common.Internal.ExceptionEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                AddLog("ERROR", $"升级异常: {e.Exception?.Message}");
                UpdateUIState(false, true);
            });
        }

        #endregion

        #region UI更新

        /// <summary>
        /// 更新UI状态
        /// </summary>
        private void UpdateUIState(bool isUpdating, bool hasError = false, bool isCompleted = false)
        {
            if (isUpdating)
            {
                StatusText.Text = "正在更新...";
                StatusIconBorder.Background = new SolidColorBrush(Color.FromRgb(0, 180, 216));
                StartButton.IsEnabled = false;
                StartButton.Content = "更新中...";
                CancelButton.Content = "取消";
            }
            else if (isCompleted)
            {
                StatusText.Text = "更新完成！";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 180, 216));
                StatusIconBorder.Background = new SolidColorBrush(Color.FromRgb(0, 180, 216));
                
                // 停止旋转动画
                StatusIconBorder.RenderTransform = new RotateTransform(0);
                
                StartButton.IsEnabled = true;
                StartButton.Content = "完成";
                StartButton.Click -= StartButton_Click;
                StartButton.Click += (s, e) => Close();
                CancelButton.Visibility = Visibility.Collapsed;

                AddLog("INFO", "更新成功完成！");
            }
            else if (hasError)
            {
                StatusText.Text = "更新失败";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(232, 17, 35));
                StatusIconBorder.Background = new SolidColorBrush(Color.FromRgb(232, 17, 35));
                
                StartButton.IsEnabled = true;
                StartButton.Content = "重试";
                CancelButton.Content = "退出";

                AddLog("ERROR", "更新失败，请检查网络连接或联系技术支持");
            }
        }

        #endregion

        #region 日志系统

        /// <summary>
        /// 日志条目
        /// </summary>
        private class LogEntry
        {
            public DateTime Time { get; set; }
            public string Level { get; set; } = "";
            public string Message { get; set; } = "";
        }

        /// <summary>
        /// 添加日志
        /// </summary>
        private void AddLog(string level, string message)
        {
            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Message = message
            };
            _logs.Add(entry);

            // 实时写入日志文件
            AppendLogToFile(entry);

            // 创建UI元素
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            var timeBlock = new TextBlock
            {
                Text = entry.Time.ToString("HH:mm:ss"),
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                Width = 60
            };

            var levelColor = level switch
            {
                "ERROR" => Color.FromRgb(232, 17, 35),
                "WARN" => Color.FromRgb(255, 193, 7),
                "INFO" => Color.FromRgb(0, 180, 216),
                _ => Color.FromRgb(136, 136, 136)
            };

            var levelBlock = new TextBlock
            {
                Text = level,
                Foreground = new SolidColorBrush(levelColor),
                FontSize = 11,
                Width = 40,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5, 0, 0, 0)
            };

            var msgBlock = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(5, 0, 0, 0)
            };

            panel.Children.Add(timeBlock);
            panel.Children.Add(levelBlock);
            panel.Children.Add(msgBlock);

            LogPanel.Children.Add(panel);

            // 自动滚动到底部
            LogScrollViewer.ScrollToEnd();
        }

        /// <summary>
        /// 实时追加单条日志到文件
        /// </summary>
        private void AppendLogToFile(LogEntry entry)
        {
            try
            {
                // 获取程序所在目录，创建logs子目录
                var logDir = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
                    "logs");
                Directory.CreateDirectory(logDir);

                // 使用固定文件名，按日期命名
                var logFile = Path.Combine(logDir, $"upgrade_{DateTime.Now:yyyyMMdd}.log");
                var line = $"[{entry.Time:yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}{Environment.NewLine}";
                File.AppendAllText(logFile, line);
            }
            catch
            {
                // 写入日志失败不影响主程序运行
            }
        }

        /// <summary>
        /// 保存日志到文件（窗口关闭时调用，保留原有功能）
        /// </summary>
        private void SaveLogsToFile()
        {
            try
            {
                // 获取程序所在目录，创建logs子目录
                var logDir = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
                    "logs");
                Directory.CreateDirectory(logDir);

                var logFile = Path.Combine(logDir, $"upgrade_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                var lines = _logs.Select(l => $"[{l.Time:yyyy-MM-dd HH:mm:ss}] [{l.Level}] {l.Message}");
                File.WriteAllLines(logFile, lines);
            }
            catch
            {
                // 保存日志失败不影响程序退出
            }
        }

        #endregion
    }

    #region 转换器

    /// <summary>
    /// 进度条宽度转换器
    /// </summary>
    public class ProgressBarWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length < 3) return 0.0;

            double value = (double)values[0];
            double maximum = (double)values[1];
            double trackWidth = (double)values[2];

            if (maximum == 0) return 0.0;

            return (value / maximum) * trackWidth;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion
}
