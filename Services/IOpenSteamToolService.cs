using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public interface IOpenSteamToolService
{
    bool IsInstalled { get; }
    string? GetSteamPath();
    Task<string?> GetLocalVersionAsync();
    Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync();
    Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default);
    Task InstallEmbeddedAsync(IProgress<string>? status = null);
    Task UninstallAsync();
}

public class OpenSteamToolService : IOpenSteamToolService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;
    private const string OfficialRepo = "OpenSteam001/OpenSteamTool";
    private const string ForkRepo = "pvzcxw/OpenSteamTool";
    private static readonly string[] RequiredDlls = ["dwmapi.dll", "KeySteamTool.dll", "cloud_redirect.dll", "xinput1_4.dll"];

    public OpenSteamToolService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider, ISettingsService settingsService)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;
        _settingsService = settingsService;
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XiJiaYi/1.0");
    }

    public bool IsInstalled
    {
        get
        {
            var type = _steamPathService.DetectSteamToolType();
            return type == SteamToolType.KeySteamTool || type == SteamToolType.OpenSteamTool;
        }
    }

    public string? GetSteamPath()
    {
        var path = _steamPathService.DetectSteamPath();
        return !string.IsNullOrEmpty(path) ? path : null;
    }

    public Task<string?> GetLocalVersionAsync()
    {
        var steamPath = GetSteamPath();
        if (steamPath == null) return Task.FromResult<string?>(null);

        var kstPath = Path.Combine(steamPath, "KeySteamTool.dll");
        if (File.Exists(kstPath))
        {
            return Task.FromResult<string?>("运行核心驱动 v2.99");
        }

        var dwmPath = Path.Combine(steamPath, "dwmapi.dll");
        if (File.Exists(dwmPath) && Directory.Exists(Path.Combine(steamPath, "config", "stplug-in")))
        {
            return Task.FromResult<string?>("运行核心驱动 (LTS)");
        }

        var ostPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (File.Exists(ostPath))
        {
            return Task.FromResult<string?>("旧版驱动 (已废弃)");
        }

        return Task.FromResult<string?>(null);
    }

    public Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync()
    {
        return Task.FromResult(("v2.99", "", "https://github.com/"));
    }

    public Task InstallEmbeddedAsync(IProgress<string>? status = null)
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        status?.Report("正在安全退出 Steam 进程...");
        TryKillSteamProcesses();

        CleanLegacyConflictFiles(steamPath);

        status?.Report("正在解压并安装运行核心驱动文件...");
        using var stream = typeof(OpenSteamToolService).Assembly.GetManifestResourceStream("SteamLuaManager.Resources.KeySteamTool.zip");
        if (stream == null)
            throw new InvalidOperationException("未找到内置的运行核心驱动离线包资源");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            var fileName = Path.GetFileName(entry.Name);
            if (string.IsNullOrEmpty(fileName)) continue;

            var targetPath = Path.Combine(steamPath, fileName);
            entry.ExtractToFile(targetPath, overwrite: true);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidOperationException("驱动安装包中未找到有效的驱动文件");

        // 确保插件目录与清单缓存目录就绪
        var stPluginDir = Path.Combine(steamPath, "config", "stplug-in");
        if (!Directory.Exists(stPluginDir))
        {
            Directory.CreateDirectory(stPluginDir);
        }

        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        if (!Directory.Exists(depotCacheDir))
        {
            Directory.CreateDirectory(depotCacheDir);
        }

        var configDepotCacheDir = Path.Combine(steamPath, "config", "depotcache");
        if (!Directory.Exists(configDepotCacheDir))
        {
            Directory.CreateDirectory(configDepotCacheDir);
        }

        status?.Report("运行核心驱动安装就绪");
        LogService.Info("驱动", "运行核心驱动已成功部署至 Steam 根目录及插件目录。");
        return Task.CompletedTask;
    }

    public async Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        await InstallEmbeddedAsync(status);
    }

    public Task UninstallAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        TryKillSteamProcesses();
        CleanLegacyConflictFiles(steamPath);

        foreach (var dll in RequiredDlls)
        {
            var path = Path.Combine(steamPath, dll);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { LogService.Warn("驱动卸载", $"删除 {dll} 失败: {ex.Message}"); }
            }
        }

        var cfgPath = Path.Combine(steamPath, "steam.cfg");
        if (File.Exists(cfgPath))
        {
            try { File.Delete(cfgPath); } catch { }
        }

        LogService.Info("驱动", "运行核心驱动已成功卸载。");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 清除旧版废弃文件（如 OpenSteamTool.dll、toml 配置文件等）。
    /// 注意：严禁删除 steam.cfg 与 config\stplug-in 目录。
    /// </summary>
    private static void CleanLegacyConflictFiles(string steamPath)
    {
        var obsoleteFiles = new[] { "OpenSteamTool.dll", "opensteamtool.toml", "opensteamtool.example.toml", "hid.dll" };
        foreach (var file in obsoleteFiles)
        {
            var p = Path.Combine(steamPath, file);
            if (File.Exists(p))
            {
                try { File.Delete(p); } catch { }
            }
        }
    }

    public static void TryKillSteamProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                try { proc.Kill(); proc.WaitForExit(3000); } catch { }
            }
            foreach (var proc in Process.GetProcessesByName("steamwebhelper"))
            {
                try { proc.Kill(); proc.WaitForExit(1000); } catch { }
            }
        }
        catch { }
    }
}
