using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private readonly ILuaFileManager _luaFileManager;
    private readonly ISettingsService _settingsService;
    private readonly ISteamApiService _steamApiService;
    private readonly IUpdateService _updateService;
    private AppSettings _settings;

    [ObservableProperty]
    private string _steamPath = string.Empty;

    [ObservableProperty]
    private string _luaFolderPath = string.Empty;

    [ObservableProperty]
    private bool _isAutoRefreshEnabled = true;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _selectedCdnIndex;

    [ObservableProperty]
    private bool _isSpeedTesting;

    [ObservableProperty]
    private string _updateCheckUrl = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private string _kernelSource = "Fork";

    [ObservableProperty]
    private bool _compatMode;

    /// <summary>当前软件版本号（随构建自动更新）。</summary>
    public string CurrentVersion =>
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestProgressText))]
    private int _speedTestProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestProgressText))]
    private int _speedTestTotal;

    public string SpeedTestProgressText => $"{SpeedTestProgress}/{SpeedTestTotal}";

    [ObservableProperty]
    private string _selectedBackdrop = "Acrylic10";

    [ObservableProperty]
    private string _selectedSkin = "PureBlack";

    public record BackdropOption(string Display, string Value);

    public List<BackdropOption> BackdropOptions { get; } = new()
    {
        new("亚克力", "Acrylic10"),
        new("云母", "Mica"),
        new("无", "None"),
    };

    public record ThemeOption(string Display, string Value);

    public List<ThemeOption> ThemeOptions { get; } = new()
    {
        new("深色模式", "Dark"),
    };

    public IReadOnlyList<SkinCatalog.SkinOption> SkinOptions { get; } = SkinCatalog.Options;

    public List<CdnEndpoint> CdnEndpoints { get; } = CdnEndpoint.Defaults;

    public record KernelSourceOption(string Display, string Value);

    public List<KernelSourceOption> KernelSourceOptions { get; } = new()
    {
        new("作者分支版（pvzcxw，视频配套 ost）", "Fork"),
        new("官方版（OpenSteam001）", "Official"),
    };

    public ObservableCollection<SpeedTestItem> SpeedTestResults { get; } = new();

    public SettingsViewModel(ISteamPathService steamPathService, ILuaFileManager luaFileManager,
        ISettingsService settingsService, ISteamApiService steamApiService, IUpdateService updateService)
    {
        _steamPathService = steamPathService;
        _luaFileManager = luaFileManager;
        _settingsService = settingsService;
        _steamApiService = steamApiService;
        _updateService = updateService;
        _settings = settingsService.Load();

        SteamPath = _settings.SteamPath;
        IsAutoRefreshEnabled = _settings.AutoRefreshEnabled;
        IsFabVisible = _settings.IsFabVisible;
        IsCardRefreshVisible = _settings.IsCardRefreshVisible;
        AutoCheckUpdateEnabled = _settings.AutoCheckUpdateEnabled;
        UpdateCheckUrl = UpdateService.DefaultUpdateCheckUrl;
        KernelSource = string.IsNullOrEmpty(_settings.KernelSource) ? "Fork" : _settings.KernelSource;
        CompatMode = _settings.CompatMode;
        IsShowTrainerSections = _settings.ShowTrainerSections;
        IsShowCopyLogButton = _settings.ShowCopyLogButton;
        EnableLogging = _settings.EnableLogging;
        MinimizeToTray = _settings.MinimizeToTray;

        SelectedTheme = _settings.SelectedTheme;
        SelectedSkin = string.IsNullOrEmpty(_settings.Skin) ? "PureBlack" : _settings.Skin;
        SelectedCdnIndex = Math.Clamp(_settings.SelectedCdnIndex, 0, CdnEndpoints.Count - 1);
        _selectedBackdrop = _settings.SelectedBackdrop;
        DownloadMode = _settings.DownloadMode;
        KeyFolderPath = _settings.KeyFolderPath;

        _steamApiService.CdnAutoSwitched += OnCdnAutoSwitched;

        if (string.IsNullOrEmpty(SteamPath))
        {
            var detectedPath = steamPathService.DetectSteamPath();
            SteamPath = detectedPath ?? "未检测到Steam";
        }

        LuaFolderPath = steamPathService.GetLuaFolder() ?? "未配置";

    }

    private void OnCdnAutoSwitched(int newIndex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (newIndex >= 0 && newIndex < CdnEndpoints.Count)
            {
                SelectedCdnIndex = newIndex;
                StatusMessage = $"封面节点已自动切换: {CdnEndpoints[newIndex].Name}";
                LogService.Warn("设置", $"封面节点已自动切换: {CdnEndpoints[newIndex].Name}");
            }
        });
    }

    private DispatcherTimer? _statusTimer;

    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;

        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusMessage = string.Empty;
    }

    public void Dispose()
    {
        _steamApiService.CdnAutoSwitched -= OnCdnAutoSwitched;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
            _statusTimer = null;
        }
    }

    partial void OnSelectedCdnIndexChanged(int value)
    {
        _settings.SelectedCdnIndex = value;
        _settingsService.Save(_settings);
        _steamApiService.UpdateCdnPreference(value);
        StatusMessage = $"封面节点已切换: {CdnEndpoints[value].Name}";
        LogService.Info("设置", $"封面节点已切换: {CdnEndpoints[value].Name}");
    }

    partial void OnIsFabVisibleChanged(bool value)
    {
        _settings.IsFabVisible = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "悬浮按钮已显示" : "悬浮按钮已隐藏";
        LogService.Info("设置", value ? "悬浮按钮已显示" : "悬浮按钮已隐藏");
    }

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.SelectedTheme = value;
        _settingsService.Save(_settings);
        // 本版本固定为纯黑深色主题
        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        StatusMessage = "已切换为深色模式";
        LogService.Info("设置", $"主题已切换: {StatusMessage}");
    }

    partial void OnSelectedSkinChanged(string value)
    {
        _settings.Skin = value;
        _settingsService.Save(_settings);
        SkinCatalog.Apply(value);
        var option = SkinOptions.FirstOrDefault(o => o.Value == value);
        StatusMessage = option == null ? $"已切换皮肤：{value}" : $"已切换皮肤：{option.Display}";
        LogService.Info("设置", StatusMessage);
    }

    private static ApplicationTheme GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        catch { }
        return ApplicationTheme.Dark;
    }

    partial void OnIsCardRefreshVisibleChanged(bool value)
    {
        _settings.IsCardRefreshVisible = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "卡片刷新按钮已显示" : "卡片刷新按钮已隐藏";
        LogService.Info("设置", value ? "卡片刷新按钮已显示" : "卡片刷新按钮已隐藏");
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        _settings.AutoRefreshEnabled = value;
        _settingsService.Save(_settings);

        if (value)
            _luaFileManager.StartWatching();
        else
            _luaFileManager.StopWatching();
        StatusMessage = value ? "自动监控已开启" : "自动监控已关闭";
        LogService.Info("设置", value ? "自动监控已开启" : "自动监控已关闭");
    }

    partial void OnAutoCheckUpdateEnabledChanged(bool value)
    {
        _settings.AutoCheckUpdateEnabled = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "启动时自动检查更新已开启" : "启动时自动检查更新已关闭";
        LogService.Info("设置", value ? "启动时自动检查更新已开启" : "启动时自动检查更新已关闭");
    }

    partial void OnKernelSourceChanged(string value)
    {
        _settings.KernelSource = value;
        _settingsService.Save(_settings);
        StatusMessage = value == "Official" ? "内核更新源已切换为官方版（OpenSteam001）" : "内核更新源已切换为作者分支版（pvzcxw）";
        LogService.Info("设置", StatusMessage);
    }

    partial void OnCompatModeChanged(bool value)
    {
        _settings.CompatMode = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "兼容模式已开启：关闭背景特效，缓解 A 卡/低配电脑拖拽卡顿" : "兼容模式已关闭";
        LogService.Info("设置", StatusMessage);
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        await CheckForUpdatesCoreAsync(showUpToDate: true);
    }

    /// <summary>启动时静默检查更新；有新版本时弹窗提示并返回 true。</summary>
    public async Task<bool> CheckForUpdatesSilentlyAsync()
    {
        return await CheckForUpdatesCoreAsync(showUpToDate: false);
    }

    private async Task<bool> CheckForUpdatesCoreAsync(bool showUpToDate)
    {
        if (string.IsNullOrWhiteSpace(UpdateCheckUrl))
        {
            if (showUpToDate)
            {
                StatusMessage = "尚未填写更新检查地址，请在「软件更新」中填写 GitHub/Gitee 发布地址";
                LogService.Warn("更新", "未配置更新检查地址");
            }
            return false;
        }
        if (IsCheckingUpdate) return false;

        IsCheckingUpdate = true;
        try
        {
            StatusMessage = "正在检查更新...";
            var info = await _updateService.CheckLatestAsync(UpdateCheckUrl.Trim());
            if (info == null)
            {
                StatusMessage = "检查更新失败：无法解析版本信息";
                if (showUpToDate)
                    await ShowInfoDialogAsync("检查更新", "无法获取远程版本信息，请确认更新地址正确。");
                return false;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            if (!UpdateService.IsNewer(info.LatestVersion, current))
            {
                StatusMessage = $"当前已是最新版本（{current}）";
                if (showUpToDate)
                    await ShowInfoDialogAsync("检查更新", $"当前已是最新版本：{current}");
                return false;
            }

            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "作者发布了新版本，建议升级。"
                : $"更新说明：\n{info.Notes}";
            var downloadTarget = string.IsNullOrEmpty(info.DownloadUrl) ? info.ReleaseUrl : info.DownloadUrl;
            var dialog = new ContentDialog
            {
                Title = "发现新版本",
                Content = new TextBlock
                {
                    Text = $"当前版本：{current}\n最新版本：{info.LatestVersion}\n\n{notes}",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 440
                },
                PrimaryButtonText = "前往下载",
                CloseButtonText = "稍后再说",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(downloadTarget) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogService.Warn("更新", $"打开下载页失败: {ex.Message}");
                }
            }
            StatusMessage = $"发现新版本 {info.LatestVersion}";
            LogService.Info("更新", $"发现新版本 {info.LatestVersion}，当前 {current}");
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"检查更新失败：{ex.Message}";
            LogService.Warn("更新", $"检查更新失败: {ex.Message}");
            if (showUpToDate)
                await ShowInfoDialogAsync("检查更新", $"检查更新失败：{ex.Message}\n\n请确认更新地址可访问，或稍后重试。");
            return false;
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private static async Task ShowInfoDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "关闭时最小化到系统托盘已开启" : "关闭时最小化到系统托盘已关闭";
        LogService.Info("设置", value ? "关闭时最小化到系统托盘已开启" : "关闭时最小化到系统托盘已关闭");
    }

    partial void OnSelectedBackdropChanged(string value)
    {
        _settings.SelectedBackdrop = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Acrylic10" => "背景效果已切换为亚克力",
            "Mica" => "背景效果已切换为云母",
            "None" => "背景效果已关闭",
            _ => ""
        };
        if (!string.IsNullOrEmpty(StatusMessage))
            LogService.Info("设置", StatusMessage);
    }

    [RelayCommand]
    private void BrowseSteamPath()
    {
        var dialog = new OpenFileDialog
        {
            FileName = "steam.exe",
            Filter = "Steam可执行文件|steam.exe",
            Title = "选择Steam安装路径"
        };
        if (dialog.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(dir))
            {
                SteamPath = dir;
                _steamPathService.SetCustomPath(dir);
                _settings.SteamPath = dir;
                _settingsService.Save(_settings);
                StatusMessage = $"Steam路径已设置为: {dir}";
                LogService.Info("设置", $"Steam路径已设置为: {dir}");
            }
        }
    }

    [RelayCommand]
    private void ResetSteamPath()
    {
        _steamPathService.SetCustomPath(string.Empty);
        var detectedPath = _steamPathService.DetectSteamPath();
        SteamPath = detectedPath ?? "未检测到Steam";
        _settings.SteamPath = string.Empty;
        _settingsService.Save(_settings);
        StatusMessage = "已重置为自动检测路径";
        LogService.Info("设置", "已重置为自动检测路径");
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                StatusMessage = "没有需要清理的缓存";
                LogService.Info("设置", "没有需要清理的缓存");
                return;
            }

            var lockedFiles = new List<string>();
            var deletedCount = 0;

            await Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (IOException)
                    {
                        lockedFiles.Add(Path.GetFileName(file));
                    }
                    catch { }
                }

                foreach (var dir in Directory.GetDirectories(cacheDir))
                {
                    try { Directory.Delete(dir, true); }
                    catch { }
                }

                Directory.CreateDirectory(Path.Combine(cacheDir, "covers"));
            });

            if (lockedFiles.Count > 0)
            {
                StatusMessage = $"缓存已清理(跳过{lockedFiles.Count}个占用文件)";
                LogService.Info("设置", $"缓存已清理(跳过{lockedFiles.Count}个占用文件): {string.Join(", ", lockedFiles)}");
            }
            else
            {
                StatusMessage = $"缓存已清理(共{deletedCount}个文件)";
                LogService.Info("设置", $"缓存已清理(共{deletedCount}个文件)");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"清理失败: {ex.Message}";
            LogService.Error("设置", $"清理缓存失败: {ex}");
        }
    }

    [RelayCommand]
    private void OpenLuaFolder()
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
        {
            StatusMessage = "Lua文件夹不存在";
            LogService.Warn("设置", "Lua文件夹不存在");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败: {ex.Message}";
            LogService.Error("设置", $"打开 Lua 文件夹失败 ({luaFolder}): {ex}");
        }
    }

    [RelayCommand]
    private async Task ChangeLuaFolderAsync()
    {
        var oldFolder = _steamPathService.GetLuaFolder();

        string? dir;
        try
        {
            var owner = new System.Windows.Interop.WindowInteropHelper(System.Windows.Application.Current.MainWindow).Handle;
            dir = FolderPicker.PickFolder(oldFolder, owner);
        }
        catch (Exception ex)
        {
            LogService.Error("设置", $"选择 Lua 目录失败: {ex}");
            StatusMessage = "打开目录选择失败，请重试";
            return;
        }
        if (string.IsNullOrEmpty(dir)) return;
        if (string.Equals(oldFolder, dir, StringComparison.OrdinalIgnoreCase)) return;

        if (_steamPathService.SetConfiguredLuaPath(dir))
        {
            LuaFolderPath = _steamPathService.GetLuaFolder() ?? "未配置";

            // 路径变更后重启文件监听，指向新目录
            if (IsAutoRefreshEnabled)
            {
                _luaFileManager.StopWatching();
                _luaFileManager.StartWatching();
            }

            // 检测原路径是否有残留 lua 文件（含子目录，如被禁用清单），询问是否迁移
            await MigrateLuaFilesIfNeededAsync(oldFolder, dir);

            // 通知主页重新扫描游戏，避免残留旧路径的 lua 引用
            MainViewModel.RequestRefresh();

            StatusMessage = $"Lua 目录已设置为: {dir}";
            LogService.Info("设置", $"Lua 目录已设置为: {dir}");
        }
        else
        {
            StatusMessage = "Lua 目录设置失败，请检查配置文件写入权限";
            LogService.Error("设置", "Lua 目录设置失败，请检查配置文件写入权限");
        }
    }

    [RelayCommand]
    private async Task ResetLuaConfigAsync()
    {
        var oldFolder = _steamPathService.GetLuaFolder();

        if (_steamPathService.ResetConfiguredLuaPath())
        {
            LuaFolderPath = _steamPathService.GetLuaFolder() ?? "未配置";

            // 自定义路径 → 默认路径，同样触发迁移检测
            await MigrateLuaFilesIfNeededAsync(oldFolder, LuaFolderPath);

            if (IsAutoRefreshEnabled)
            {
                _luaFileManager.StopWatching();
                _luaFileManager.StartWatching();
            }

            MainViewModel.RequestRefresh();

            StatusMessage = $"已重置为默认目录: {LuaFolderPath}";
            LogService.Info("设置", $"已重置 Lua 目录为默认: {LuaFolderPath}");
        }
        else
        {
            StatusMessage = "重置失败，请检查配置文件写入权限";
            LogService.Error("设置", "重置 Lua 目录配置失败");
        }
    }

    /// <summary>检测旧路径残留 lua 文件（含子目录），弹窗询问后迁移到新路径。</summary>
    private async Task<bool> MigrateLuaFilesIfNeededAsync(string? oldFolder, string newFolder)
    {
        if (string.IsNullOrEmpty(oldFolder) || !Directory.Exists(oldFolder) ||
            string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        var files = Directory.GetFiles(oldFolder, "*.lua", SearchOption.AllDirectories);
        if (files.Length == 0) return false;

        var migrate = await ShowConfirmAsync(
            "迁移 Lua 文件",
            $"原路径 {oldFolder} 存在 {files.Length} 个 lua 文件（含子目录），\n是否迁移到新路径 {newFolder}？",
            "迁移", "不迁移");
        if (!migrate) return false;

        var copied = 0;
        foreach (var file in files)
        {
            try
            {
                var relative = Path.GetRelativePath(oldFolder, file);
                var dest = Path.Combine(newFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, overwrite: true);
                copied++;
            }
            catch
            {
                // 跨卷等场景 File.Move 不可用时回退为复制后删除
                try
                {
                    var relative = Path.GetRelativePath(oldFolder, file);
                    var dest = Path.Combine(newFolder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    File.Delete(file);
                    copied++;
                }
                catch (Exception ex2)
                {
                    LogService.Warn("设置", $"迁移 {file} 失败: {ex2.Message}");
                }
            }
        }
        StatusMessage = $"已迁移 {copied}/{files.Length} 个 lua 文件";
        LogService.Info("设置", $"已迁移 {copied}/{files.Length} 个 lua 文件到 {newFolder}");
        return true;
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        var steamDir = SteamPath;
        if (!string.IsNullOrEmpty(steamDir) && Directory.Exists(steamDir))
            Process.Start(new ProcessStartInfo { FileName = steamDir, UseShellExecute = true });
        else
        {
            StatusMessage = "Steam路径不存在或未设置";
            LogService.Warn("设置", "Steam路径不存在或未设置");
        }
    }

    [RelayCommand]
    private void OpenBinStatsFolder()
    {
        var steamDir = SteamPath;
        if (string.IsNullOrEmpty(steamDir) || !Directory.Exists(steamDir))
        {
            StatusMessage = "Steam路径不存在或未设置";
            LogService.Warn("设置", "Steam路径不存在或未设置");
            return;
        }
        var statsDir = Path.Combine(steamDir, "appcache", "stats");
        Directory.CreateDirectory(statsDir);
        Process.Start(new ProcessStartInfo { FileName = statsDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);
        Process.Start(new ProcessStartInfo { FileName = cacheDir, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task TestCdnSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        SpeedTestResults.Clear();
        SpeedTestTotal = CdnEndpoint.Defaults.Count;
        SpeedTestProgress = 0;
        StatusMessage = "正在测试所有CDN节点...";

        try
        {
            var progress = new Progress<(string Name, long LatencyMs, bool IsSuccess)>(result =>
            {
                SpeedTestProgress++;
                SpeedTestResults.Add(new SpeedTestItem
                {
                    Name = result.Name,
                    LatencyMs = result.LatencyMs,
                    IsSuccess = result.IsSuccess
                });
            });

            var results = await _steamApiService.TestCdnSpeedAsync(progress);

            var best = SpeedTestResults.Where(r => r.IsSuccess).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (best != null)
            {
                StatusMessage = $"测速完成，最快节点: {best.Name} ({best.LatencyMs}ms)";
                LogService.Info("设置", $"CDN 测速完成，最快节点: {best.Name} ({best.LatencyMs}ms)");
            }
            else
            {
                StatusMessage = "所有节点均不可达";
                LogService.Warn("设置", "CDN 测速完成，所有节点均不可达");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"测速失败: {ex.Message}";
            LogService.Error("设置", $"CDN 测速失败: {ex}");
        }
        finally
        {
            IsSpeedTesting = false;
        }
    }

    [ObservableProperty]
    private bool _isFabVisible = true;

    [ObservableProperty]
    private bool _isCardRefreshVisible = true;

    [ObservableProperty]
    private bool _autoCheckUpdateEnabled = true;

    [ObservableProperty]
    private string _selectedTheme = "Dark";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _downloadMode = "DepotKey";

    [ObservableProperty]
    private bool _isShowTrainerSections = true;

    [ObservableProperty]
    private bool _isShowCopyLogButton;

    [ObservableProperty]
    private bool _enableLogging;

    partial void OnEnableLoggingChanged(bool value)
    {
        _settings.EnableLogging = value;
        _settingsService.Save(_settings);
        LogService.SetEnabled(value);
        StatusMessage = value ? "日志记录已开启，将输出到软件目录的 app.log" : "日志记录已关闭";
        LogService.Info("设置", value ? "日志记录已开启，将输出到软件目录的 app.log" : "日志记录已关闭");
    }

    partial void OnIsShowCopyLogButtonChanged(bool value)
    {
        _settings.ShowCopyLogButton = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "日志复制按钮已显示" : "日志复制按钮已隐藏";
        LogService.Info("设置", value ? "日志复制按钮已显示" : "日志复制按钮已隐藏");
    }

    partial void OnIsShowTrainerSectionsChanged(bool value)
    {
        _settings.ShowTrainerSections = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "修改器推荐栏目已显示" : "修改器推荐栏目已隐藏";
        LogService.Info("设置", value ? "修改器推荐栏目已显示" : "修改器推荐栏目已隐藏");
    }

    [ObservableProperty]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private string _keyFolderPath = string.Empty;

    partial void OnDownloadModeChanged(string value)
    {
        _settings.DownloadMode = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Remote" => "已切换为远程清单仓库",
            "DepotKey" => "已切换为本地缓存仓库 V1",
            "DepotKey2" => "已切换为本地缓存仓库 V2",
            "ShikiLua" => "已切换为 ShikiLua 内置库",
            _ => ""
        };
        if (!string.IsNullOrEmpty(StatusMessage))
            LogService.Info("设置", StatusMessage);
    }

    partial void OnKeyFolderPathChanged(string value)
    {
        _settings.KeyFolderPath = value;
        _settingsService.Save(_settings);
    }
}

public class SpeedTestItem
{
    public string Name { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
    public bool IsSuccess { get; set; }
    public string StatusText => IsSuccess ? $"{LatencyMs}ms" : "失败";
    public string ColorCode => IsSuccess ? LatencyMs switch
    {
        <= 200 => "#4CAF50",
        <= 500 => "#FF9800",
        _ => "#F44336"
    } : "#F44336";
}
