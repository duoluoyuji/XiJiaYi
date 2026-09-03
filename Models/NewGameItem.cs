using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class NewGameItem : ObservableObject
{
    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CoverUrl { get; init; } = string.Empty;
    public string PriceText { get; init; } = string.Empty;
    public string DiscountText { get; init; } = string.Empty;
    public string PlatformText { get; init; } = string.Empty;

    [ObservableProperty]
    private string _releaseDateText = "上线时间获取中";

    [ObservableProperty]
    private bool _isImported;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importStatus = string.Empty;
}
