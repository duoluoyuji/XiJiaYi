using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;
using SteamLuaManager.Views;

namespace SteamLuaManager;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }

    private Mutex? _singleInstanceMutex;

    /// <summary>单实例互斥量名称（fix 在 worker 子进程逻辑之后）。</summary>
    private const string SingleInstanceMutexName = "XiJiaYi_SingleInstance";

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

    protected override void OnStartup(StartupEventArgs e)
    {
        // worker 子进程模式：仅用于成就/统计数据的读取与写回，
        // 通过进程启动时设置 SteamAppId 避免单进程上下文固定问题
        if (e.Args.Length >= 1 && e.Args[0] == "--worker")
        {
            int code;
            try
            {
                code = StatsWorker.Run(e.Args);
            }
            catch
            {
                code = -1;
            }
            Shutdown(code);
            return;
        }

        // 授权提取 worker 子进程：一次性提取 AppTicket / ETicket 并写入结果文件
        if (e.Args.Length >= 3 && e.Args[0] == "--ticket-worker")
        {
            // 日志输出遵循用户设置：主进程传入第 4 个参数（1=开启）决定是否记录
            if (e.Args.Length >= 4 && e.Args[3] == "1")
            {
                LogService.SetEnabled(true);
                LogService.Info("提取", $"worker 启动 args=[{string.Join("|", e.Args)}]");
            }
            int code;
            try
            {
                code = TicketWorker.Run(e.Args[1], e.Args[2]);
            }
            catch (Exception ex)
            {
                LogService.Error("提取", $"worker 未捕获异常: {ex}");
                code = -1;
            }
            if (LogService.IsEnabled)
                LogService.Info("提取", $"worker 结束 exit={code}");
            Shutdown(code);
            return;
        }

        // 单实例限制：已有实例时激活其窗口并退出（worker 子进程不参与）
        var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            System.Windows.MessageBox.Show("程序已在运行，重复启动失败。",
                "喜加一",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        _singleInstanceMutex = mutex;

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        var settingsService = ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = settingsService.Load();

        LogService.SetEnabled(settings.EnableLogging);
        LogService.Info("系统", $"程序启动，版本 {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        LogService.Info("系统", $"操作系统: {Environment.OSVersion.VersionString}, .NET: {Environment.Version}, 进程: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        var steamPathService = ServiceProvider.GetRequiredService<ISteamPathService>();
        LogService.Info("系统", $"Steam 路径: {steamPathService.DetectSteamPath() ?? "未检测到"}");
        RegisterGlobalLogging();
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

        // 本版本固定为纯黑深色主题
        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        SkinCatalog.Apply(settings.Skin);

        mainWindow.Show();

        var autoLaunch = ServiceProvider.GetRequiredService<ITrainerAutoLaunchService>();
        autoLaunch.Start();
        mainWindow.Closed += (_, _) => autoLaunch.Dispose();

        _ = Task.Run(async () =>
        {
            try
            {
                var depotService = ServiceProvider.GetRequiredService<ISteamDepotService>();
                await depotService.EnsureAllSourcesAsync();
            }
            catch { }
        });

        base.OnStartup(e);
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var hwnd = FindWindow(null, "喜加一");
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Info("系统", "程序退出");
        LogService.Shutdown();
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    private void RegisterGlobalLogging()
    {
        DispatcherUnhandledException += (_, args) =>
            LogService.Exception("UI未处理异常", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Exception("AppDomain异常",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "未知异常"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Exception("后台任务异常", args.Exception);
            args.SetObserved();
        };
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler(OnGlobalButtonClick));
    }

    private static void OnGlobalButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button) return;
        var desc = DescribeButtonContent(button.Content);
        if (string.IsNullOrEmpty(desc))
            desc = string.IsNullOrEmpty(button.Name) ? button.GetType().Name : button.Name;
        LogService.Info("操作", $"[{Views.MainWindow.CurrentPage ?? "?"}] 点击按钮: {desc}");
    }

    private static string? DescribeButtonContent(object? content)
    {
        switch (content)
        {
            case string s when !string.IsNullOrWhiteSpace(s):
                return s;
            case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                return tb.Text;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    var desc = DescribeButtonContent(child);
                    if (!string.IsNullOrEmpty(desc)) return desc;
                }
                break;
        }
        return null;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISteamPathService, SteamPathService>();
        services.AddSingleton<ILuaFileManager, LuaFileManager>();
        services.AddSingleton<ISteamApiService, SteamApiService>();
        services.AddSingleton<ISteamManifestService, SteamManifestService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
        services.AddSingleton<ISteamDepotService, SteamDepotService>();
        services.AddSingleton<IOpenSteamToolService, OpenSteamToolService>();
        services.AddSingleton<ITrainerService, TrainerService>();
        services.AddSingleton<ITrainerAutoLaunchService, TrainerAutoLaunchService>();
        services.AddSingleton<ISteamAchievementService, SteamAchievementService>();
        services.AddSingleton<SteamTicketExtractor>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();
        services.AddSingleton<ISaveService, SaveService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<TrainerViewModel>();
        services.AddTransient<AchievementViewModel>();
        services.AddTransient<AuthorizationViewModel>();
        services.AddTransient<SaveViewModel>();
        services.AddTransient<MainWindow>();
    }
}
