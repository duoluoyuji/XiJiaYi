using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

/// <summary>本地扫描到的 Steam 存档条目（userdata\&lt;账号&gt;\&lt;AppID&gt;\remote）。</summary>
public partial class SaveGameEntry : ObservableObject
{
    [ObservableProperty]
    private int _appId;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _steamId3 = string.Empty;

    [ObservableProperty]
    private string _accountName = string.Empty;

    [ObservableProperty]
    private string _remotePath = string.Empty;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private DateTime _lastWriteTime;

    [ObservableProperty]
    private bool _isCurrentAccount;

    /// <summary>该游戏所有需要备份/同步的存档根目录（remote/ugc/local/自定义目录）。</summary>
    public List<string> SaveRoots { get; set; } = new();

    /// <summary>由 SteamID3 换算出的 SteamID64（76561197960265728 + 账号ID）。</summary>
    public string SteamId64 =>
        long.TryParse(SteamId3, out var id3) && id3 > 0
            ? (76561197960265728UL + (ulong)id3).ToString()
            : string.Empty;

    public string DisplayName => $"{GameName}（AppID {AppId}）";

    public string AccountDisplay =>
        string.IsNullOrEmpty(AccountName)
            ? $"账号 {SteamId3}"
            : IsCurrentAccount ? $"{AccountName}（当前账号）" : AccountName;

    public string SizeText => FormatSize(TotalBytes);

    public string InfoText =>
        $"{FileCount} 个文件 · {SizeText} · 最后修改 {LastWriteTime:yyyy-MM-dd HH:mm}";

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(double)bytes / (1L << 30):0.0} GB",
        >= 1L << 20 => $"{(double)bytes / (1L << 20):0.0} MB",
        >= 1L << 10 => $"{(double)bytes / (1L << 10):0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>本地备份条目。</summary>
public partial class LocalBackupEntry : ObservableObject
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private DateTime _createdAt;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private long _totalBytes;

    public string DisplayName =>
        $"备份于 {CreatedAt:yyyy-MM-dd HH:mm:ss} · {FileCount} 个文件 · {SaveGameEntry.FormatSize(TotalBytes)}";
}

/// <summary>云端备份条目（WebDAV 上的 zip 文件）。</summary>
public partial class CloudBackupEntry : ObservableObject
{
    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private DateTime? _modified;

    public string DisplayName =>
        $"{FileName} · {SaveGameEntry.FormatSize(Size)}{(Modified.HasValue ? $" · {Modified:yyyy-MM-dd HH:mm}" : "")}";
}
