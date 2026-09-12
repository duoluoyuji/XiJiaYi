using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class SteamDepotService : ISteamDepotService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly string _cacheFolder;
    private string _currentSource = "DepotKey";
    private readonly Dictionary<string, (string DepotKeysUrl, string TokenKeysUrl)> _resolvedUrls = new();

    private const string KeyIndexUrl = "https://pan.qzyun.net/f/d/MlArs0/key.txt";
    private const string Source2DepotKeysUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string Source2TokenKeysUrl = "https://api.993499094.xyz/appaccesstokens.json";
    /// <summary>ShikiLua（KeySteam 内置库）在线密钥源；未配置托管时由内置离线数据兜底。</summary>
    private const string ShikiLuaDepotKeysUrl = "https://raw.githubusercontent.com/duoluoyuji/ShikiLuaDepot/main/depotkeys.json";
    private const string ShikiLuaTokenKeysUrl = "https://raw.githubusercontent.com/duoluoyuji/ShikiLuaDepot/main/appaccesstokens.json";

    /// <summary>备用密钥数据源（ManifestHub 仓库及其镜像），按顺序逐个尝试。</summary>
    private static readonly (string DepotKeysUrl, string TokenKeysUrl)[] DefaultKeySourceCandidates =
    [
        ("https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/depotkeys.json",
         "https://raw.githubusercontent.com/SteamAutoCracks/ManifestHub/main/appaccesstokens.json"),
        ("https://api.993499094.xyz/depotkeys.json",
         "https://api.993499094.xyz/appaccesstokens.json"),
        ("https://cdn.jsdmirror.com/gh/SteamAutoCracks/ManifestHub@main/depotkeys.json",
         "https://cdn.jsdmirror.com/gh/SteamAutoCracks/ManifestHub@main/appaccesstokens.json"),
        ("https://raw.gitmirror.com/SteamAutoCracks/ManifestHub/main/depotkeys.json",
         "https://raw.gitmirror.com/SteamAutoCracks/ManifestHub/main/appaccesstokens.json"),
        ("https://raw.dgithub.xyz/SteamAutoCracks/ManifestHub/main/depotkeys.json",
         "https://raw.dgithub.xyz/SteamAutoCracks/ManifestHub/main/appaccesstokens.json"),
        ("https://gh.akass.cn/SteamAutoCracks/ManifestHub/main/depotkeys.json",
         "https://gh.akass.cn/SteamAutoCracks/ManifestHub/main/appaccesstokens.json"),
    ];

    private static readonly string[] SourceNames = ["DepotKey", "DepotKey2", "ShikiLua"];

    public SteamDepotService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;

        _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        if (!Directory.Exists(_cacheFolder))
            Directory.CreateDirectory(_cacheFolder);
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public void UseDataSource(string source)
    {
        _currentSource = source;
    }

    /// <summary>随程序分发的内置 ShikiLua 密钥库目录（Data/ShikiLua），离线可用。</summary>
    private static string GetBundledDataDir() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "ShikiLua");

    private bool TryCopyBundledShikiLua(string depotPath, string tokenPath)
    {
        try
        {
            var bundledDir = GetBundledDataDir();
            var srcDepot = Path.Combine(bundledDir, "depotkeys.json");
            var srcToken = Path.Combine(bundledDir, "appaccesstokens.json");
            if (!File.Exists(srcDepot) || !File.Exists(srcToken))
                return false;

            var cacheDir = Path.GetDirectoryName(depotPath);
            if (!string.IsNullOrEmpty(cacheDir))
                Directory.CreateDirectory(cacheDir);

            File.Copy(srcDepot, depotPath, true);
            File.Copy(srcToken, tokenPath, true);
            LogService.Info("入库", "ShikiLua 内置密钥库已就绪（离线数据）");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("入库", $"复制内置 ShikiLua 密钥库失败: {ex.Message}");
            return false;
        }
    }

    private string GetSourceCacheDirFor(string source) =>
        source switch
        {
            "DepotKey2" => Path.Combine(_cacheFolder, "v2"),
            "ShikiLua" => Path.Combine(_cacheFolder, "v3"),
            _ => Path.Combine(_cacheFolder, "v1"),
        };

    private string GetSourceCacheDir() => GetSourceCacheDirFor(_currentSource);

    private string GetDepotKeysPath() =>
        Path.Combine(GetSourceCacheDir(), "depotkeys.json");

    private string GetTokenKeysPath() =>
        Path.Combine(GetSourceCacheDir(), "appaccesstokens.json");

    private Dictionary<string, string> LoadCombinedDepotKeys()
    {
        var combined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. 内置离线数据兜底
        var bundledPath = Path.Combine(GetBundledDataDir(), "depotkeys.json");
        LoadKeysFromFile(bundledPath, combined);

        // 2. 其它非当前选中的本地缓存源作为备用补充
        foreach (var source in SourceNames)
        {
            if (!string.Equals(source, _currentSource, StringComparison.OrdinalIgnoreCase))
            {
                var otherPath = Path.Combine(GetSourceCacheDirFor(source), "depotkeys.json");
                LoadKeysFromFile(otherPath, combined);
            }
        }

        // 3. 当前选中的数据源作为最高优先级覆盖
        var currentPath = GetDepotKeysPath();
        LoadKeysFromFile(currentPath, combined);

        return combined;
    }

    private Dictionary<string, string> LoadCombinedTokenKeys()
    {
        var combined = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var bundledPath = Path.Combine(GetBundledDataDir(), "appaccesstokens.json");
        LoadKeysFromFile(bundledPath, combined);

        foreach (var source in SourceNames)
        {
            if (!string.Equals(source, _currentSource, StringComparison.OrdinalIgnoreCase))
            {
                var otherPath = Path.Combine(GetSourceCacheDirFor(source), "appaccesstokens.json");
                LoadKeysFromFile(otherPath, combined);
            }
        }

        var currentPath = GetTokenKeysPath();
        LoadKeysFromFile(currentPath, combined);

        return combined;
    }

    private static void LoadKeysFromFile(string filePath, Dictionary<string, string> targetDict)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict != null)
            {
                foreach (var (k, v) in dict)
                {
                    if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                        targetDict[k] = v;
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("入库", $"读取密钥文件失败 ({filePath}): {ex.Message}");
        }
    }

    private async Task<bool> ResolveKeyUrlsAsync(CancellationToken ct = default)
    {
        if (_resolvedUrls.TryGetValue(_currentSource, out var cached) &&
            !string.IsNullOrEmpty(cached.DepotKeysUrl) &&
            !string.IsNullOrEmpty(cached.TokenKeysUrl))
            return true;

        if (_currentSource == "DepotKey")
        {
            _resolvedUrls[_currentSource] = (DefaultKeySourceCandidates[0].DepotKeysUrl, DefaultKeySourceCandidates[0].TokenKeysUrl);
            return true;
        }

        if (_currentSource == "DepotKey2")
        {
            _resolvedUrls[_currentSource] = (Source2DepotKeysUrl, Source2TokenKeysUrl);
            return true;
        }

        if (_currentSource == "ShikiLua")
        {
            _resolvedUrls[_currentSource] = (ShikiLuaDepotKeysUrl, ShikiLuaTokenKeysUrl);
            return true;
        }

        try
        {
            var depotKeysUrl = string.Empty;
            var tokenKeysUrl = string.Empty;
            var content = await _httpClientProvider.SendWithProxyRetryAsync(
                $"steam-depot-{_currentSource}",
                TimeSpan.FromSeconds(30),
                client => client.GetStringAsync(KeyIndexUrl, ct),
                ConfigureHeaders);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var url = line.Trim();
                if (url.EndsWith("depotkeys.json", StringComparison.OrdinalIgnoreCase))
                    depotKeysUrl = url;
                else if (url.EndsWith("appaccesstokens.json", StringComparison.OrdinalIgnoreCase))
                    tokenKeysUrl = url;
            }

            if (!string.IsNullOrEmpty(depotKeysUrl) && !string.IsNullOrEmpty(tokenKeysUrl))
            {
                _resolvedUrls[_currentSource] = (depotKeysUrl, tokenKeysUrl);
                return true;
            }
        }
        catch (Exception ex) { LogService.Warn("入库", $"解析密钥仓库地址失败 ({_currentSource}): {ex.Message}"); }

        return false;
    }

    public async Task<bool> EnsureKeyFilesAsync(CancellationToken ct = default)
    {
        var cacheDir = GetSourceCacheDir();
        var depotPath = GetDepotKeysPath();
        var tokenPath = GetTokenKeysPath();

        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);

        if (File.Exists(depotPath) && File.Exists(tokenPath))
            return true;

        // ShikiLua：内置离线数据兜底，无需联网
        if (_currentSource == "ShikiLua" && TryCopyBundledShikiLua(depotPath, tokenPath))
            return true;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var result = await UpdateKeyFilesAsync(ct);
                if (result.Success) return true;
            }
            catch (Exception ex) { LogService.Warn("入库", $"更新密钥文件失败 (第{attempt}次): {ex.Message}"); }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
        return false;
    }

    public async Task<KeyFileUpdateResult> UpdateKeyFilesAsync(CancellationToken ct = default)
    {
        var result = new KeyFileUpdateResult();

        try
        {
            var cacheDir = GetSourceCacheDir();
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            var depotPath = GetDepotKeysPath();
            var tokenPath = GetTokenKeysPath();

            // 读取旧文件条目数
            if (File.Exists(depotPath))
            {
                try
                {
                    var oldContent = await File.ReadAllTextAsync(depotPath, ct);
                    var oldDict = JsonSerializer.Deserialize<Dictionary<string, string>>(oldContent);
                    result.DepotKeysOldCount = oldDict?.Count ?? 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析旧 depot 密钥文件失败: {ex.Message}"); result.DepotKeysOldCount = 0; }
            }
            if (File.Exists(tokenPath))
            {
                try
                {
                    var oldContent = await File.ReadAllTextAsync(tokenPath, ct);
                    var oldDict = JsonSerializer.Deserialize<Dictionary<string, string>>(oldContent);
                    result.TokenKeysOldCount = oldDict?.Count ?? 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析旧 token 密钥文件失败: {ex.Message}"); result.TokenKeysOldCount = 0; }
            }

            // ShikiLua：内置数据优先（离线可用），直接落地到缓存
            if (_currentSource == "ShikiLua" && TryCopyBundledShikiLua(depotPath, tokenPath))
            {
                try
                {
                    var newDepot = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        await File.ReadAllTextAsync(depotPath, ct));
                    result.DepotKeysNewCount = newDepot?.Count ?? 0;
                    var newToken = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        await File.ReadAllTextAsync(tokenPath, ct));
                    result.TokenKeysNewCount = newToken?.Count ?? 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析内置 ShikiLua 密钥库失败: {ex.Message}"); }

                result.Success = true;
                return result;
            }

            // 组装候选源：当前配置源优先，其次为备用镜像源（自动去重）
            var candidates = new List<(string DepotKeysUrl, string TokenKeysUrl)>();
            if (await ResolveKeyUrlsAsync(ct) && _resolvedUrls.TryGetValue(_currentSource, out var resolved))
                candidates.Add(resolved);
            candidates.AddRange(DefaultKeySourceCandidates);
            candidates = candidates.Distinct().ToList();

            var clientName = $"steam-depot-{_currentSource}";
            var lastError = string.Empty;
            foreach (var urls in candidates)
            {
                try
                {
                    // 下载新文件
                    var depotTask = _httpClientProvider.SendWithProxyRetryAsync(
                        clientName,
                        TimeSpan.FromSeconds(30),
                        client => client.GetByteArrayAsync(urls.DepotKeysUrl, ct),
                        ConfigureHeaders);
                    var tokenTask = _httpClientProvider.SendWithProxyRetryAsync(
                        clientName,
                        TimeSpan.FromSeconds(30),
                        client => client.GetByteArrayAsync(urls.TokenKeysUrl, ct),
                        ConfigureHeaders);

                    await Task.WhenAll(depotTask, tokenTask);

                    var depotData = await depotTask;
                    var tokenData = await tokenTask;
                    if (depotData.Length == 0)
                        throw new InvalidOperationException("下载的 depotkeys.json 为空");

                    await File.WriteAllBytesAsync(depotPath, depotData, ct);
                    result.DepotKeysSizeBytes = depotData.Length;
                    await File.WriteAllBytesAsync(tokenPath, tokenData, ct);
                    result.TokenKeysSizeBytes = tokenData.Length;

                    // 解析新文件条目数
                    try
                    {
                        var newDict = JsonSerializer.Deserialize<Dictionary<string, string>>(depotData);
                        result.DepotKeysNewCount = newDict?.Count ?? 0;
                    }
                    catch (Exception ex) { LogService.Warn("入库", $"解析新 depot 密钥文件失败: {ex.Message}"); result.DepotKeysNewCount = 0; }
                    try
                    {
                        var newDict = JsonSerializer.Deserialize<Dictionary<string, string>>(tokenData);
                        result.TokenKeysNewCount = newDict?.Count ?? 0;
                    }
                    catch (Exception ex) { LogService.Warn("入库", $"解析新 token 密钥文件失败: {ex.Message}"); result.TokenKeysNewCount = 0; }

                    LogService.Info("入库", $"密钥数据源更新成功: {urls.DepotKeysUrl}");
                    result.Success = true;
                    return result;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    LogService.Warn("入库", $"密钥数据源不可用 ({urls.DepotKeysUrl}): {ex.Message}");
                }
            }

            LogService.Error("入库", $"所有密钥数据源均失败: {lastError}");
            result.Success = false;
            return result;
        }
        catch (Exception ex)
        {
            LogService.Error("入库", $"更新密钥文件失败: {ex.Message}");
            result.Success = false;
            return result;
        }
    }

    public async Task EnsureAllSourcesAsync(CancellationToken ct = default)
    {
        foreach (var source in SourceNames)
        {
            UseDataSource(source);
            try { await EnsureKeyFilesAsync(ct); }
            catch (Exception ex) { LogService.Warn("入库", $"后台更新密钥文件失败 ({source}): {ex.Message}"); }
        }
    }

    public async Task<DepotQueryResult?> QueryAppAsync(int appId, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await _httpClientProvider.SendWithProxyRetryAsync(
                $"steam-depot-{_currentSource}",
                TimeSpan.FromSeconds(30),
                client => client.GetStringAsync(url, ct),
                ConfigureHeaders);
            using var doc = JsonDocument.Parse(response);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty(appId.ToString(), out var app)) return null;

            var result = new DepotQueryResult { AppId = appId };

            if (app.TryGetProperty("common", out var common) && common.TryGetProperty("name", out var name))
                result.AppName = name.GetString() ?? $"App {appId}";

            if (app.TryGetProperty("depots", out var depots))
            {
                foreach (var depotProp in depots.EnumerateObject())
                {
                    var depotIdStr = depotProp.Name;
                    if (!int.TryParse(depotIdStr, out var depotId)) continue;

                    var depot = depotProp.Value;
                    if (depot.TryGetProperty("depotfromapp", out _)) continue;

                    var dki = new DepotKeyInfo { DepotId = depotId };

                    if (depot.TryGetProperty("manifests", out var manifests) &&
                        manifests.TryGetProperty("public", out var pub) &&
                        pub.TryGetProperty("gid", out var gid))
                    {
                        dki.ManifestId = gid.GetString() ?? "";
                    }

                    result.GameDepots.Add(dki);
                }
            }

            if (app.TryGetProperty("extended", out var ext) && ext.TryGetProperty("listofdlc", out var dlcList))
            {
                if (dlcList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dlcIdStr in dlcList.EnumerateArray())
                    {
                        if (int.TryParse(dlcIdStr.GetString(), out var dlcId))
                            result.DlcAppIds.Add(dlcId);
                    }
                }
                else if (dlcList.ValueKind == JsonValueKind.String)
                {
                    foreach (var idStr in dlcList.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(idStr.Trim(), out var dlcId))
                            result.DlcAppIds.Add(dlcId);
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"查询 AppID {appId} 失败：{ex.Message}", ex);
        }
    }

    public async Task<string?> GenerateLuaAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default)
    {
        try
        {
            await EnsureKeyFilesAsync(ct);

            var depotKeys = LoadCombinedDepotKeys();
            var appTokens = LoadCombinedTokenKeys();

            var queryResult = await QueryAppAsync(appId, ct);
            if (queryResult == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("-- generated by XiJiaYi");
            sb.AppendLine();
            var matchedItems = 0;

            if (depotKeys.TryGetValue(appId.ToString(), out var mainKey))
            {
                sb.AppendLine($"addappid({appId}, 1, \"{mainKey}\")");
                matchedItems++;
            }
            else
            {
                // 主 AppID 没有 key 时，仍允许依赖子 depot key 继续生成
                sb.AppendLine($"addappid({appId})");
            }

            foreach (var depot in queryResult.GameDepots)
            {
                if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var key))
                {
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                    depot.Key = key;
                    depot.IsMatched = true;
                    matchedItems++;
                }
                else
                {
                    sb.AppendLine($"addappid({depot.DepotId})");
                }
            }

            if (appTokens.TryGetValue(appId.ToString(), out var token))
            {
                sb.AppendLine($"addtoken({appId}, \"{token}\")");
                queryResult.AppToken = token;
                matchedItems++;
            }

            if (matchedItems == 0)
            {
                LogService.Warn("入库", $"本地各仓库均未匹配到 AppID {appId} 的加密密钥，已生成基础入库配置（可配合分流清单或免费/公开库下载）");
            }

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            if (!Directory.Exists(luaFolder))
                Directory.CreateDirectory(luaFolder);

            var luaPath = Path.Combine(luaFolder, $"{appId}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            return luaPath;
        }
        catch (Exception ex) { LogService.Error("入库", $"生成入库文件失败 (AppID {appId}): {ex.Message}"); return null; }
    }

    public async Task<string?> GenerateLuaWithDlcAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default)
    {
        try
        {
            await EnsureKeyFilesAsync(ct);

            var depotKeys = LoadCombinedDepotKeys();
            var appTokens = LoadCombinedTokenKeys();

            var queryResult = await QueryAppAsync(appId, ct);
            if (queryResult == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("-- generated by XiJiaYi");
            sb.AppendLine();
            var matchedItems = 0;

            if (depotKeys.TryGetValue(appId.ToString(), out var mainKeyDlc))
            {
                sb.AppendLine($"addappid({appId}, 1, \"{mainKeyDlc}\")");
                matchedItems++;
            }
            else
            {
                // 主 AppID 没有 key 时，DLC 模式也继续向下生成
                sb.AppendLine($"addappid({appId})");
            }

            foreach (var depot in queryResult.GameDepots)
            {
                if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var key))
                {
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                    matchedItems++;
                }
                else
                {
                    sb.AppendLine($"addappid({depot.DepotId})");
                }
            }

            if (appTokens.TryGetValue(appId.ToString(), out var token))
            {
                sb.AppendLine($"addtoken({appId}, \"{token}\")");
                queryResult.AppToken = token;
                matchedItems++;
            }

            var mainDepotIds = new HashSet<int>(queryResult.GameDepots.Select(d => d.DepotId));
            var matchedDlc = 0;
            foreach (var dlcAppId in queryResult.DlcAppIds)
            {
                // Skip DLC that is already a main game depot (already handled above)
                if (!mainDepotIds.Contains(dlcAppId))
                {
                    if (depotKeys.TryGetValue(dlcAppId.ToString(), out var dlcMainKey))
                    {
                        sb.AppendLine($"addappid({dlcAppId}, 1, \"{dlcMainKey}\")");
                        matchedItems++;
                    }
                    else
                    {
                        sb.AppendLine($"addappid({dlcAppId})");
                    }

                    if (appTokens.TryGetValue(dlcAppId.ToString(), out var dlcToken))
                    {
                        sb.AppendLine($"addtoken({dlcAppId}, \"{dlcToken}\")");
                        matchedItems++;
                    }
                }

                // Query DLC's own sub-depots for additional keys
                try
                {
                    var dlcResult = await QueryAppAsync(dlcAppId, ct);
                    if (dlcResult != null)
                    {
                        foreach (var depot in dlcResult.GameDepots)
                        {
                            if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var key))
                            {
                                sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                                matchedItems++;
                            }
                            else
                            {
                                sb.AppendLine($"addappid({depot.DepotId})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn("入库", $"查询 DLC {dlcAppId} 仓库信息跳过: {ex.Message}");
                }
                matchedDlc++;
            }

            if (matchedItems == 0)
            {
                LogService.Warn("入库", $"本地各仓库均未匹配到 AppID {appId}、其 depots 或 DLC 的加密密钥，已生成基础入库配置（可配合分流清单或免费/公开库下载）");
            }

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            if (!Directory.Exists(luaFolder))
                Directory.CreateDirectory(luaFolder);

            var luaPath = Path.Combine(luaFolder, $"{appId}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            return luaPath;
        }
        catch (Exception ex) { LogService.Error("入库", $"生成入库文件失败 (AppID {appId}, DLC): {ex.Message}"); return null; }
    }

    public async Task<DlcFetchResult> FetchDlcAsync(string luaPath, int dlcAppId, bool hasOwnDepot, CancellationToken ct = default)
    {
        var result = new DlcFetchResult();
        try
        {
            // 1. 无独立 depot → 无需密钥，直接写入
            if (!hasOwnDepot)
            {
                result.NeedKey = false;
                await AppendLinesToLuaAsync(luaPath, new List<string> { $"addappid({dlcAppId})" }, ct);
                result.Success = true;
                result.Message = $"DLC {dlcAppId} 无独立 depot，无需密钥，已写入";
                return result;
            }

            // 2. 有独立 depot → 需要密钥，从本地各密钥仓库联合搜索
            result.NeedKey = true;
            await EnsureKeyFilesAsync(ct);

            var depotKeys = LoadCombinedDepotKeys();
            if (depotKeys.TryGetValue(dlcAppId.ToString(), out var dlcMainKey))
            {
                var lines = new List<string> { $"addappid({dlcAppId}, 1, \"{dlcMainKey}\")" };
                await AppendLinesToLuaAsync(luaPath, lines, ct);
                result.Success = true;
                result.Message = $"DLC {dlcAppId} 密钥获取成功，已写入";
            }
            else
            {
                result.Message = $"无法获取 DLC {dlcAppId} 的密钥信息（本地各密钥仓库中均未找到），获取失败";
            }

            return result;
        }
        catch (Exception ex)
        {
            LogService.Error("获取DLC", $"获取 DLC {dlcAppId} 失败: {ex.Message}");
            result.Message = $"获取失败：{ex.Message}";
            return result;
        }
    }

    private static async Task AppendLinesToLuaAsync(string luaPath, List<string> lines, CancellationToken ct = default)
    {
        if (lines == null || lines.Count == 0) return;

        var dir = Path.GetDirectoryName(luaPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var existing = File.Exists(luaPath) ? await File.ReadAllTextAsync(luaPath, ct) : string.Empty;
        var newLines = new List<string>();

        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"addappid\((\d+)");
            if (match.Success && Regex.IsMatch(existing, $@"\baddappid\(\s*{match.Groups[1].Value}\s*[,\)]"))
                continue; // 已存在则跳过
            newLines.Add(line);
        }

        if (newLines.Count == 0)
        {
            LogService.Info("获取DLC", "所有行已存在于 Lua 文件中，跳过写入");
            return;
        }

        var sb = new StringBuilder(existing.TrimEnd());
        if (sb.Length > 0)
            sb.AppendLine();
        sb.AppendLine(string.Join(Environment.NewLine, newLines));

        await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);
        LogService.Info("获取DLC", $"已写入 {newLines.Count} 行到 {luaPath}");
    }
}
