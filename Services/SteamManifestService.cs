using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public class SteamManifestService : ISteamManifestService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;

    public SteamManifestService(IHttpClientProvider httpClientProvider, ISteamPathService steamPathService)
    {
        _httpClientProvider = httpClientProvider;
        _steamPathService = steamPathService;
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        if (!client.DefaultRequestHeaders.Accept.Any())
            client.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public Dictionary<int, string> ParseMountedDepots(string acfPath)
    {
        var result = new Dictionary<int, string>();
        try
        {
            var content = File.ReadAllText(acfPath);

            var depotMatches = Regex.Matches(content,
                @"""(\d+)""\s*\{\s*""manifest""\s+""(\d+)""",
                RegexOptions.Singleline);
            foreach (Match match in depotMatches)
            {
                if (int.TryParse(match.Groups[1].Value, out var depotId))
                    result[depotId] = match.Groups[2].Value;
            }

            if (result.Count == 0)
            {
                var mountSection = Regex.Match(content,
                    @"""MountedDepots""\s*\{([^}]*)\}",
                    RegexOptions.Singleline);
                if (mountSection.Success)
                {
                    var inner = mountSection.Groups[1].Value;
                    var pairs = Regex.Matches(inner, @"""(\d+)""\s+""(\d+)""");
                    foreach (Match pair in pairs)
                    {
                        if (int.TryParse(pair.Groups[1].Value, out var depotId))
                            result[depotId] = pair.Groups[2].Value;
                    }
                }
            }
        }
        catch (Exception ex) { LogService.Warn("版本固定", $"解析 MountedManifests 失败: {ex.Message}"); }
        return result;
    }

    public async Task<string?> FetchLatestManifestIdAsync(int appId, int depotId)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-manifest",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(url),
                ConfigureHeaders);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty(appId.ToString(), out var app)) return null;
            if (!app.TryGetProperty("depots", out var depots)) return null;
            if (!depots.TryGetProperty(depotId.ToString(), out var depot)) return null;
            if (!depot.TryGetProperty("manifests", out var manifests)) return null;
            if (!manifests.TryGetProperty("public", out var pub)) return null;
            if (!pub.TryGetProperty("gid", out var gid)) return null;

            return gid.GetString();
        }
        catch (Exception ex) { LogService.Warn("版本固定", $"查询最新清单 ID 失败 (AppID {appId}, Depot {depotId}): {ex.Message}"); }
        return null;
    }

    public async Task<(bool success, int count, string message)> EnsureManifestsCachedAsync(
        int appId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
                return (false, 0, "未检测到 Steam 安装路径，请先在「设置」中指定有效的 Steam 根目录。");

            var depotcacheDir = Path.Combine(steamPath, "depotcache");
            var configDepotDir = Path.Combine(steamPath, "config", "depotcache");
            if (!Directory.Exists(depotcacheDir))
                Directory.CreateDirectory(depotcacheDir);
            if (!Directory.Exists(configDepotDir))
                Directory.CreateDirectory(configDepotDir);

            // 先执行一轮现有清单双向同步，避免遗漏
            await SyncDepotcacheAsync(ct);

            progress?.Report($"正在检索 AppID {appId} 的可用清单文件列表...");

            // 1. 查询清单镜像库中的文件列表（优先 git trees API，其次 contents API）
            var treeUrls = new[]
            {
                $"https://api.github.com/repos/P-ToyStore/SteamManifestCache_Pro/git/trees/{appId}",
                $"https://api.github.com/repos/P-ToyStore/SteamManifestCache_Pro/contents?ref={appId}",
                $"https://ghproxy.net/https://api.github.com/repos/P-ToyStore/SteamManifestCache_Pro/git/trees/{appId}"
            };

            var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in treeUrls)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var json = await _httpClientProvider.SendWithProxyRetryAsync(
                        "manifest-cache-list",
                        TimeSpan.FromSeconds(12),
                        client => client.GetStringAsync(url),
                        client =>
                        {
                            if (!client.DefaultRequestHeaders.UserAgent.Any())
                                client.DefaultRequestHeaders.UserAgent.ParseAdd("XiJiaYi/1.0");
                            if (!client.DefaultRequestHeaders.Accept.Any())
                                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                        });

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("tree", out var treeElement) && treeElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in treeElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("path", out var pathProp))
                            {
                                var p = pathProp.GetString();
                                if (!string.IsNullOrEmpty(p) && p.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                                    manifestFiles.Add(Path.GetFileName(p));
                            }
                        }
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            if (item.TryGetProperty("name", out var nameProp))
                            {
                                var n = nameProp.GetString();
                                if (!string.IsNullOrEmpty(n) && n.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                                    manifestFiles.Add(n);
                            }
                        }
                    }

                    if (manifestFiles.Count > 0)
                        break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 404 说明号池分支尚未收录该 AppID
                    return (false, 0, $"清单镜像库中暂未收录 AppID {appId} 的清单文件。\n对于极冷门或刚发售游戏，通常需要等待号池同步，或通过分流包导入。");
                }
                catch (Exception ex)
                {
                    LogService.Warn("清单同步", $"请求清单列表失败 ({url}): {ex.Message}");
                }
            }

            if (manifestFiles.Count == 0)
                return (false, 0, $"未能获取到 AppID {appId} 的有效清单信息，可能该游戏未收录或网络访问异常。");

            var downloadedCount = 0;
            var existingCount = 0;

            foreach (var manifestName in manifestFiles)
            {
                ct.ThrowIfCancellationRequested();

                var targetPath = Path.Combine(depotcacheDir, manifestName);
                var targetConfigPath = Path.Combine(configDepotDir, manifestName);

                var existsInA = File.Exists(targetPath) && new FileInfo(targetPath).Length > 0;
                var existsInB = File.Exists(targetConfigPath) && new FileInfo(targetConfigPath).Length > 0;

                if (existsInA && !existsInB)
                {
                    try { File.Copy(targetPath, targetConfigPath, true); } catch { }
                    existingCount++;
                    continue;
                }
                if (!existsInA && existsInB)
                {
                    try { File.Copy(targetConfigPath, targetPath, true); } catch { }
                    existingCount++;
                    continue;
                }
                if (existsInA && existsInB)
                {
                    existingCount++;
                    continue;
                }

                progress?.Report($"正在下载清单: {manifestName}...");

                // 多源重试下载单文件
                var downloadCandidates = new[]
                {
                    $"https://raw.githubusercontent.com/P-ToyStore/SteamManifestCache_Pro/{appId}/{manifestName}",
                    $"https://raw.gitmirror.com/P-ToyStore/SteamManifestCache_Pro/{appId}/{manifestName}",
                    $"https://ghproxy.net/https://raw.githubusercontent.com/P-ToyStore/SteamManifestCache_Pro/{appId}/{manifestName}",
                    $"https://cdn.jsdelivr.net/gh/P-ToyStore/SteamManifestCache_Pro@{appId}/{manifestName}"
                };

                var downloaded = false;
                foreach (var dlUrl in downloadCandidates)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var bytes = await _httpClientProvider.SendWithProxyRetryAsync(
                            "manifest-dl",
                            TimeSpan.FromSeconds(25),
                            client => client.GetByteArrayAsync(dlUrl),
                            client =>
                            {
                                if (!client.DefaultRequestHeaders.UserAgent.Any())
                                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                            });

                        if (bytes != null && bytes.Length > 0)
                        {
                            await File.WriteAllBytesAsync(targetPath, bytes, ct);
                            await File.WriteAllBytesAsync(targetConfigPath, bytes, ct);
                            downloaded = true;
                            downloadedCount++;
                            LogService.Info("清单同步", $"成功下载清单 {manifestName} ({bytes.Length} 字节) 并写入两处 depotcache");
                            break;
                        }
                    }
                    catch (Exception dlEx)
                    {
                        LogService.Warn("清单同步", $"从 {dlUrl} 下载清单失败: {dlEx.Message}");
                    }
                }

                if (!downloaded)
                {
                    LogService.Warn("清单同步", $"所有镜像源下载清单 {manifestName} 均失败");
                }
            }

            await SyncDepotcacheAsync(ct);

            var total = downloadedCount + existingCount;
            return (true, total, $"成功同步 {total} 个清单文件（新增下载 {downloadedCount} 个，已就绪 {existingCount} 个）至 depotcache！\n您现在可以在 Steam 客户端中点击安装/下载。");
        }
        catch (OperationCanceledException)
        {
            return (false, 0, "清单下载已取消。");
        }
        catch (Exception ex)
        {
            LogService.Error("清单同步", $"补齐清单发生异常: {ex.Message}");
            return (false, 0, $"补齐清单失败: {ex.Message}");
        }
    }

    public Task<(int syncedAtoB, int syncedBtoA)> SyncDepotcacheAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
                return (0, 0);

            var dirA = Path.Combine(steamPath, "depotcache");
            var dirB = Path.Combine(steamPath, "config", "depotcache");

            if (!Directory.Exists(dirA) && !Directory.Exists(dirB))
                return (0, 0);

            if (!Directory.Exists(dirA)) Directory.CreateDirectory(dirA);
            if (!Directory.Exists(dirB)) Directory.CreateDirectory(dirB);

            int syncedAtoB = 0;
            int syncedBtoA = 0;

            // Sync A (depotcache) -> B (config/depotcache)
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirA, "*.manifest"))
                {
                    if (ct.IsCancellationRequested) break;
                    var name = Path.GetFileName(file);
                    var target = Path.Combine(dirB, name);
                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length)
                    {
                        File.Copy(file, target, true);
                        syncedAtoB++;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("清单同步", $"从 depotcache 同步到 config/depotcache 失败: {ex.Message}");
            }

            // Sync B (config/depotcache) -> A (depotcache)
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirB, "*.manifest"))
                {
                    if (ct.IsCancellationRequested) break;
                    var name = Path.GetFileName(file);
                    var target = Path.Combine(dirA, name);
                    if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(file).Length)
                    {
                        File.Copy(file, target, true);
                        syncedBtoA++;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("清单同步", $"从 config/depotcache 同步到 depotcache 失败: {ex.Message}");
            }

            if (syncedAtoB > 0 || syncedBtoA > 0)
            {
                LogService.Info("清单同步", $"已完成清单双向增量同步: depotcache->config 同步 {syncedAtoB} 个, config->depotcache 同步 {syncedBtoA} 个");
            }

            return (syncedAtoB, syncedBtoA);
        }, ct);
    }
}
