using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

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
    private const string OfficialRepo = "mmxlyo/OpenSteamTool";
    private static readonly string[] RequiredDlls = ["OpenSteamTool.dll", "dwmapi.dll", "xinput1_4.dll"];

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
            return type == SteamToolType.OpenSteamTool || type == SteamToolType.KeySteamTool;
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

        var ostPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (File.Exists(ostPath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(ostPath);
                var ver = vi.ProductVersion ?? vi.FileVersion;
                if (!string.IsNullOrWhiteSpace(ver) && ver != "0.0.0.0")
                {
                    return Task.FromResult<string?>($"OpenSteamTool v{ver}");
                }
            }
            catch { }
            return Task.FromResult<string?>("OpenSteamTool v1.4.8.15");
        }

        var kstPath = Path.Combine(steamPath, "KeySteamTool.dll");
        if (File.Exists(kstPath))
        {
            return Task.FromResult<string?>("旧版 KeySteam (已失效，请升级)");
        }

        var dwmPath = Path.Combine(steamPath, "dwmapi.dll");
        if (File.Exists(dwmPath) && Directory.Exists(Path.Combine(steamPath, "config", "stplug-in")))
        {
            return Task.FromResult<string?>("旧版驱动 (已失效，请升级)");
        }

        return Task.FromResult<string?>(null);
    }

    public async Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync()
    {
        const string fallbackVersion = "v1.4.8.15-fix2";
        const string fallbackRelease = "https://github.com/mmxlyo/OpenSteamTool/releases";
        const string fallbackDownload = "https://github.com/mmxlyo/OpenSteamTool/releases/download/1.4.8.15-fix2/OpenSteamTool-1.4.8.15-fix2-Release.zip";

        try
        {
            var client = _httpClientProvider.GetClient("GitHub", TimeSpan.FromSeconds(10), ConfigureHeaders);
            var url = $"https://api.github.com/repos/{OfficialRepo}/releases/latest";
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var tagName = root.GetProperty("tag_name").GetString() ?? fallbackVersion;
                var htmlUrl = root.GetProperty("html_url").GetString() ?? fallbackRelease;
                var downloadUrl = fallbackDownload;

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("Release", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? downloadUrl;
                            break;
                        }
                    }
                }

                return (tagName, downloadUrl, htmlUrl);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("驱动更新", $"获取远程 OpenSteamTool 版本失败: {ex.Message}");
        }

        return (fallbackVersion, fallbackDownload, fallbackRelease);
    }

    public Task InstallEmbeddedAsync(IProgress<string>? status = null)
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        status?.Report("正在安全退出 Steam 进程...");
        TryKillSteamProcesses();

        CleanLegacyConflictFiles(steamPath);

        status?.Report("正在解压并安装 OpenSteamTool 内核文件...");
        using var stream = typeof(OpenSteamToolService).Assembly.GetManifestResourceStream("SteamLuaManager.Resources.OpenSteamTool.zip");
        if (stream == null)
            throw new InvalidOperationException("未找到内置的 OpenSteamTool 内核离线包资源");

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

        // 确保 Lua 脚本目录与清单缓存目录就绪
        var luaDir = Path.Combine(steamPath, "config", "lua");
        if (!Directory.Exists(luaDir))
        {
            Directory.CreateDirectory(luaDir);
        }

        // 兼容迁移：如果旧版的 config\stplug-in 存在 Lua 脚本，自动同步一份到 config\lua
        var oldStPluginDir = Path.Combine(steamPath, "config", "stplug-in");
        if (Directory.Exists(oldStPluginDir))
        {
            try
            {
                foreach (var luaFile in Directory.GetFiles(oldStPluginDir, "*.lua"))
                {
                    var dest = Path.Combine(luaDir, Path.GetFileName(luaFile));
                    if (!File.Exists(dest))
                    {
                        File.Copy(luaFile, dest, overwrite: false);
                    }
                }
            }
            catch { }
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

        status?.Report("OpenSteamTool 开源内核安装就绪");
        LogService.Info("驱动", "OpenSteamTool 开源内核已成功部署至 Steam 根目录及 Lua 配置目录。");
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

        var additionalFiles = new[] { "ost-Injector.exe", "config.ini", "opensteamtool.toml", "CreateAutoInjectTask.bat", "DeleteAutoInjectTask.bat" };
        foreach (var file in additionalFiles)
        {
            var path = Path.Combine(steamPath, file);
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }

        // 清理开机自启注入项（若曾配置）
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            runKey?.DeleteValue("OpenSteamTool_AutoInject", throwOnMissingValue: false);
        }
        catch { }

        LogService.Info("驱动", "OpenSteamTool 内核已成功卸载。");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 清除旧版废弃冲突文件（如旧版 KeySteamTool.dll、cloud_redirect.dll、旧版 rogue dwmapi.dll 等）。
    /// </summary>
    private static void CleanLegacyConflictFiles(string steamPath)
    {
        var obsoleteFiles = new[]
        {
            "KeySteamTool.dll",
            "cloud_redirect.dll",
            "hid.dll"
        };

        foreach (var file in obsoleteFiles)
        {
            var p = Path.Combine(steamPath, file);
            if (File.Exists(p))
            {
                try { File.Delete(p); } catch { }
            }
        }

        // 清理可能遗留在 bin 目录下的老旧劫持 dll
        var binDwm = Path.Combine(steamPath, "bin", "dwmapi.dll");
        if (File.Exists(binDwm))
        {
            try { File.Delete(binDwm); } catch { }
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
