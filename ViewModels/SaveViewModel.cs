using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class SaveViewModel : ObservableObject
{
    private readonly ISaveService _saveService;
    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private ObservableCollection<SaveGameEntry> _saveGames = new();

    [ObservableProperty]
    private SaveGameEntry? _selectedSave;

    [ObservableProperty]
    private ObservableCollection<LocalBackupEntry> _localBackups = new();

    [ObservableProperty]
    private ObservableCollection<CloudBackupEntry> _cloudBackups = new();

    [ObservableProperty]
    private LocalBackupEntry? _selectedLocalBackup;

    [ObservableProperty]
    private CloudBackupEntry? _selectedCloudBackup;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _webDavUrl = string.Empty;

    [ObservableProperty]
    private string _webDavUser = string.Empty;

    [ObservableProperty]
    private string _webDavPassword = string.Empty;

    [ObservableProperty]
    private string _originalSteamId = string.Empty;

    [ObservableProperty]
    private string _customRules = string.Empty;

    [ObservableProperty]
    private bool _replaceIdsEnabled = true;

    [ObservableProperty]
    private ObservableCollection<string> _accounts = new();

    [ObservableProperty]
    private string _selectedAccount = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _selectedSaveCustomFolders = new();

    [ObservableProperty]
    private ObservableCollection<SaveCandidate> _saveCandidates = new();

    [ObservableProperty]
    private SaveCandidate? _selectedSaveCandidate;

    [ObservableProperty]
    private bool _isScanningSaveLocations;

    [ObservableProperty]
    private string _saveScanStatus = string.Empty;

    [ObservableProperty]
    private string _bindAppId = string.Empty;

    private bool _suppressSelectionHandler;
    private List<SaveGameEntry> _allSaveEntries = new();

    public SaveViewModel(ISaveService saveService, ISteamPathService steamPathService, ISettingsService settingsService)
    {
        _saveService = saveService;
        _steamPathService = steamPathService;
        _settingsService = settingsService;

        var settings = settingsService.Load();
        WebDavUrl = settings.WebDavUrl;
        WebDavUser = settings.WebDavUser;
        WebDavPassword = settings.WebDavPassword;
    }

    partial void OnSelectedSaveChanged(SaveGameEntry? value)
    {
        if (_suppressSelectionHandler || value == null) return;
        RefreshCustomFolders(value);
        _ = RefreshBackupListsAsync(value);
    }

    partial void OnSelectedAccountChanged(string value)
    {
        ApplySaveFilter();
    }

    private void ApplySaveFilter()
    {
        _suppressSelectionHandler = true;
        if (string.IsNullOrEmpty(SelectedAccount) || SelectedAccount == "全部账号")
        {
            SaveGames = new ObservableCollection<SaveGameEntry>(_allSaveEntries);
        }
        else
        {
            var entry = _allSaveEntries.FirstOrDefault(e => e.AccountDisplay == SelectedAccount);
            if (entry != null)
            {
                var id3 = entry.SteamId3;
                SaveGames = new ObservableCollection<SaveGameEntry>(
                    _allSaveEntries.Where(e => e.SteamId3 == id3));
            }
            else
            {
                SaveGames = new ObservableCollection<SaveGameEntry>();
            }
        }
        SelectedSave = SaveGames.FirstOrDefault();
        _suppressSelectionHandler = false;
    }

    private void RefreshCustomFolders(SaveGameEntry? entry)
    {
        if (entry == null)
        {
            SelectedSaveCustomFolders.Clear();
            return;
        }
        var settings = _settingsService.Load();
        var list = settings.CustomSaveFolders.TryGetValue(entry.AppId.ToString(), out var l) ? l : new List<string>();
        SelectedSaveCustomFolders = new ObservableCollection<string>(list);
    }

    private async Task RefreshBackupListsAsync(SaveGameEntry entry)
    {
        IsBusy = true;
        BusyText = "正在读取备份列表...";
        try
        {
            LocalBackups = new ObservableCollection<LocalBackupEntry>(_saveService.ListLocalBackups(entry));
            SelectedLocalBackup = LocalBackups.FirstOrDefault();
            try
            {
                CloudBackups = new ObservableCollection<CloudBackupEntry>(await _saveService.ListCloudBackupsAsync(entry, CancellationToken.None));
                SelectedCloudBackup = CloudBackups.FirstOrDefault();
            }
            catch (Exception ex)
            {
                CloudBackups.Clear();
                StatusMessage = $"云端列表读取失败：{ex.Message}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        BusyText = "正在扫描 Steam 本地存档...";
        try
        {
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                await ShowDialogAsync("提示", "未检测到 Steam 安装路径，请先在设置页面配置。");
                SaveGames.Clear();
                return;
            }

            var list = _saveService.ScanLocalSaves();
            _suppressSelectionHandler = true;
            _allSaveEntries = list;

            var accountDisplays = list
                .GroupBy(e => e.SteamId3)
                .Select(g => g.First())
                .OrderByDescending(e => e.IsCurrentAccount)
                .Select(e => e.AccountDisplay)
                .ToList();
            var current = accountDisplays.FirstOrDefault(a => a.Contains("当前账号"));
            accountDisplays.Insert(0, "全部账号");
            Accounts = new ObservableCollection<string>(accountDisplays);
            SelectedAccount = current ?? "全部账号";

            ApplySaveFilter();
            _suppressSelectionHandler = false;

            if (list.Count == 0)
            {
                StatusMessage = "未在 userdata 目录中找到带存档的游戏（需 Steam 云存档目录中存在 remote 文件夹）。";
                LocalBackups.Clear();
                CloudBackups.Clear();
            }
            else if (SelectedSave != null)
            {
                RefreshCustomFolders(SelectedSave);
                await RefreshBackupListsAsync(SelectedSave);
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"扫描存档失败：{ex.Message}");
            LogService.Error("存档", $"扫描存档失败: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BackupLocalAsync()
    {
        var entry = RequireSave();
        if (entry == null) return;
        try
        {
            IsBusy = true;
            BusyText = "正在备份到本地...";
            var backup = _saveService.BackupToLocal(entry);
            await RefreshBackupListsAsync(entry);
            StatusMessage = $"已备份：{backup.Path}";
            LogService.Info("存档", $"本地备份完成: {backup.Path}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"备份失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSaveFolder()
    {
        var entry = SelectedSave;
        if (entry == null || string.IsNullOrEmpty(entry.RemotePath)) return;
        if (!Directory.Exists(entry.RemotePath))
        {
            StatusMessage = "存档目录不存在，可能已被删除。";
            return;
        }
        Process.Start(new ProcessStartInfo(entry.RemotePath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Saves", "本地备份");
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task SaveCloudSettingsAsync()
    {
        var settings = _settingsService.Load();
        settings.WebDavUrl = WebDavUrl?.Trim() ?? string.Empty;
        settings.WebDavUser = WebDavUser?.Trim() ?? string.Empty;
        settings.WebDavPassword = WebDavPassword ?? string.Empty;
        _settingsService.Save(settings);
        StatusMessage = "云端设置已保存";
        LogService.Info("存档", "已保存云端(WebDAV)设置");
        if (SelectedSave != null)
            await RefreshBackupListsAsync(SelectedSave);
    }

    [RelayCommand]
    private async Task AddCustomFolderAsync()
    {
        var entry = SelectedSave;
        if (entry == null)
        {
            await ShowDialogAsync("提示", "请先选择要添加存档目录的游戏。");
            return;
        }

        var owner = new WindowInteropHelper(Application.Current.MainWindow).Handle;
        var folder = FolderPicker.PickFolder(null, owner);
        if (string.IsNullOrEmpty(folder)) return;

        var settings = _settingsService.Load();
        if (!settings.CustomSaveFolders.TryGetValue(entry.AppId.ToString(), out var list))
        {
            list = new List<string>();
            settings.CustomSaveFolders[entry.AppId.ToString()] = list;
        }
        if (!list.Contains(folder, StringComparer.OrdinalIgnoreCase))
            list.Add(folder);
        _settingsService.Save(settings);
        StatusMessage = $"已为 {entry.GameName} 添加存档目录：{folder}";
        LogService.Info("存档", $"自定义存档目录: {entry.AppId} -> {folder}");
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveCustomFolderAsync(string folder)
    {
        var entry = SelectedSave;
        if (entry == null || string.IsNullOrEmpty(folder)) return;

        var settings = _settingsService.Load();
        if (settings.CustomSaveFolders.TryGetValue(entry.AppId.ToString(), out var list))
        {
            list.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (list.Count == 0)
                settings.CustomSaveFolders.Remove(entry.AppId.ToString());
            _settingsService.Save(settings);
            StatusMessage = $"已移除存档目录：{folder}";
            await RefreshAsync();
        }
    }

    // ============ 常见存档位置扫描助手 ============

    [RelayCommand]
    private async Task ScanSaveLocationsAsync()
    {
        if (IsScanningSaveLocations) return;
        IsScanningSaveLocations = true;
        SaveScanStatus = "正在扫描常见存档位置（Documents / My Games / Saved Games / AppData）...";
        try
        {
            var candidates = await Task.Run(() => ScanCommonSaveLocations());
            SaveCandidates = new ObservableCollection<SaveCandidate>(candidates);
            SelectedSaveCandidate = SaveCandidates.FirstOrDefault();
            SaveScanStatus = candidates.Count == 0
                ? "未发现候选存档目录，可稍后重试或手动添加"
                : $"发现 {candidates.Count} 个候选目录，选中后点「绑定到选中游戏」";
        }
        catch (Exception ex)
        {
            SaveScanStatus = $"扫描失败：{ex.Message}";
            LogService.Error("存档", $"扫描常见位置失败: {ex}");
        }
        finally
        {
            IsScanningSaveLocations = false;
        }
    }

    [RelayCommand]
    private async Task BindSaveCandidateAsync()
    {
        var candidate = SelectedSaveCandidate;
        if (candidate == null)
        {
            await ShowDialogAsync("提示", "请先在候选列表中选中一个存档目录。");
            return;
        }

        var appId = 0;
        var fromManual = !string.IsNullOrWhiteSpace(BindAppId);
        if (fromManual)
        {
            if (!int.TryParse(BindAppId.Trim(), out appId) || appId <= 0)
            {
                await ShowDialogAsync("提示", "填写的 AppID 无效，请输入数字（lua 文件名就是 AppID）。");
                return;
            }
        }
        else if (SelectedSave != null)
        {
            appId = SelectedSave.AppId;
        }
        else
        {
            await ShowDialogAsync("提示", "请先在上方选中游戏，或在「AppID」框里填写目标游戏 AppID。");
            return;
        }

        var settings = _settingsService.Load();
        if (!settings.CustomSaveFolders.TryGetValue(appId.ToString(), out var list))
        {
            list = new List<string>();
            settings.CustomSaveFolders[appId.ToString()] = list;
        }
        if (!list.Contains(candidate.Path, StringComparer.OrdinalIgnoreCase))
            list.Add(candidate.Path);
        _settingsService.Save(settings);

        StatusMessage = $"已把 {candidate.Path} 绑定到 AppID {appId}";
        LogService.Info("存档", $"绑定存档目录: {appId} -> {candidate.Path}");
        await RefreshAsync();

        // 绑定后自动选中该游戏，方便直接查看/上传
        _suppressSelectionHandler = true;
        SelectedSave = SaveGames.FirstOrDefault(g => g.AppId == appId);
        _suppressSelectionHandler = false;
        if (SelectedSave != null)
        {
            RefreshCustomFolders(SelectedSave);
            await RefreshBackupListsAsync(SelectedSave);
        }
    }

    private static List<SaveCandidate> ScanCommonSaveLocations()
    {
        var roots = new List<string>();
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs))
        {
            roots.Add(docs);
            roots.Add(Path.Combine(docs, "My Games"));
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            roots.Add(Path.Combine(profile, "Saved Games"));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "windows", "adobe", "google", "mozilla", "edge", "tencent", "wechat", "weixin", "qq",
            "qqex", "cache", "caches", "logs", "log", "temp", "tmp", "crashdumps", "package cache", "nuget", "pip",
            "codex", "programs", "npm", ".git", ".cache", "nvidia", "intel", "onedrive", "battle.net", "epic games",
            "roaming", "local", "locallow", "low", "telegram", "discord", "obs-studio", "steam", "ubisoft game launcher"
        };
        var strongExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sav", ".save", ".sol", ".sfs", ".slot", ".profile", ".savegame", ".prf", ".dat0", ".slot0"
        };
        for (var i = 0; i <= 9; i++)
            strongExts.Add($".sav{i}");

        var genericExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dat", ".bin", ".json", ".cfg", ".config", ".xml", ".ini", ".txt"
        };

        var results = new List<SaveCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, int depth)
        {
            if (depth > 3 || results.Count >= 150) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.') || exclude.Contains(name))
                    continue;

                if (depth >= 1)
                {
                    var candidate = Evaluate(sub, strongExts, genericExts);
                    if (candidate != null && seen.Add(sub))
                        results.Add(candidate);
                }
                Walk(sub, depth + 1);
            }
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            Walk(root, 0);
        }

        return results
            .OrderByDescending(c => c.LastWriteTime)
            .Take(150)
            .ToList();
    }

    private static SaveCandidate? Evaluate(
        string dir, HashSet<string> strongExts, HashSet<string> genericExts)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
            if (files.Length == 0 || files.Length > 2000) return null;

            var strong = 0;
            var recentGeneric = 0;
            long total = 0;
            var last = DateTime.MinValue;
            var now = DateTime.Now;

            foreach (var file in files)
            {
                var fi = new FileInfo(file);
                total += fi.Length;
                if (fi.LastWriteTime > last) last = fi.LastWriteTime;
                var ext = fi.Extension;
                if (strongExts.Contains(ext))
                    strong++;
                else if (genericExts.Contains(ext) && (now - fi.LastWriteTime).TotalDays <= 90)
                    recentGeneric++;
            }

            if (total > 300L * 1024 * 1024) return null;
            if (strong == 0 && recentGeneric < 3) return null;

            return new SaveCandidate
            {
                Path = dir,
                FileCount = files.Length,
                TotalBytes = total,
                LastWriteTime = last == DateTime.MinValue ? DateTime.Now : last
            };
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task UploadCloudAsync()
    {
        var entry = RequireSave();
        if (entry == null) return;
        try
        {
            var confirmed = await ShowConfirmAsync(
                "上传到云端",
                $"将把「{entry.GameName}（AppID {entry.AppId}）」的当前存档压缩上传到云端。\n\n" +
                "上传前会读取当前 remote 目录内容，不会影响本地存档。",
                "开始上传");
            if (!confirmed) return;

            IsBusy = true;
            BusyText = "正在上传到云端...";
            var progress = new Progress<string>(s => BusyText = s);
            var url = await _saveService.UploadToCloudAsync(entry, progress, CancellationToken.None);
            await RefreshBackupListsAsync(entry);
            StatusMessage = "上传成功";
            await ShowDialogAsync("上传成功", $"云端备份已生成：\n{url}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"上传失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshCloudAsync()
    {
        var entry = RequireSave();
        if (entry == null) return;
        try
        {
            IsBusy = true;
            BusyText = "正在读取云端列表...";
            CloudBackups = new ObservableCollection<CloudBackupEntry>(
                await _saveService.ListCloudBackupsAsync(entry, CancellationToken.None));
            SelectedCloudBackup = CloudBackups.FirstOrDefault();
            StatusMessage = CloudBackups.Count == 0
                ? "云端暂无该游戏的备份"
                : $"云端共 {CloudBackups.Count} 个备份";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"读取云端列表失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreLocalAsync()
    {
        var entry = RequireSave();
        var backup = SelectedLocalBackup;
        if (entry == null || backup == null) return;

        try
        {
            var confirmed = await ShowConfirmAsync(
                "从本地备份恢复",
                $"将用以下备份覆盖当前存档：\n{backup.DisplayName}\n\n" +
                "恢复前会自动把当前存档再备份一份，可放心操作。",
                "恢复");
            if (!confirmed) return;

            IsBusy = true;
            BusyText = "正在恢复本地备份...";
            _saveService.RestoreBackup(entry, backup.Path);
            await RefreshBackupListsAsync(entry);
            StatusMessage = "恢复完成";
            await ShowDialogAsync("恢复完成", $"已恢复备份：\n{backup.Path}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"恢复失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreCloudAsync()
    {
        var entry = RequireSave();
        var item = SelectedCloudBackup;
        if (entry == null || item == null) return;

        try
        {
            var confirmed = await ShowConfirmAsync(
                "从云端下载并恢复",
                $"将下载云端备份「{item.FileName}」并覆盖当前存档。\n\n" +
                "恢复前会自动把当前存档再备份一份。",
                "下载并恢复");
            if (!confirmed) return;

            IsBusy = true;
            BusyText = "正在下载云端备份...";
            var progress = new Progress<string>(s => BusyText = s);
            var result = await _saveService.DownloadCloudBackupAsync(entry, item, progress, CancellationToken.None);
            await RefreshBackupListsAsync(entry);
            await ShowDialogAsync("恢复完成", result.Summary);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"下载/恢复失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportPerfectSaveAsync()
    {
        var entry = RequireSave();
        if (entry == null) return;

        var dialog = new OpenFileDialog
        {
            Filter = "压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Title = "选择完美存档 ZIP 压缩包"
        };
        if (dialog.ShowDialog() != true) return;
        await ImportPerfectSaveCoreAsync(entry, dialog.FileName);
    }

    [RelayCommand]
    private async Task ImportPerfectSaveFolderAsync()
    {
        var entry = RequireSave();
        if (entry == null) return;

        var owner = new WindowInteropHelper(Application.Current.MainWindow).Handle;
        var folder = FolderPicker.PickFolder(null, owner);
        if (string.IsNullOrEmpty(folder)) return;
        await ImportPerfectSaveCoreAsync(entry, folder);
    }


    private async Task ImportPerfectSaveCoreAsync(SaveGameEntry entry, string sourcePath)
    {
        var replaceDesc = ReplaceIdsEnabled && !string.IsNullOrWhiteSpace(OriginalSteamId)
            ? $"\n• 将存档内原账号 ID（{OriginalSteamId.Trim()}）替换为本机账号"
            : ReplaceIdsEnabled
                ? "\n• 将按自定义规则替换账号 ID（未填原 ID 则仅执行自定义规则）"
                : "\n• 不替换账号 ID";

        var confirmed = await ShowConfirmAsync(
            "导入完美存档",
            $"目标游戏：{entry.GameName}（AppID {entry.AppId}）\n" +
            $"来源：{sourcePath}\n\n" +
            "执行内容：\n" +
            "• 自动备份当前存档到本地\n" +
            "• 清空原存档目录并覆盖为完美存档" +
            replaceDesc +
            "\n\n此操作会覆盖当前存档（原档已自动备份，可随时恢复）。",
            "开始导入");
        if (!confirmed) return;

        try
        {
            IsBusy = true;
            BusyText = "正在导入完美存档...";
            var progress = new Progress<string>(s => BusyText = s);
            var rules = ReplaceIdsEnabled ? CustomRules : string.Empty;
            var originalId = ReplaceIdsEnabled ? OriginalSteamId : null;
            var result = await _saveService.ImportPerfectSaveAsync(
                entry, sourcePath, originalId, rules, progress, CancellationToken.None);
            await RefreshBackupListsAsync(entry);
            await ShowDialogAsync("导入完成", result.Summary);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("错误", $"导入失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SearchPerfectSave()
    {
        var entry = SelectedSave;
        var keyword = entry == null
            ? "完美存档"
            : $"{entry.GameName} 完美存档 AppID {entry.AppId}";
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(keyword);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private SaveGameEntry? RequireSave()
    {
        if (SelectedSave != null) return SelectedSave;
        _ = ShowDialogAsync("提示", "请先在「本地存档」中选择一个游戏。");
        return null;
    }

    private static async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private static async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
