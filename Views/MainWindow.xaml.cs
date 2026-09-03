using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;
using MessageBox = iNKORE.UI.WPF.Modern.Controls.MessageBox;

namespace SteamLuaManager.Views;

public partial class MainWindow : Window
{
    private readonly string[] _navOrder = ["Home", "ScriptDownload", "NewGames", "Achievement", "Save", "OnlineFix", "Trainer", "Settings"];
    private string _prevTag = "Home";

    /// <summary>当前页面 tag，供全局操作日志标注上下文。</summary>
    public static string? CurrentPage { get; private set; }

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ISteamPathService _steamPathService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ScriptDownloadViewModel _scriptDownloadViewModel;
    private readonly NewGamesViewModel _newGamesViewModel;
    private readonly ExtractionViewModel _extractionViewModel;
    private readonly TrainerViewModel _trainerViewModel;
    private readonly AchievementViewModel _achievementViewModel;
    private readonly AuthorizationViewModel _authorizationViewModel;
    private readonly SaveViewModel _saveViewModel;
    private readonly HomeView _homeView;
    private readonly SettingsView _settingsView;
    private readonly ScriptDownloadView _scriptDownloadView;
    private readonly NewGamesView _newGamesView;
    private readonly ExtractionView _extractionView;
    private readonly TrainerView _trainerView;
    private readonly AchievementView _achievementView;
    private readonly AuthorizationView _authorizationView;
    private readonly SaveView _saveView;
    private readonly OnlineFixView _onlineFixView;
    private readonly IOpenSteamToolService _openSteamToolService;
    private CancellationTokenSource? _kernelCts;
    private TrayIconManager? _trayIcon;
    private bool _exitRequested;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settingsViewModel, ScriptDownloadViewModel scriptDownloadViewModel, NewGamesViewModel newGamesViewModel, ExtractionViewModel extractionViewModel, TrainerViewModel trainerViewModel, AchievementViewModel achievementViewModel, AuthorizationViewModel authorizationViewModel, SaveViewModel saveViewModel, ISettingsService settingsService, ISteamPathService steamPathService, IOpenSteamToolService openSteamToolService)
    {
        InitializeComponent();
        CurrentPage = "Home";
        _openSteamToolService = openSteamToolService;
        _dropHintHideTimer.Tick += (_, _) => { _dropHintHideTimer.Stop(); DropHintGrid.Visibility = Visibility.Collapsed; };
        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _scriptDownloadViewModel = scriptDownloadViewModel;
        _newGamesViewModel = newGamesViewModel;
        _extractionViewModel = extractionViewModel;
        _trainerViewModel = trainerViewModel;
        _achievementViewModel = achievementViewModel;
        _authorizationViewModel = authorizationViewModel;
        _saveViewModel = saveViewModel;
        _settingsService = settingsService;
        _steamPathService = steamPathService;
        DataContext = _viewModel;

        var iconUri = new Uri("pack://application:,,,/Assets/app.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bestFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        Icon = bestFrame;

        _homeView = new HomeView { DataContext = _viewModel };
        _settingsView = new SettingsView { DataContext = settingsViewModel };
        _scriptDownloadView = new ScriptDownloadView { DataContext = scriptDownloadViewModel };
        _newGamesView = new NewGamesView { DataContext = newGamesViewModel };
        _extractionView = new ExtractionView { DataContext = extractionViewModel };
        _trainerView = new TrainerView { DataContext = trainerViewModel };
        _achievementView = new AchievementView { DataContext = achievementViewModel };
        _authorizationView = new AuthorizationView { DataContext = authorizationViewModel };
        _saveView = new SaveView { DataContext = saveViewModel };
        _onlineFixView = new OnlineFixView { DataContext = viewModel };
        HomeItem.IsChecked = true;
        ContentTransition.Content = _homeView;

        settingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
        Closed += MainWindow_Closed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        // 用户选择最小化到托盘：拦截关闭并隐藏到系统托盘
        if (!_exitRequested && _settingsService.Load().MinimizeToTray)
        {
            e.Cancel = true;
            EnsureTrayIcon();
            _trayIcon!.Visible = true;
            Hide();
            _trayIcon.ShowBalloonOnce("喜加一", "程序已最小化到系统托盘，单击托盘图标恢复");
            return;
        }

        DisposeTrayIcon();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;
        _trayIcon = new TrayIconManager();
        _trayIcon.RestoreRequested += RestoreFromTray;
        _trayIcon.OpenSettingsRequested += OpenSettingsFromTray;
        _trayIcon.ExitRequested += () =>
        {
            _exitRequested = true;
            DisposeTrayIcon();
            Application.Current.Shutdown();
        };
    }

    private void OpenSettingsFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        SettingsItem.IsChecked = true;
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null) return;
        _trayIcon.RestoreRequested -= RestoreFromTray;
        _trayIcon.OpenSettingsRequested -= OpenSettingsFromTray;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel svm) return;
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SelectedBackdrop):
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
            case nameof(SettingsViewModel.IsCardRefreshVisible):
                _viewModel.IsCardRefreshVisible = svm.IsCardRefreshVisible;
                break;
            case nameof(SettingsViewModel.SelectedTheme):
                UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
            case nameof(SettingsViewModel.CompatMode):
                // 兼容模式：忽略所选背景特效，直接使用纯色背景，缓解 A 卡/低配电脑拖拽卡顿
                UpdateBackdrop(svm.CompatMode ? "None" : svm.SelectedBackdrop);
                break;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
        if (_viewModel is IDisposable viewModelDisposable)
            viewModelDisposable.Dispose();
        if (_settingsViewModel is IDisposable settingsViewModelDisposable)
            settingsViewModelDisposable.Dispose();
    }

    private void RefreshTitle()
    {
        // 顶部标题保持简洁，不显示内核状态等信息
        Title = "喜加一";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadedCommand.CanExecute(null))
            await _viewModel.LoadedCommand.ExecuteAsync(null);

        RefreshTitle();

        var settings = _settingsService.Load();
        UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
        UpdateBackdrop(settings.SelectedBackdrop);
        if (settings.CompatMode)
            UpdateBackdrop("None");
        _settingsViewModel.SelectedBackdrop = settings.SelectedBackdrop;

        // 启动时静默检查更新（有新版本会弹窗提示，失败不影响使用）
        _ = _settingsViewModel.CheckForUpdatesSilentlyAsync();

        switch (_viewModel.OpenSteamToolStatus)
        {
            case "未安装 OpenSteamTool":
                await ShowModernDialogAsync(
                    "未安装 OpenSteamTool",
                    "未检测到 OpenSteamTool，本软件目前仅适配 OpenSteamTool。\n\n" +
                    "请确保已在 Steam 目录中正确安装 OpenSteamTool 后再使用。\n\n" +
                    "可在左侧栏「内核管理」中点击安装。");
                break;

            case "检测到不适配的 SteamTools":
                var fixNow = await ShowModernConfirmAsync(
                    "内核冲突提示",
                    "检测到第三方 SteamTools（闭源）内核及残留配置，与本软件不适配。\n\n" +
                    "本软件采用开源的 OpenSteamTool 内核，更稳定、且支持最新游戏与持续更新。\n\n" +
                    "是否立即一键自动清理冲突残留，并安装 OpenSteamTool 内核？",
                    "一键修复并安装");
                if (fixNow)
                {
                    await InstallKernelAsync();
                }
                break;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (_settingsViewModel.SelectedTheme != "System") return;

        Dispatcher.Invoke(() =>
        {
            var isLight = GetSystemIsLightTheme();
            ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
            UpdateBackdropTheme(isLight);
        });
    }

    private static bool GetSystemIsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1;
        }
        catch { }
        return false;
    }

    private async Task ShowModernDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowModernConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowKernelOverlay(string status)
    {
        KernelOverlayGrid.Visibility = Visibility.Visible;
        KernelOverlayStatus.Text = status;
        KernelOverlayHint.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Collapsed;
        KernelOverlayPercent.Visibility = Visibility.Collapsed;
        KernelOverlayRing.IsActive = true;
    }

    private void UpdateKernelOverlayProgress(int percent)
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayRing.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Visible;
        KernelOverlayPercent.Visibility = Visibility.Visible;
        KernelOverlayProgressBar.Value = percent;
        KernelOverlayPercent.Text = $"{percent}%";
    }

    private void ShowKernelDownloadHint()
    {
        KernelOverlayHint.Visibility = Visibility.Visible;
    }

    private void HideKernelOverlay()
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayGrid.Visibility = Visibility.Collapsed;
    }

    private void KernelCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _kernelCts?.Cancel();
    }

    private void UpdateBackdrop(string backdropTypeName)
    {
        if (!Enum.TryParse<BackdropType>(backdropTypeName, true, out var backdropType))
            return;

        WindowHelper.SetSystemBackdropType(this, backdropType);

        if (backdropType == BackdropType.None)
        {
            var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        }
        else
        {
            Background = null;
        }
    }

    private void UpdateBackdropTheme(bool isLight)
    {
        if (isLight)
        {
            BackdropHelper.RemoveDarkMode(this);
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xF0, 0xF5, 0xF5, 0xF5));
        }
        else
        {
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
            BackdropHelper.ApplyDarkMode(this);
        }
    }

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } rb || rb.IsChecked != true)
            return;
        if (_homeView == null) return; // 构造期间忽略

        if (tag != "ScriptDownload")
        {
            _scriptDownloadViewModel.LogLines.Clear();
            _scriptDownloadViewModel.SearchResults.Clear();
            _scriptDownloadViewModel.StatusMessage = "";
        }
        if (tag != "Extraction")
        {
            _extractionViewModel.LogLines.Clear();
            _extractionViewModel.StatusMessage = "";
        }
        if (tag != "Settings")
        {
            _settingsViewModel.SpeedTestResults.Clear();
            _settingsViewModel.StatusMessage = "";
        }

        var prevIndex = Array.IndexOf(_navOrder, _prevTag);
        var newIndex = Array.IndexOf(_navOrder, tag);
        ContentTransition.Transition = TransitionType.Normal;
        _prevTag = tag;

        SwitchView(tag);
    }

    private void SwitchView(string tag)
    {
        UserControl? newView = tag switch
        {
            "Home" => _homeView,
            "Settings" => _settingsView,
            "ScriptDownload" => _scriptDownloadView,
            "NewGames" => _newGamesView,
            "Extraction" => _extractionView,
            "Trainer" => _trainerView,
            "Achievement" => _achievementView,
            "Authorization" => _authorizationView,
            "Save" => _saveView,
            "OnlineFix" => _onlineFixView,
            _ => null
        };

        if (newView is null || newView == ContentTransition.Content) return;
        ContentTransition.Content = newView;
        CurrentPage = tag;
        LogService.Info("导航", $"切换到 {tag}");
        UpdatePageHeader(tag);

        if (tag == "Trainer")
        {
            _ = _trainerViewModel.LoadSectionsCommand.ExecuteAsync(null);
        }
        if (tag == "Achievement")
        {
            _ = _achievementViewModel.EnsureLoadedAsync();
        }
        else if (tag == "Save")
        {
            _ = _saveViewModel.RefreshCommand.ExecuteAsync(null);
        }
    }

    private readonly DispatcherTimer _dropHintHideTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>判断文件是否为授权票据文件（tickets.txt，或内容含票据标记的 .txt、.cw、.shiki、.json）。</summary>
    private static bool IsTicketFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".cw" or ".shiki") return true;
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "tickets",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            if (ext is not (".txt" or ".json")) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1024];
            var n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return false;
            var head = Encoding.UTF8.GetString(buf, 0, n);
            return head.Contains("appid", StringComparison.OrdinalIgnoreCase) &&
                   (head.Contains("appticket", StringComparison.OrdinalIgnoreCase) ||
                    head.Contains("eticket", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTicketDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsTicketFile);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var isTicket = IsTicketDrop(e.Data);
        e.Effects = DragDropEffects.Copy;
        DropAuthHintGrid.Visibility = isTicket ? Visibility.Visible : Visibility.Collapsed;
        DropHintGrid.Visibility = isTicket ? Visibility.Collapsed : Visibility.Visible;
        _dropHintHideTimer.Stop();
        _dropHintHideTimer.Start();
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        DropHintGrid.Visibility = Visibility.Collapsed;
        DropAuthHintGrid.Visibility = Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        DropHintGrid.Visibility = Visibility.Collapsed;
        DropAuthHintGrid.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;

        var tickets = files.Where(IsTicketFile).ToArray();
        var others = files.Where(f => !IsTicketFile(f)).ToArray();

        if (tickets.Length > 0)
        {
            LogService.Info("操作", $"拖拽导入授权 {tickets.Length} 个文件: {string.Join("; ", tickets)}");
            foreach (var ticket in tickets)
                await HandleGlobalTicketDropAsync(ticket);
        }
        if (others.Length > 0)
        {
            LogService.Info("操作", $"拖拽入库 {others.Length} 个文件: {string.Join("; ", others)}");
            await _viewModel.HandleDropAsync(others);
        }
    }

    private async Task HandleGlobalTicketDropAsync(string ticketPath)
    {
        try
        {
            var authService = App.ServiceProvider?.GetRequiredService<IAuthorizationService>();
            if (authService == null)
            {
                MessageBox.Show("授权服务不可用", "导入授权", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var parsed = authService.ParseAuthFile(ticketPath);
            if (!parsed.Ok)
            {
                MessageBox.Show($"解析授权文件失败：{parsed.Error}", "导入授权失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existingGame = _viewModel.Games.FirstOrDefault(g => g.AppId == (int)parsed.AppId);
            var gameName = existingGame?.GameName ?? $"AppID {parsed.AppId}";

            if (existingGame == null)
            {
                var ask = MessageBox.Show(
                    $"检测到《{gameName}》的 D加密授权文件。\n\n当前游戏库中尚未添加该游戏（未入库）。\n\n是否立即一键入库该游戏并写入授权？\n（点击“确定”将自动生成清单加入 Steam 库，让游戏在 Steam 中直接可见并可启动）",
                    "发现新游戏授权",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (ask == MessageBoxResult.Yes)
                {
                    await _scriptDownloadViewModel.ImportGameAsync((int)parsed.AppId);
                    await _viewModel.RefreshGamesCommand.ExecuteAsync(null);

                    var ticket = new TicketData(parsed.AppId, parsed.SteamId, parsed.AppTicket!, parsed.ETicket!, ticketPath);
                    var importRes = authService.ImportTicket(ticket);

                    var postGame = _viewModel.Games.FirstOrDefault(g => g.AppId == (int)parsed.AppId);
                    var postName = postGame?.GameName ?? gameName;

                    var tip = !string.IsNullOrEmpty(importRes.Warning) ? $"\n\n注意：{importRes.Warning}" : "";
                    MessageBox.Show(
                        $"🎉 恭喜！《{postName}》已成功自动入库，且 D加密授权已写入生效！\n\n后续重启 Steam 即可在库中看到并直接启动游玩！{tip}",
                        "入库与授权成功",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }

            var ticketData = new TicketData(parsed.AppId, parsed.SteamId, parsed.AppTicket!, parsed.ETicket!, ticketPath);
            var result = authService.ImportTicket(ticketData);

            if (!string.IsNullOrEmpty(result.Warning) && result.AppTicketBytes == 0)
            {
                MessageBox.Show($"写入授权失败：{result.Warning}", "导入授权文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                var tip = !string.IsNullOrEmpty(result.Warning) ? $"\n\n注意：{result.Warning}" : "";
                MessageBox.Show($"《{gameName}》D加密授权已成功导入生效！\n\n后续直接从 Steam 正常启动该游戏即可。{tip}", "导入授权成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"处理授权文件异常：{ex.Message}", "导入授权失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LaunchSteamAsync()
    {
        try
        {
            var path = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(path))
            {
                await ShowModernDialogAsync("提示", "未检测到 Steam 安装路径，请先在设置页面配置");
                return;
            }

            var exePath = System.IO.Path.Combine(path, "steam.exe");
            if (!System.IO.File.Exists(exePath))
            {
                await ShowModernDialogAsync("提示", $"未找到 steam.exe：{exePath}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"启动 Steam 失败：{ex.Message}");
        }
    }

    private static void KillSteamProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                if (proc.Id != 0)
                    proc.Kill();
            }
        }
        catch { }
    }

    private class SteamAccount
    {
        public string SteamId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string AvatarHash { get; set; } = "";
        public string? AvatarPath { get; set; }
        public bool MostRecent { get; set; }
        public bool IsSeparator { get; set; }

        public static SteamAccount Separator() => new() { IsSeparator = true };
    }

    private async Task SwitchSteamAccountAsync(SteamAccount target)
    {
        try
        {
            KillSteamProcesses();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
            {
                if (key == null)
                {
                    await ShowModernDialogAsync("错误", "无法打开注册表 Steam 项");
                    return;
                }
                key.SetValue("AutoLoginUser", target.AccountName, RegistryValueKind.String);
            }
            await LaunchSteamAsync();
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"切换账号失败：{ex.Message}");
        }
    }

    private List<SteamAccount>? ParseLoginUsersVdf()
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return null;

        var vdfPath = System.IO.Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!System.IO.File.Exists(vdfPath)) return null;

        try
        {
            var content = System.IO.File.ReadAllText(vdfPath);
            var accounts = new List<SteamAccount>();

            foreach (Match blockMatch in Regex.Matches(content, "\\\"(\\d+)\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = blockMatch.Groups["body"].Value;
                var accountName = GetVdfValue(body, "AccountName");
                var personaName = GetVdfValue(body, "PersonaName");
                if (string.IsNullOrEmpty(accountName))
                    continue;

                accounts.Add(new SteamAccount
                {
                    SteamId = blockMatch.Groups[1].Value,
                    AccountName = accountName,
                    PersonaName = string.IsNullOrEmpty(personaName) ? accountName : personaName,
                    AvatarHash = GetVdfValue(body, "AvatarHash"),
                    MostRecent = GetVdfValue(body, "MostRecent") == "1"
                });
            }
            return accounts;
        }
        catch { return null; }
    }

    private static string GetVdfValue(string block, string key)
    {
        var match = Regex.Match(block, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private async Task InstallKernelAsync()
    {
        if (_openSteamToolService.IsInstalled)
        {
            var confirmed = await ShowModernConfirmAsync(
                "确认安装",
                "OpenSteamTool 已安装，是否仍要重新安装？这将覆盖现有文件。",
                "重新安装");
            if (!confirmed) return;
        }

        try
        {
            var (version, downloadUrl, _) = await _openSteamToolService.GetRemoteInfoAsync();
            if (string.IsNullOrEmpty(downloadUrl))
            {
                await ShowModernDialogAsync("错误", "无法获取最新版本下载链接");
                return;
            }

            ShowKernelOverlay("正在下载 OpenSteamTool...");
            _kernelCts = new CancellationTokenSource();
            try
            {
                var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
                var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
                ShowKernelDownloadHint();
                await _openSteamToolService.InstallAsync(downloadUrl, status, progress, _kernelCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _kernelCts?.Cancel();
                _kernelCts?.Dispose();
                _kernelCts = null;
                HideKernelOverlay();
            }

            await ShowModernDialogAsync("安装完成", $"OpenSteamTool {version} 安装成功！\n请重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"安装失败：{ex.Message}");
        }
    }

    private async Task UpdateKernelAsync()
    {
        if (!_openSteamToolService.IsInstalled)
        {
            await ShowModernDialogAsync("提示", "未检测到 OpenSteamTool，请先安装。");
            return;
        }

        var localVersion = await _openSteamToolService.GetLocalVersionAsync() ?? "未知";
        var localDisplay = localVersion;

        (string version, string downloadUrl, string releaseUrl)? remote = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var info = await _openSteamToolService.GetRemoteInfoAsync();
                if (!string.IsNullOrEmpty(info.downloadUrl))
                {
                    remote = info;
                    break;
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("内核", $"获取远程内核信息失败(第{attempt + 1}次): {ex.Message}");
            }

            if (attempt < 2)
            {
                var retry = await ShowModernConfirmAsync(
                    "获取失败",
                    $"无法从更新源获取内核版本信息（当前本地版本：{localDisplay}）。\n\n" +
                    "可能是网络波动或 GitHub 服务不稳定，是否重试？",
                    "重试");
                if (!retry) return;
            }
        }

        if (remote == null)
        {
            await ShowModernDialogAsync("错误", $"多次尝试后仍无法获取内核版本信息（本地版本：{localDisplay}）。\n\n请检查网络后重试。");
            return;
        }

        var (remoteVersion, downloadUrl, releaseUrl) = remote.Value;
        try
        {
            if (localVersion != "未知")
            {
                var localVer = Version.TryParse(localVersion, out var lv) ? lv : null;
                var remoteVer = Version.TryParse(remoteVersion, out var rv) ? rv : null;
                if (localVer != null && remoteVer != null && localVer >= remoteVer)
                {
                    await ShowModernDialogAsync("无需更新", $"当前已是最新版本。\n本地：{localVersion}\n仓库：{remoteVersion}");
                    return;
                }
            }

            var updateDialog = new ContentDialog
            {
                Title = "更新可用",
                Content = new TextBlock
                {
                    Text = $"发现新版本！\n\n当前版本：{localDisplay}\n最新版本：{remoteVersion}\n\n是否更新？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = "更新",
                SecondaryButtonText = "跳转发布页",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            var dialogResult = await updateDialog.ShowAsync();
            if (dialogResult == ContentDialogResult.Secondary)
            {
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                return;
            }
            if (dialogResult != ContentDialogResult.Primary) return;

            ShowKernelOverlay("正在下载 OpenSteamTool...");
            _kernelCts = new CancellationTokenSource();
            try
            {
                var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
                var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
                ShowKernelDownloadHint();
                await _openSteamToolService.InstallAsync(downloadUrl, status, progress, _kernelCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _kernelCts?.Cancel();
                _kernelCts?.Dispose();
                _kernelCts = null;
                HideKernelOverlay();
            }

            await ShowModernDialogAsync("更新完成", $"已更新至 {remoteVersion}！\n请重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"检查更新失败：{ex.Message}");
        }
    }

    private async Task UninstallKernelAsync()
    {
        var toolType = _steamPathService.DetectSteamToolType();
        if (toolType == SteamToolType.None)
        {
            await ShowModernDialogAsync("提示", "未检测到已安装的内核或残留文件。");
            return;
        }

        var isThirdParty = toolType == SteamToolType.SteamTools;
        var promptTitle = isThirdParty ? "清理第三方内核残留" : "确认卸载内核";
        var promptMsg = isThirdParty
            ? "检测到第三方 SteamTools（闭源）残留文件与配置。\n是否立即彻底清除其残留？\n\n注意：将自动退出 Steam 进程以解除文件占用。"
            : "确定要卸载 OpenSteamTool 内核吗？\n这将安全退出 Steam 并删除相关内核文件。";

        var confirmed = await ShowModernConfirmAsync(promptTitle, promptMsg, "清理并卸载");
        if (!confirmed) return;

        try
        {
            await _openSteamToolService.UninstallAsync();
            await ShowModernDialogAsync("清理完成", "内核文件与冲突配置已彻底清除！\n重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"清理失败：{ex.Message}");
        }
    }

    // ========== 侧边栏 Steam 快捷操作 ==========

    private void SteamQuickButton_Checked(object sender, RoutedEventArgs e)
    {
        SteamQuickPopup.IsOpen = true;
    }

    private void SteamQuickButton_Unchecked(object sender, RoutedEventArgs e)
    {
        SteamQuickPopup.IsOpen = false;
    }

    private void SteamQuickPopup_Closed(object? sender, EventArgs e)
    {
        SteamQuickButton.IsChecked = false;
    }

    private async void SteamStartButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info("操作", "侧边栏: 启动 Steam");
        await LaunchSteamAsync();
    }

    private async void SteamRestartButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info("操作", "侧边栏: 重启 Steam");
        KillSteamProcesses();
        await LaunchSteamAsync();
    }

    private async void SteamAccountButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info("操作", "侧边栏: 切换账号");
        await ShowAccountPickerAsync();
    }

    private async Task ShowAccountPickerAsync()
    {
        var accounts = ParseLoginUsersVdf();
        if (accounts == null || accounts.Count == 0)
        {
            await ShowModernDialogAsync("提示", "未找到可切换的 Steam 账号，请先登录过 Steam");
            return;
        }

        var steamPath = _steamPathService.DetectSteamPath();
        if (!string.IsNullOrEmpty(steamPath))
        {
            foreach (var acc in accounts)
            {
                var avatarPath = System.IO.Path.Combine(steamPath, "config", "avatarcache", $"{acc.SteamId}.png");
                if (System.IO.File.Exists(avatarPath))
                    acc.AvatarPath = avatarPath;
            }
        }

        var listBox = new ListBox
        {
            MaxHeight = 340,
            MinWidth = 300,
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 0)
        };
        foreach (var acc in accounts)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            if (!string.IsNullOrEmpty(acc.AvatarPath))
            {
                panel.Children.Add(new Border
                {
                    Width = 30,
                    Height = 30,
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Margin = new Thickness(0, 0, 10, 0),
                    Child = new Image
                    {
                        Source = new BitmapImage(new Uri(acc.AvatarPath)),
                        Width = 30,
                        Height = 30,
                        Stretch = Stretch.UniformToFill
                    }
                });
            }
            panel.Children.Add(new TextBlock
            {
                Text = $"{acc.PersonaName}（{acc.AccountName}）",
                VerticalAlignment = VerticalAlignment.Center
            });
            listBox.Items.Add(new ListBoxItem { Content = panel, Tag = acc });
        }

        var dialog = new ContentDialog
        {
            Title = "切换 Steam 账号",
            Content = listBox,
            PrimaryButtonText = "切换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && listBox.SelectedItem is ListBoxItem { Tag: SteamAccount target })
            await SwitchSteamAccountAsync(target);
    }

    // ========== 侧边栏内核管理 ==========

    private void KernelButton_Checked(object sender, RoutedEventArgs e)
    {
        KernelPopup.IsOpen = true;
    }

    private void KernelButton_Unchecked(object sender, RoutedEventArgs e)
    {
        KernelPopup.IsOpen = false;
    }

    private void KernelPopup_Closed(object? sender, EventArgs e)
    {
        KernelButton.IsChecked = false;
    }

    private async void KernelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info("操作", "侧边栏: 安装内核");
        await InstallKernelAsync();
    }

    private async void KernelUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info("操作", "侧边栏: 更新内核");
        await UpdateKernelAsync();
    }

    private async void KernelUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info("操作", "侧边栏: 卸载内核");
        await UninstallKernelAsync();
    }

    // ========== 页面标题 ==========

    private void UpdatePageHeader(string tag)
    {
        var (title, subtitle) = tag switch
        {
            "Home" => ("我的游戏库", "管理已入库的 Steam 游戏 · 支持直接拖入清单与授权文件"),
            "ScriptDownload" => ("入库管理", "搜索并入库新的游戏"),
            "NewGames" => ("热门游戏", "热门游戏推荐与一键入库"),
            "Trainer" => ("修改器", "支持中英文搜索、热门推荐与一键下载"),
            "Achievement" => ("成就管理", "解锁或回锁已拥有的游戏成就"),
            "Save" => ("存档管理", "本地备份、云端同步与完美存档一键替换"),
            "OnlineFix" => ("在线联机", "以 SpaceWar(480) 身份启动游戏，使用大厅匹配与好友联机"),
            "Settings" => ("设置", "软件设置与皮肤选择"),
            _ => (tag, string.Empty)
        };
        PageTitleText.Text = title;
        PageSubtitleText.Text = subtitle;
    }
}
