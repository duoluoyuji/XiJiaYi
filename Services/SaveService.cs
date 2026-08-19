using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

/// <summary>SteamID 换算辅助。</summary>
public static class SteamIdUtils
{
    public const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>把 userdata 下的账号目录名归一化为 (SteamID3, SteamID64)。</summary>
    public static (string Id3, string Id64)? Normalize(string folder)
    {
        if (!ulong.TryParse(folder, out var value)) return null;
        if (folder.Length == 17 && value >= SteamId64Base)
            return ((value - SteamId64Base).ToString(), value.ToString());
        if (value < SteamId64Base)
            return (value.ToString(), (SteamId64Base + value).ToString());
        return null;
    }
}

public sealed record SaveImportResult(
    int FileCount,
    int ReplacementCount,
    int RenamedCount,
    string BackupPath,
    string Summary);

public interface ISaveService
{
    List<SaveGameEntry> ScanLocalSaves();
    LocalBackupEntry BackupToLocal(SaveGameEntry entry);
    List<LocalBackupEntry> ListLocalBackups(SaveGameEntry entry);
    void RestoreBackup(SaveGameEntry entry, string backupDir);
    Task<string> UploadToCloudAsync(SaveGameEntry entry, IProgress<string>? progress, CancellationToken ct);
    Task<List<CloudBackupEntry>> ListCloudBackupsAsync(SaveGameEntry entry, CancellationToken ct);
    Task<SaveImportResult> DownloadCloudBackupAsync(SaveGameEntry entry, CloudBackupEntry item, IProgress<string>? progress, CancellationToken ct);
    Task<SaveImportResult> ImportPerfectSaveAsync(SaveGameEntry entry, string sourcePath, string? originalSteamId, string customRules, IProgress<string>? progress, CancellationToken ct);
}

public class SaveService : ISaveService
{
    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;
    private readonly string _savesRoot;
    private Dictionary<int, string>? _appInfoNames;

    private string LocalBackupRoot => Path.Combine(_savesRoot, "本地备份");
    private string CloudCacheRoot => Path.Combine(_savesRoot, "云端缓存");
    private string ImportTempRoot => Path.Combine(_savesRoot, "导入临时");

    public SaveService(ISteamPathService steamPathService, ISettingsService settingsService)
    {
        _steamPathService = steamPathService;
        _settingsService = settingsService;
        _savesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Saves");
    }

    // ============ 扫描本地存档 ============

