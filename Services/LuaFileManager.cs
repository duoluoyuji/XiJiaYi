using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class LuaFileManager : ILuaFileManager, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private FileSystemWatcher? _watcher;
    private bool _isWatching;
    private CancellationTokenSource? _debounceCts;

    private static readonly Regex AddAppIdRegex = new(@"addappid\((\d+)\)", RegexOptions.IgnoreCase);
    private static readonly Regex AddDepotRegex = new(@"addappid\((\d+),\s*(\d+),\s*""([^""]+)""\)", RegexOptions.IgnoreCase);
    private static readonly Regex AddTokenRegex = new(@"addtoken\((\d+),\s*""([^""]+)""\)", RegexOptions.IgnoreCase);
    private static readonly Regex ManifestPinRegex = new(@"^\s*setManifestid\((\d+),\s*""(\d+)""(?:\s*,\s*(\d+))?\)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ManifestPinCommentedRegex = new(@"^\s*--\s*setManifestid\((\d+),\s*""(\d+)""(?:\s*,\s*(\d+))?\)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex AppNameRegex = new(@"--\s*app_name:\s*(.+)", RegexOptions.IgnoreCase);

    private static Dictionary<string, string>? _cachedNames;
    private static readonly object _namesLock = new();

    public event EventHandler? FilesChanged;

    public LuaFileManager(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
    }

    public static string? TryGetLocalGameName(int appId)
    {
        if (_cachedNames == null)
        {
            lock (_namesLock)
            {
                if (_cachedNames == null)
                {
                    try
                    {
                        var namesFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "ShikiLua", "names.json");
                        if (File.Exists(namesFile))
                        {
                            var json = File.ReadAllText(namesFile);
                            _cachedNames = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                        }
                        else
                        {
                            _cachedNames = new();
                        }
                    }
                    catch
                    {
                        _cachedNames = new();
                    }
                }
            }
        }
        return _cachedNames.TryGetValue(appId.ToString(), out var name) ? name : null;
    }

    private static List<string> GetScriptFiles(string folder)
    {
        if (!Directory.Exists(folder)) return new List<string>();
        var files = Directory.GetFiles(folder, "*.ks")
            .Concat(Directory.GetFiles(folder, "*.lua"))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.EndsWith(".ks", StringComparison.OrdinalIgnoreCase)).First())
            .ToList();
        return files;
    }

    public async Task<List<GameInfo>> ScanLuaFilesAsync()
    {
        return await Task.Run(() =>
        {
            var result = new List<GameInfo>();
            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
                return result;

            var scriptFiles = GetScriptFiles(luaFolder);
            foreach (var file in scriptFiles)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (int.TryParse(fileName, out var appId))
                {
                    var game = new GameInfo
                    {
                        AppId = appId,
                        LuaFilePath = file,
                        LuaFileTime = File.GetLastWriteTime(file),
                        IsDisabled = false
                    };
                    ParseLuaContent(game);
                    result.Add(game);
                }
            }

            var disableFolder = Path.Combine(luaFolder, "Disable");
            if (Directory.Exists(disableFolder))
            {
                var disabledFiles = GetScriptFiles(disableFolder);
                foreach (var file in disabledFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (int.TryParse(fileName, out var appId))
                    {
                        if (result.Any(g => g.AppId == appId)) continue;

                        var game = new GameInfo
                        {
                            AppId = appId,
                            LuaFilePath = file,
                            LuaFileTime = File.GetLastWriteTime(file),
                            IsDisabled = true
                        };
                        ParseLuaContent(game);
                        result.Add(game);
                    }
                }
            }

            return result;
        });
    }

    public async Task<GameInfo?> ParseLuaFileAsync(int appId)
    {
        return await Task.Run(() =>
        {
            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            var ksPath = Path.Combine(luaFolder, $"{appId}.ks");
            var luaPath = Path.Combine(luaFolder, $"{appId}.lua");
            var filePath = File.Exists(ksPath) ? ksPath : (File.Exists(luaPath) ? luaPath : null);
            if (filePath == null) return null;

            var game = new GameInfo
            {
                AppId = appId,
                LuaFilePath = filePath,
                LuaFileTime = File.GetLastWriteTime(filePath)
            };
            ParseLuaContent(game);
            return game;
        });
    }

    private static void ParseLuaContent(GameInfo game)
    {
        if (!File.Exists(game.LuaFilePath)) return;

        var content = File.ReadAllText(game.LuaFilePath);
        game.Depots.Clear();

        // 尝试从文件注释读取游戏名（-- app_name: xxx）
        var nameMatch = AppNameRegex.Match(content);
        if (nameMatch.Success)
        {
            game.GameName = nameMatch.Groups[1].Value.Trim();
        }
        // 如果文件未带名字，从本地 8.6万+ 游戏字典快速反查
        if (string.IsNullOrWhiteSpace(game.GameName))
        {
            var localName = TryGetLocalGameName(game.AppId);
            if (!string.IsNullOrEmpty(localName))
                game.GameName = localName;
        }

        // Parse addtoken
        var tokenMatch = AddTokenRegex.Match(content);
        if (tokenMatch.Success)
            game.Token = tokenMatch.Groups[2].Value;

        // Parse depots from addappid(depotId, flag, "key")
        var depotMatches = AddDepotRegex.Matches(content);
        foreach (Match match in depotMatches)
        {
            var depotId = int.Parse(match.Groups[1].Value);
            var key = match.Groups[3].Value;
            game.Depots.Add(new DepotInfo { DepotId = depotId, Key = key });
        }

        // Parse active manifest pins
        var activePins = new Dictionary<int, string>();
        var activeMatches = ManifestPinRegex.Matches(content);
        foreach (Match match in activeMatches)
        {
            var depotId = int.Parse(match.Groups[1].Value);
            activePins[depotId] = match.Groups[2].Value;
        }

        // Parse commented manifest pins
        var commentedPins = new Dictionary<int, string>();
        var commentedMatches = ManifestPinCommentedRegex.Matches(content);
        foreach (Match match in commentedMatches)
        {
            var depotId = int.Parse(match.Groups[1].Value);
            commentedPins[depotId] = match.Groups[2].Value;
        }

        // Merge into depots
        var allDepotIds = game.Depots.Select(d => d.DepotId)
            .Union(activePins.Keys)
            .Union(commentedPins.Keys)
            .Distinct()
            .ToList();

        foreach (var depotId in allDepotIds)
        {
            var existing = game.Depots.FirstOrDefault(d => d.DepotId == depotId);
            if (existing != null)
            {
                if (activePins.TryGetValue(depotId, out var activeId))
                {
                    existing.ManifestId = activeId;
                    existing.IsPinned = true;
                }
                else if (commentedPins.TryGetValue(depotId, out var commentedId))
                {
                    existing.ManifestId = commentedId;
                    existing.IsPinned = false;
                }
            }
            else
            {
                string manifestId = "";
                bool isPinned = false;
                if (activePins.TryGetValue(depotId, out var aid)) { manifestId = aid; isPinned = true; }
                else if (commentedPins.TryGetValue(depotId, out var cid)) { manifestId = cid; }
                game.Depots.Add(new DepotInfo { DepotId = depotId, Key = "", ManifestId = manifestId, IsPinned = isPinned });
            }
        }

        game.IsManifestPinned = activePins.Count > 0;
    }

    private static string? FindScriptInFolder(string folder, int appId)
    {
        if (!Directory.Exists(folder)) return null;
        var ks = Path.Combine(folder, $"{appId}.ks");
        if (File.Exists(ks)) return ks;
        var lua = Path.Combine(folder, $"{appId}.lua");
        if (File.Exists(lua)) return lua;
        return null;
    }

    public async Task SetManifestPinAsync(int appId, bool pin, Dictionary<int, string>? manifestIds = null)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var filePath = FindScriptInFolder(luaFolder, appId);
        if (filePath == null) return;

        var content = await File.ReadAllTextAsync(filePath);
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        if (pin && manifestIds != null)
        {
            foreach (var kvp in manifestIds)
            {
                var depotId = kvp.Key;
                var newManifestId = kvp.Value;
                var targetLine = $"setManifestid({depotId},\"{newManifestId}\",0)";
                UpdateManifestLine(lines, depotId, targetLine);
            }
        }
        else if (!pin)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var match = ManifestPinRegex.Match(lines[i]);
                if (match.Success && !lines[i].TrimStart().StartsWith("--"))
                {
                    lines[i] = "--" + (lines[i].Length > 0 && lines[i][0] == '\t' ? "" : "") + lines[i].TrimStart();
                    if (!lines[i].StartsWith("--"))
                        lines[i] = "--" + lines[i];
                }
            }
        }

        await File.WriteAllTextAsync(filePath, string.Join("\n", lines));
    }

    private static void UpdateManifestLine(List<string> lines, int depotId, string newLine)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var active = ManifestPinRegex.Match(lines[i]);
            var commented = ManifestPinCommentedRegex.Match(lines[i]);

            if (active.Success && int.Parse(active.Groups[1].Value) == depotId)
            {
                lines[i] = newLine;
                return;
            }
            if (commented.Success && int.Parse(commented.Groups[1].Value) == depotId)
            {
                lines[i] = newLine;
                return;
            }
        }

        var insertAt = lines.Count - 1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (AddDepotRegex.IsMatch(lines[i]) || AddAppIdRegex.IsMatch(lines[i]) || AddTokenRegex.IsMatch(lines[i]))
            {
                insertAt = i + 1;
                break;
            }
        }
        lines.Insert(insertAt, newLine);
    }

    public async Task AddLuaFileAsync(string sourceFilePath)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(luaFolder, fileName);

        await Task.Run(() => File.Copy(sourceFilePath, destPath, true));
    }

    public async Task AddBinFileAsync(string sourceFilePath)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return;

        var statsDir = Path.Combine(steamPath, "appcache", "stats");
        Directory.CreateDirectory(statsDir);
        var fileName = Path.GetFileName(sourceFilePath);
        var destPath = Path.Combine(statsDir, fileName);

        await Task.Run(() => File.Copy(sourceFilePath, destPath, true));
    }

    public async Task DeleteLuaFileAsync(int appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        await Task.Run(() =>
        {
            var ks = Path.Combine(luaFolder, $"{appId}.ks");
            var lua = Path.Combine(luaFolder, $"{appId}.lua");
            if (File.Exists(ks)) try { File.Delete(ks); } catch { }
            if (File.Exists(lua)) try { File.Delete(lua); } catch { }
        });
    }

    public async Task DisableGameAsync(int appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var srcPath = FindScriptInFolder(luaFolder, appId);
        if (srcPath == null) return;

        var disableFolder = Path.Combine(luaFolder, "Disable");
        Directory.CreateDirectory(disableFolder);
        var ext = Path.GetExtension(srcPath);
        var destPath = Path.Combine(disableFolder, $"{appId}{ext}");

        await Task.Run(() =>
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(srcPath, destPath);
        });
    }

    public async Task EnableGameAsync(int appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder)) return;

        var disableFolder = Path.Combine(luaFolder, "Disable");
        var srcPath = FindScriptInFolder(disableFolder, appId);
        if (srcPath == null) return;

        var ext = Path.GetExtension(srcPath);
        var destPath = Path.Combine(luaFolder, $"{appId}{ext}");

        await Task.Run(() =>
        {
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(srcPath, destPath);
        });
    }

    public void StartWatching()
    {
        if (_isWatching) return;

        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder)) return;

        _watcher = new FileSystemWatcher(luaFolder, "*.*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += OnFilesChanged;
        _watcher.Changed += OnFilesChanged;
        _watcher.Deleted += OnFilesChanged;
        _watcher.Renamed += OnFilesChanged;
        _watcher.EnableRaisingEvents = true;
        _isWatching = true;
    }

    public void StopWatching()
    {
        if (!_isWatching || _watcher == null) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
        _isWatching = false;
    }

    private void OnFilesChanged(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath);
        if (!string.Equals(ext, ".ks", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".lua", StringComparison.OrdinalIgnoreCase))
            return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                    FilesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void Dispose()
    {
        StopWatching();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
