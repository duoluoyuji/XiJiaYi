using System.IO;
using System.IO.Compression;
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

            progress?.Report($"正在解析 AppID {appId} 的 Depot 清单信息...");

            var depotManifests = await ResolveDepotsForAppAsync(appId, steamPath, ct);
            if (depotManifests.Count == 0)
            {
                return (false, 0, $"未能获取到 AppID {appId} 的有效 Depot 清单信息，可能该游戏无需额外清单或网络请求超时。");
            }

            var downloadedCount = 0;
            var existingCount = 0;

            foreach (var kvp in depotManifests)
            {
                ct.ThrowIfCancellationRequested();

                var depotId = kvp.Key;
                var manifestId = kvp.Value;
                var manifestName = $"{depotId}_{manifestId}.manifest";

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

                progress?.Report($"正在获取 Depot {depotId} 清单授权码...");

                var codeSources = new (string url, string ua)[]
                {
                    ($"https://20770407.xyz/manifest/{depotId}/{manifestId}", "Mozilla/5.0"),
                    ($"https://steamapi.993499094.xyz/manifest/{depotId}/{manifestId}", "Mozilla/5.0"),
                    ($"https://manifest.manifestdex.com/{depotId}", "ManifestDeX/1.0"),
                    ($"https://manifest.steam.run/api/manifest/{depotId}", "Mozilla/5.0"),
                    ($"http://gmrc.wudrm.com/manifest/{depotId}", "Mozilla/5.0")
                };

                string? code = null;
                foreach (var (srcUrl, ua) in codeSources)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var resp = await _httpClientProvider.SendWithProxyRetryAsync(
                            "manifest-code",
                            TimeSpan.FromSeconds(6),
                            client => client.GetStringAsync(srcUrl, ct),
                            client =>
                            {
                                client.DefaultRequestHeaders.UserAgent.Clear();
                                client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
                            });

                        var trimmed = resp?.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && ulong.TryParse(trimmed, out var val) && val > 0)
                        {
                            code = trimmed;
                            break;
                        }
                    }
                    catch
                    {
                        // 尝试下一个清单授权码源
                    }
                }

                if (string.IsNullOrEmpty(code))
                {
                    LogService.Warn("清单同步", $"未能在各号池接口检索到 Depot {depotId} 的清单授权码");
                    continue;
                }

                progress?.Report($"正在通过官方 CDN 下载清单: {manifestName}...");

                var cdnCandidates = new[]
                {
                    $"https://steampipe.akamaized.net/depot/{depotId}/manifest/{manifestId}/5/{code}",
                    $"https://cdn.steamstatic.com/depot/{depotId}/manifest/{manifestId}/5/{code}",
                    $"https://steamcdn-a.akamaihd.net/depot/{depotId}/manifest/{manifestId}/5/{code}"
                };

                var downloaded = false;
                foreach (var cdnUrl in cdnCandidates)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var dlBytes = await _httpClientProvider.SendWithProxyRetryAsync(
                            "manifest-cdn",
                            TimeSpan.FromSeconds(20),
                            client => client.GetByteArrayAsync(cdnUrl, ct),
                            client =>
                            {
                                client.DefaultRequestHeaders.UserAgent.Clear();
                                client.DefaultRequestHeaders.UserAgent.ParseAdd("Valve/Steam HTTP Client 1.0");
                            });

                        if (dlBytes != null && dlBytes.Length > 0)
                        {
                            // 若为 ZIP 压缩包格式 (PK\x03\x04)，解包提取原始清单流
                            if (dlBytes.Length >= 4 && dlBytes[0] == 0x50 && dlBytes[1] == 0x4B && dlBytes[2] == 0x03 && dlBytes[3] == 0x04)
                            {
                                using var ms = new MemoryStream(dlBytes);
                                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                                var entry = zip.GetEntry("z") ?? zip.Entries.FirstOrDefault();
                                if (entry != null)
                                {
                                    using var es = entry.Open();
                                    using var outMs = new MemoryStream();
                                    await es.CopyToAsync(outMs, ct);
                                    dlBytes = outMs.ToArray();
                                }
                            }

                            // 校验 Steam 清单 Magic 头 (0xD0, 0x17, 0xF6, 0x71)
                            if (dlBytes.Length >= 4 && dlBytes[0] == 0xD0 && dlBytes[1] == 0x17 && dlBytes[2] == 0xF6 && dlBytes[3] == 0x71)
                            {
                                await File.WriteAllBytesAsync(targetPath, dlBytes, ct);
                                await File.WriteAllBytesAsync(targetConfigPath, dlBytes, ct);
                                downloaded = true;
                                downloadedCount++;
                                LogService.Info("清单同步", $"成功下载清单 {manifestName} ({dlBytes.Length} 字节) 并写入两处 depotcache");
                                break;
                            }
                            else
                            {
                                LogService.Warn("清单同步", $"从 {cdnUrl} 下载的内容校验 Magic 失败");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn("清单同步", $"从 {cdnUrl} 下载清单失败: {ex.Message}");
                    }
                }

                if (!downloaded)
                {
                    LogService.Warn("清单同步", $"Depot {depotId} 清单 {manifestName} 下载或校验未成功");
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

    private async Task<Dictionary<int, string>> ResolveDepotsForAppAsync(int appId, string steamPath, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-depot-manifests",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(url, ct),
                ConfigureHeaders);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty(appId.ToString(), out var appObj) &&
                appObj.TryGetProperty("depots", out var depotsObj))
            {
                foreach (var prop in depotsObj.EnumerateObject())
                {
                    if (!int.TryParse(prop.Name, out var depotId)) continue;
                    var dVal = prop.Value;
                    if (dVal.ValueKind != JsonValueKind.Object) continue;
                    if (dVal.TryGetProperty("depotfromapp", out _)) continue;

                    if (dVal.TryGetProperty("config", out var configObj) &&
                        configObj.TryGetProperty("oslist", out var osProp))
                    {
                        var os = osProp.GetString()?.ToLowerInvariant();
                        if (!string.IsNullOrEmpty(os) && !os.Contains("windows"))
                            continue;
                    }

                    if (dVal.TryGetProperty("manifests", out var manifestsObj) &&
                        manifestsObj.TryGetProperty("public", out var pubObj) &&
                        pubObj.TryGetProperty("gid", out var gidProp))
                    {
                        var gid = gidProp.GetString();
                        if (!string.IsNullOrEmpty(gid))
                        {
                            result[depotId] = gid;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("清单同步", $"从 steamcmd 查询 AppID {appId} Depots 失败: {ex.Message}");
        }

        // 尝试从本地 ACF 读取 MountedDepots 补全
        try
        {
            var acfPath = Path.Combine(steamPath, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(acfPath))
            {
                var mounted = ParseMountedDepots(acfPath);
                foreach (var kvp in mounted)
                {
                    if (!result.ContainsKey(kvp.Key))
                        result[kvp.Key] = kvp.Value;
                }
            }
        }
        catch { }

        return result;
    }
}