    public List<SaveGameEntry> ScanLocalSaves()
    {
        var result = new List<SaveGameEntry>();
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return result;

        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return result;

        var (accountNames, currentId64) = ReadAccountNames(steamPath);
        var customFolders = _settingsService.Load().CustomSaveFolders ?? new Dictionary<string, List<string>>();

        foreach (var accountDir in Directory.GetDirectories(userdata))
        {
            var folderName = Path.GetFileName(accountDir);
            var ids = SteamIdUtils.Normalize(folderName);
            if (ids == null) continue;
            var (id3, id64) = ids.Value;
            accountNames.TryGetValue(id64, out var accountNameRaw);
            var accountName = accountNameRaw ?? string.Empty;
            var isCurrent = string.Equals(id64, currentId64, StringComparison.Ordinal);

            foreach (var appDir in Directory.GetDirectories(accountDir))
            {
                var remote = Path.Combine(appDir, "remote");
                if (!int.TryParse(Path.GetFileName(appDir), out var appId)) continue;

                // 扫描范围：remote（Steam 云存档）、ugc（创意工坊内容）、local（部分游戏本地云目录）
                var roots = new List<string>();
                foreach (var sub in new[] { "remote", "ugc", "local" })
                {
                    var p = Path.Combine(appDir, sub);
                    if (Directory.Exists(p) && Directory.GetFiles(p, "*", SearchOption.AllDirectories).Length > 0)
                        roots.Add(p);
                }

                // 用户手动添加的自定义存档目录（适用于存档在 Documents/AppData 等的游戏）
                if (customFolders.TryGetValue(appId.ToString(), out var customList))
                {
                    foreach (var custom in customList)
                    {
                        if (Directory.Exists(custom) &&
                            Directory.GetFiles(custom, "*", SearchOption.AllDirectories).Length > 0)
                            roots.Add(custom);
                    }
                }

                if (roots.Count == 0) continue;

                var files = roots.SelectMany(r => Directory.GetFiles(r, "*", SearchOption.AllDirectories)).ToArray();

                long total = 0;
                var last = DateTime.MinValue;
                foreach (var file in files)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        total += fi.Length;
                        if (fi.LastWriteTime > last) last = fi.LastWriteTime;
                    }
                    catch { }
                }

                result.Add(new SaveGameEntry
                {
                    AppId = appId,
                    GameName = ResolveGameName(appId, steamPath),
                    SteamId3 = id3,
                    AccountName = accountName,
                    IsCurrentAccount = isCurrent,
                    SaveRoots = roots,
                    RemotePath = roots.FirstOrDefault(r =>
                        Path.GetFileName(r.TrimEnd('\\', '/')).Equals("remote", StringComparison.OrdinalIgnoreCase))
                        ?? roots[0],
                    FileCount = files.Length,
                    TotalBytes = total,
                    LastWriteTime = last
                });
            }
        }

        return result
            .OrderByDescending(s => s.IsCurrentAccount)
            .ThenBy(s => s.GameName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.AppId)
            .ToList();
    }

    private (Dictionary<string, string> Names, string CurrentId64) ReadAccountNames(string steamPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var currentId64 = string.Empty;
        var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(vdfPath)) return (map, currentId64);
        try
        {
            var content = File.ReadAllText(vdfPath);
            foreach (Match block in Regex.Matches(content, "\"(\\d+)\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = block.Groups["body"].Value;
                var name = GetVdfValue(body, "PersonaName") ?? GetVdfValue(body, "AccountName");
                if (!string.IsNullOrEmpty(name))
                    map[block.Groups[1].Value] = name;
                if (GetVdfValue(body, "MostRecent") == "1")
                    currentId64 = block.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("存档", $"读取 loginusers.vdf 失败: {ex.Message}");
        }
        return (map, currentId64);
    }

    private static string? GetVdfValue(string block, string key)
    {
        var match = Regex.Match(block, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private string ResolveGameName(int appId, string steamPath)
    {
        // 优先 appmanifest_<id>.acf（已安装游戏）
        var acf = _steamPathService.FindAppManifest(appId);
        if (acf != null)
        {
            try
            {
                var content = File.ReadAllText(acf);
                var match = Regex.Match(content, "\"name\"\\s+\"([^\"]+)\"");
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                    return match.Groups[1].Value;
            }
            catch { }
        }

        // 兜底：appinfo.vdf 名称缓存
        try
        {
            _appInfoNames ??= LoadAppInfoNames(steamPath);
            if (_appInfoNames.TryGetValue(appId, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch { }

        return $"游戏 {appId}";
    }

    private static Dictionary<int, string> LoadAppInfoNames(string steamPath)
    {
        var map = new Dictionary<int, string>();
        var appInfo = Path.Combine(steamPath, "appcache", "appinfo.vdf");
        if (!File.Exists(appInfo)) return map;
        foreach (var entry in AppInfoVdf.Parse(appInfo))
        {
            if (!string.IsNullOrEmpty(entry.Name))
                map[(int)entry.AppId] = entry.Name;
        }
        return map;
    }

    // ============ 本地备份 / 恢复 ============

    public LocalBackupEntry BackupToLocal(SaveGameEntry entry)
    {
        var roots = entry.SaveRoots.Count > 0 ? entry.SaveRoots : new List<string> { entry.RemotePath };
        roots = roots.Where(Directory.Exists).ToList();
        if (roots.Count == 0)
            throw new InvalidOperationException("存档目录不存在，无法备份。");

        var stamp = DateTime.Now;
        var dest = Path.Combine(LocalBackupRoot, entry.AppId.ToString(), stamp.ToString("yyyyMMdd_HHmmss"));
        for (var i = 0; i < roots.Count; i++)
        {
            var name = $"{i}_{SanitizeName(Path.GetFileName(roots[i].TrimEnd('\\', '/')))}";
            CopyDirectory(roots[i], Path.Combine(dest, name));
        }

        var files = Directory.GetFiles(dest, "*", SearchOption.AllDirectories);
        var total = files.Sum(f => new FileInfo(f).Length);
        LogService.Info("存档", $"已备份 {entry.GameName}({entry.AppId}) 到本地: {dest}");
        return new LocalBackupEntry
        {
            Path = dest,
            CreatedAt = stamp,
            FileCount = files.Length,
            TotalBytes = total
        };
    }

    public List<LocalBackupEntry> ListLocalBackups(SaveGameEntry entry)
    {
        var root = Path.Combine(LocalBackupRoot, entry.AppId.ToString());
        var result = new List<LocalBackupEntry>();
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                result.Add(new LocalBackupEntry
                {
                    Path = dir,
                    CreatedAt = DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd_HHmmss",
                        null, System.Globalization.DateTimeStyles.None, out var t) ? t : File.GetLastWriteTime(dir),
                    FileCount = files.Length,
                    TotalBytes = files.Sum(f => new FileInfo(f).Length)
                });
            }
            catch { }
        }

        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public void RestoreBackup(SaveGameEntry entry, string backupDir)
    {
        if (!Directory.Exists(backupDir))
            throw new InvalidOperationException("备份目录不存在。");

        BackupToLocal(entry); // 恢复前自动备份当前存档，防止误操作

        var roots = entry.SaveRoots.Count > 0 ? entry.SaveRoots : new List<string> { entry.RemotePath };
        roots = roots.Where(Directory.Exists).ToList();
        var backupSubs = Directory.GetDirectories(backupDir);
        var hasTopLevelFiles = Directory.GetFiles(backupDir, "*", SearchOption.TopDirectoryOnly).Length > 0;

        for (var i = 0; i < roots.Count; i++)
        {
            var name = $"{i}_{SanitizeName(Path.GetFileName(roots[i].TrimEnd('\\', '/')))}";
            var sub = backupSubs.FirstOrDefault(d =>
                Path.GetFileName(d).StartsWith($"{i}_", StringComparison.OrdinalIgnoreCase));
            if (sub != null)
            {
                ClearDirectory(roots[i]);
                CopyDirectory(sub, roots[i]);
            }
            else if (i == 0 && hasTopLevelFiles)
            {
                // 旧版备份（单目录直接存放文件）
                ClearDirectory(roots[i]);
                CopyDirectory(backupDir, roots[i]);
            }
        }
        LogService.Info("存档", $"已从本地备份恢复 {entry.GameName}({entry.AppId}): {backupDir}");
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat((name ?? "save").Select(c => invalid.Contains(c) ? '_' : c));
    }

    // ============ WebDAV 云同步 ============

    private (string Url, string User, string Password) GetWebDavConfig()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.WebDavUrl))
            throw new InvalidOperationException("尚未配置云端地址，请先在「云端备份」区域填写 WebDAV 地址与账号。");
        return (settings.WebDavUrl.Trim().TrimEnd('/') + "/", settings.WebDavUser, settings.WebDavPassword);
    }

    /// <summary>云端目录按账号隔离：喜加一存档/&lt;账号ID&gt;/&lt;AppID&gt;（切换账号后互不干扰）。</summary>
    private string CloudFolderRelative(SaveGameEntry entry) => $"喜加一存档/{entry.SteamId3}/{entry.AppId}";

    /// <summary>旧版云端目录（未按账号隔离），仅用于读取历史备份。</summary>
    private static string LegacyCloudFolderRelative(int appId) => $"喜加一存档/{appId}";

    public async Task<string> UploadToCloudAsync(SaveGameEntry entry, IProgress<string>? progress, CancellationToken ct)
    {
        var (baseUrl, user, password) = GetWebDavConfig();
        var roots = entry.SaveRoots.Count > 0 ? entry.SaveRoots : new List<string> { entry.RemotePath };
        roots = roots.Where(Directory.Exists).ToList();
        if (roots.Count == 0)
            throw new InvalidOperationException("存档目录不存在。");

        Directory.CreateDirectory(CloudCacheRoot);
        var zipPath = Path.Combine(CloudCacheRoot, $"{entry.AppId}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        progress?.Report("正在压缩存档...");
        var stageDir = Path.Combine(CloudCacheRoot, $"stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDir);
        for (var i = 0; i < roots.Count; i++)
        {
            var name = $"{i}_{SanitizeName(Path.GetFileName(roots[i].TrimEnd('\\', '/')))}";
            CopyDirectory(roots[i], Path.Combine(stageDir, name));
        }
        try
        {
            await Task.Run(() => ZipFile.CreateFromDirectory(stageDir, zipPath, CompressionLevel.Optimal, false), ct);
        }
        finally
        {
            try { Directory.Delete(stageDir, true); } catch { }
        }

        try
        {
            using var client = CreateWebDavClient(user, password);
            var baseUri = new Uri(baseUrl);
            var folderRel = CloudFolderRelative(entry);
            var folderUri = new Uri(baseUri, folderRel + "/");
            await EnsureFolderChainAsync(client, baseUri, folderRel, ct);

            progress?.Report($"正在上传 {Path.GetFileName(zipPath)}...");
            var fileUri = new Uri(baseUri, folderRel + "/" + Uri.EscapeDataString(Path.GetFileName(zipPath)));
            using var stream = File.OpenRead(zipPath);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var resp = await client.PutAsync(fileUri, content, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"上传失败（HTTP {(int)resp.StatusCode}），请检查地址、账号密码或网络。");

            LogService.Info("存档", $"已上传云端 {entry.GameName}({entry.AppId}): {fileUri}");
            return fileUri.ToString();
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    public async Task<List<CloudBackupEntry>> ListCloudBackupsAsync(SaveGameEntry entry, CancellationToken ct)
    {
        var result = new List<CloudBackupEntry>();
        var (baseUrl, user, password) = GetWebDavConfig();
        using var client = CreateWebDavClient(user, password);
        var baseUri = new Uri(baseUrl);

        // 优先当前账号目录；若为空再读旧版目录（历史备份仍可下载恢复）
        var currentRel = CloudFolderRelative(entry);
        result = await ListCloudFolderAsync(client, baseUri, currentRel, ct);
        if (result.Count == 0)
            result = await ListCloudFolderAsync(client, baseUri, LegacyCloudFolderRelative(entry.AppId), ct);

        return result.OrderByDescending(b => b.Modified ?? DateTime.MinValue).ToList();
    }

    private static async Task<List<CloudBackupEntry>> ListCloudFolderAsync(
        HttpClient client, Uri baseUri, string folderRel, CancellationToken ct)
    {
        var result = new List<CloudBackupEntry>();
        var folderUri = new Uri(baseUri, folderRel + "/");

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), folderUri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<d:propfind xmlns:d=\"DAV:\">" +
            "<d:prop><d:displayname/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>",
            Encoding.UTF8, "application/xml");

        var resp = await client.SendAsync(request, ct);
        if (!resp.IsSuccessStatusCode) return result; // 文件夹尚不存在

        var xml = await resp.Content.ReadAsStringAsync(ct);
        var ns = XNamespace.Get("DAV:");
        var doc = XDocument.Parse(xml);
        foreach (var response in doc.Descendants(ns + "response"))
        {
            var href = response.Element(ns + "href")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(href)) continue;
            var fileName = Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last());
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            long size = 0;
            DateTime? modified = null;
            if (long.TryParse(response.Descendants(ns + "getcontentlength").FirstOrDefault()?.Value, out var len))
                size = len;
            if (DateTime.TryParse(response.Descendants(ns + "getlastmodified").FirstOrDefault()?.Value, out var mod))
                modified = mod.ToLocalTime();

            result.Add(new CloudBackupEntry
            {
                FileName = fileName,
                FolderPath = folderRel,
                Size = size,
                Modified = modified
            });
        }
        return result;
    }

    public async Task<SaveImportResult> DownloadCloudBackupAsync(SaveGameEntry entry, CloudBackupEntry item, IProgress<string>? progress, CancellationToken ct)
    {
        var (baseUrl, user, password) = GetWebDavConfig();
        var baseUri = new Uri(baseUrl);
        var folderRel = string.IsNullOrEmpty(item.FolderPath)
            ? CloudFolderRelative(entry)
            : item.FolderPath;
        var fileUri = new Uri(baseUri, folderRel + "/" + Uri.EscapeDataString(item.FileName));

        Directory.CreateDirectory(CloudCacheRoot);
        var zipPath = Path.Combine(CloudCacheRoot, item.FileName);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var client = CreateWebDavClient(user, password))
        {
            progress?.Report($"正在下载 {item.FileName}...");
            var resp = await client.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"下载失败（HTTP {(int)resp.StatusCode}）。");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            await src.CopyToAsync(dst, ct);
        }

        try
        {
            return await ApplyZipToRemoteAsync(entry, zipPath, progress, ct);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    private async Task<SaveImportResult> ApplyZipToRemoteAsync(SaveGameEntry entry, string zipPath, IProgress<string>? progress, CancellationToken ct)
    {
        var extractDir = Path.Combine(ImportTempRoot, entry.AppId.ToString(), "cloud");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), ct);

        var backup = BackupToLocal(entry);
        progress?.Report("已自动备份当前存档");

        var roots = entry.SaveRoots.Count > 0 ? entry.SaveRoots : new List<string> { entry.RemotePath };
        roots = roots.Where(Directory.Exists).ToList();
        var extractSubs = Directory.GetDirectories(extractDir);
        var hasTopLevelFiles = Directory.GetFiles(extractDir, "*", SearchOption.TopDirectoryOnly).Length > 0;
        var restoreRoot = roots[0];

        for (var i = 0; i < roots.Count; i++)
        {
            var name = $"{i}_{SanitizeName(Path.GetFileName(roots[i].TrimEnd('\\', '/')))}";
            var sub = extractSubs.FirstOrDefault(d =>
                Path.GetFileName(d).StartsWith($"{i}_", StringComparison.OrdinalIgnoreCase));
            if (sub != null)
            {
                ClearDirectory(roots[i]);
                CopyDirectory(sub, roots[i]);
            }
            else if (i == 0 && hasTopLevelFiles)
            {
                ClearDirectory(roots[i]);
                CopyDirectory(extractDir, roots[i]);
            }
            restoreRoot = roots[i];
        }

        var count = CountFiles(restoreRoot);
        LogService.Info("存档", $"已从云端恢复 {entry.GameName}({entry.AppId}): {Path.GetFileName(zipPath)}");
        return new SaveImportResult(count, 0, 0, backup.Path,
            $"已恢复云端备份「{Path.GetFileName(zipPath)}」\n共 {count} 个文件\n恢复前已自动备份当前存档。");
    }

    // ============ 完美存档导入 + ID 替换 ============

    public async Task<SaveImportResult> ImportPerfectSaveAsync(SaveGameEntry entry, string sourcePath, string? originalSteamId, string customRules, IProgress<string>? progress, CancellationToken ct)
    {
        var remote = ValidateRemotePath(entry);
        var tempRoot = Path.Combine(ImportTempRoot, entry.AppId.ToString(), "import");
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        Directory.CreateDirectory(tempRoot);

        string contentRoot;
        if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(tempRoot, "zip");
            progress?.Report("正在解压完美存档...");
            await Task.Run(() => ZipFile.ExtractToDirectory(sourcePath, extractDir), ct);
            contentRoot = FindContentRoot(extractDir);
        }
        else if (Directory.Exists(sourcePath))
        {
            contentRoot = sourcePath;
        }
        else
        {
            throw new InvalidOperationException("所选文件不是有效的 ZIP 或文件夹。");
        }

        // 先自动备份当前存档
        var backup = BackupToLocal(entry);
        progress?.Report("已自动备份当前存档");

        // 复制到暂存区再替换，避免污染来源文件夹
        var stage = Path.Combine(tempRoot, "stage");
        CopyDirectory(contentRoot, stage);

        var replacements = 0;
        var renamed = 0;
        var idPairs = ResolveIdPairs(entry, originalSteamId);
        if (idPairs.Count > 0 || !string.IsNullOrWhiteSpace(customRules))
        {
            progress?.Report("正在替换存档内账号 ID...");
            (replacements, renamed) = await Task.Run(() => ReplaceIdsInDirectory(stage, idPairs, customRules), ct);
        }

        ClearDirectory(remote);
        CopyDirectory(stage, remote);
        var count = CountFiles(remote);

        var summary = new StringBuilder();
        summary.AppendLine($"导入完成：共 {count} 个文件已替换到游戏存档目录。");
        if (replacements > 0 || renamed > 0)
            summary.AppendLine($"账号 ID 修正：文件内容替换 {replacements} 处，文件名修正 {renamed} 个。");
        summary.AppendLine($"原存档已自动备份到：{backup.Path}");
        LogService.Info("存档", $"完美存档导入 {entry.GameName}({entry.AppId}): {count} 文件, {replacements} 处替换");
        return new SaveImportResult(count, replacements, renamed, backup.Path, summary.ToString());
    }

    /// <summary>由「存档原账号 ID」与当前账号计算出要替换的 ID 对。</summary>
    private static List<(string Old, string New)> ResolveIdPairs(SaveGameEntry entry, string? originalSteamId)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(originalSteamId)) return pairs;

        var input = originalSteamId.Trim();
        var current3 = entry.SteamId3;
        var current64 = entry.SteamId64;
        if (string.IsNullOrEmpty(current3) || string.IsNullOrEmpty(current64)) return pairs;

        var old3 = string.Empty;
        var old64 = string.Empty;
        if (ulong.TryParse(input, out var v))
        {
            if (input.Length == 17 && v >= SteamIdUtils.SteamId64Base)
            {
                old64 = input;
                old3 = (v - SteamIdUtils.SteamId64Base).ToString();
            }
            else if (v < SteamIdUtils.SteamId64Base)
            {
                old3 = input;
                old64 = (SteamIdUtils.SteamId64Base + v).ToString();
            }
        }

        if (!string.IsNullOrEmpty(old64)) pairs.Add((old64, current64));
        if (!string.IsNullOrEmpty(old3)) pairs.Add((old3, current3));
        return pairs;
    }

    private static (int Replacements, int Renamed) ReplaceIdsInDirectory(
        string dir, List<(string Old, string New)> idPairs, string customRules)
    {
        var rules = new List<(string Old, string New)>(idPairs);
        foreach (var line in (customRules ?? string.Empty).Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            var parts = trimmed.Split(new[] { '=', '→', '>', ':' }, 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                rules.Add((parts[0].Trim(), parts[1].Trim()));
        }

        var replacements = 0;
        var renamed = 0;
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var data = File.ReadAllBytes(file);
                var before = replacements;

                // 二进制安全：等长字节替换（UTF8 与 UTF16 两种编码形式）
                foreach (var (old, @new) in rules)
                {
                    if (old.Length != @new.Length) continue;
                    foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
                    {
                        var oldBytes = encoding.GetBytes(old);
                        var newBytes = encoding.GetBytes(@new);
                        if (oldBytes.Length != newBytes.Length) continue;
                        replacements += ReplaceBytes(data, oldBytes, newBytes);
                    }
                }

                // 文本文件：完整字符串替换（支持不等长，如不同位数的 SteamID3）
                var text = TryDecodeText(data);
                if (text != null)
                {
                    var content = text.Value.Content;
                    foreach (var (old, @new) in rules)
                    {
                        if (string.IsNullOrEmpty(old) || old == @new) continue;
                        if (!content.Contains(old, StringComparison.Ordinal)) continue;
                        var count = CountOccurrences(content, old);
                        content = content.Replace(old, @new);
                        replacements += count;
                    }
                    if (content != text.Value.Content)
                    {
                        var enc = text.Value.Encoding;
                        if (text.Value.KeepBom && enc is UTF8Encoding)
                            enc = new UTF8Encoding(true);
                        var body = enc.GetBytes(content);
                        data = text.Value.KeepBom
                            ? enc.GetPreamble().Concat(body).ToArray()
                            : body;
                    }
                }

                if (replacements != before)
                    File.WriteAllBytes(file, data);

                // 文件名中的 ID 修正
                var name = Path.GetFileName(file);
                var newName = name;
                foreach (var (old, @new) in idPairs)
                {
                    if (!string.IsNullOrEmpty(old) && newName.Contains(old, StringComparison.Ordinal))
                        newName = newName.Replace(old, @new);
                }
                if (newName != name)
                {
                    var dest = Path.Combine(Path.GetDirectoryName(file)!, newName);
                    if (!File.Exists(dest))
                    {
                        File.Move(file, dest);
                        renamed++;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn("存档", $"替换 {file} 失败: {ex.Message}");
            }
        }

        return (replacements, renamed);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static int ReplaceBytes(byte[] data, byte[] oldSeq, byte[] newSeq)
    {
        if (oldSeq.Length == 0 || oldSeq.Length != newSeq.Length || data.Length < oldSeq.Length) return 0;
        var count = 0;
        for (var i = 0; i <= data.Length - oldSeq.Length; i++)
        {
            var match = true;
            for (var j = 0; j < oldSeq.Length; j++)
            {
                if (data[i + j] != oldSeq[j]) { match = false; break; }
            }
            if (!match) continue;
            Array.Copy(newSeq, 0, data, i, newSeq.Length);
            count++;
            i += newSeq.Length - 1;
        }
        return count;
    }

    private static (string Content, Encoding Encoding, bool KeepBom)? TryDecodeText(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return (Encoding.Unicode.GetString(data, 2, data.Length - 2), Encoding.Unicode, true);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2), Encoding.BigEndianUnicode, true);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return (Encoding.UTF8.GetString(data, 3, data.Length - 3), new UTF8Encoding(false), true);
        if (data.All(b => b < 128))
            return (Encoding.ASCII.GetString(data), new UTF8Encoding(false), false);
        try
        {
            return (new UTF8Encoding(false, true).GetString(data), new UTF8Encoding(false), false);
        }
        catch
        {
            return null; // 二进制文件，仅做等长字节替换
        }
    }

    // ============ WebDAV 基础 ============

    private static HttpClient CreateWebDavClient(string user, string password)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        if (!string.IsNullOrEmpty(user))
        {
            var bytes = Encoding.ASCII.GetBytes($"{user}:{password ?? string.Empty}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XiJiaYi/1.4");
        return client;
    }

    private static async Task EnsureFolderChainAsync(HttpClient client, Uri baseUri, string relativePath, CancellationToken ct)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = baseUri;
        foreach (var part in parts)
        {
            current = new Uri(current, Uri.EscapeDataString(part) + "/");
            var request = new HttpRequestMessage(new HttpMethod("MKCOL"), current);
            var resp = await client.SendAsync(request, ct);
            // 405 = 已存在；其他 2xx 也视为成功
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 405 && (int)resp.StatusCode != 301)
                throw new InvalidOperationException($"无法创建云端目录（HTTP {(int)resp.StatusCode}）。");
        }
    }

    // ============ 文件工具 ============

    private static string ValidateRemotePath(SaveGameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RemotePath))
            throw new InvalidOperationException("存档路径为空。");
        var full = Path.GetFullPath(entry.RemotePath);
        if (!full.EndsWith("\\remote", StringComparison.OrdinalIgnoreCase) &&
            !full.EndsWith("/remote", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("存档路径无效，必须指向 userdata 下的 remote 目录。");
        return full;
    }

    private static string FindContentRoot(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return entries[0];
        return dir;
    }

    private static int CountFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir))
            Directory.Delete(sub, true);
        foreach (var file in Directory.GetFiles(dir))
            File.Delete(file);
    }
}
