using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const string OfficialRepo = "huanyuejue/OpenSteamTool";
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
            return Task.FromResult<string?>("OpenSteamTool（已安装）");
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
        const string fallbackVersion = "1.5.3.8";
        const string fallbackRelease = "https://github.com/huanyuejue/OpenSteamTool/releases";
        const string fallbackDownload = "https://github.com/huanyuejue/OpenSteamTool/releases/download/1.5.3.8/OpenSteamTool-1.5.3.8-Release.zip";

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

        if (InstallFromArchive(stream, steamPath) == 0)
            throw new InvalidOperationException("驱动安装包中未找到有效的驱动文件");

        PrepareDirectoriesAndToml(steamPath);

        status?.Report("OpenSteamTool 开源内核安装就绪");
        LogService.Info("驱动", "OpenSteamTool 开源内核已成功部署至 Steam 根目录及 Lua 配置目录。");
        return Task.CompletedTask;
    }

    public async Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            await InstallEmbeddedAsync(status);
            return;
        }

        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");

        status?.Report("正在下载最新内核...");
        var client = _httpClientProvider.GetClient("GitHub", TimeSpan.FromMinutes(5), ConfigureHeaders);
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var network = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long received = 0;
        int read;
        while ((read = await network.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            received += read;
            if (total > 0)
                downloadProgress?.Report((int)(received * 100 / total));
        }

        if (buffer.Length == 0)
            throw new InvalidOperationException("下载的内核安装包为空");
        buffer.Position = 0;

        status?.Report("正在安全退出 Steam 进程...");
        TryKillSteamProcesses();

        CleanLegacyConflictFiles(steamPath);

        status?.Report("正在解压并覆盖内核文件...");
        if (InstallFromArchive(buffer, steamPath) == 0)
            throw new InvalidOperationException("内核安装包中未找到有效的驱动文件");

        PrepareDirectoriesAndToml(steamPath);

        status?.Report("OpenSteamTool 内核已更新");
        LogService.Info("驱动", $"OpenSteamTool 内核已从远程更新：{downloadUrl}");
    }

    /// <summary>把内核包内文件平铺解压到 Steam 根目录，返回解压文件数。</summary>
    private static int InstallFromArchive(Stream zipStream, string steamPath)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            var fileName = Path.GetFileName(entry.Name);
            if (string.IsNullOrEmpty(fileName)) continue;

            var targetPath = Path.Combine(steamPath, fileName);
            entry.ExtractToFile(targetPath, overwrite: true);
            extracted++;
        }
        return extracted;
    }

    /// <summary>准备 Lua / depotcache 目录，并在缺少配置文件时补一份默认 toml。</summary>
    private static void PrepareDirectoriesAndToml(string steamPath)
    {
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

        // 新版内核不再附带 opensteamtool.toml，首次安装时补一份默认配置
        var tomlPath = Path.Combine(steamPath, "opensteamtool.toml");
        if (!File.Exists(tomlPath))
        {
            try
            {
                File.WriteAllText(tomlPath,
                    "# 由喜加一生成\n[log]\nlevel = \"info\"\n\n[lua]\npaths = [\"config/stplug-in\"]\n");
            }
            catch (Exception ex) { LogService.Warn("驱动", $"写入默认 opensteamtool.toml 失败: {ex.Message}"); }
        }
        else
        {
            NormalizeManifestUpstream(tomlPath);
        }
    }

    /// <summary>旧配置里的清单上游（如 manifestdex）新版内核不认识，统一改成内核支持的 20770407。</summary>
    private static void NormalizeManifestUpstream(string tomlPath)
    {
        try
        {
            var text = File.ReadAllText(tomlPath);
            var match = Regex.Match(text, @"(?m)^\s*url\s*=\s*""([^""]*)""\s*$");
            if (!match.Success) return;

            var value = match.Groups[1].Value.Trim();
            if (value is "20770407" or "opensteamtool" or "steamrun" or "wudrm" or "SDM") return;

            var replaced = text.Remove(match.Groups[1].Index, match.Groups[1].Length).Insert(match.Groups[1].Index, "20770407");
            if (!Regex.IsMatch(replaced, @"(?m)^\s*failover\s*="))
                replaced = replaced + "\nfailover = true\n";

            File.WriteAllText(tomlPath, replaced);
            LogService.Info("驱动", $"opensteamtool.toml 清单上游已由 {value} 修正为 20770407");
        }
        catch (Exception ex) { LogService.Warn("驱动", $"修正 opensteamtool.toml 失败: {ex.Message}"); }
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

        var additionalFiles = new[] { "ost-Injector.exe", "config.ini", "opensteamtool.toml", "CreateAutoInjectTask.bat", "DeleteAutoInjectTask.bat", "version.dll", "version.dll.disabled" };
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
