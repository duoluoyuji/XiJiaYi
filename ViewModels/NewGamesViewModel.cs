using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HtmlAgilityPack;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class NewGamesViewModel : ObservableObject
{
    private const string TopSellersUrl = "https://store.steampowered.com/search/results/?query=&start=0&count=50&dynamic_data=&sort_by=_ASC&snr=1_7_7_7000_7&filter=topsellers&category1=998&hidef2p=1&infinite=1&cc=cn&l=schinese";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly ScriptDownloadViewModel _importViewModel;
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XiJiaYi", "cache", "top-sellers.json");

    public ObservableCollection<NewGameItem> Games { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "正在准备热门推荐...";

    [ObservableProperty]
    private string _lastUpdatedText = string.Empty;

    public NewGamesViewModel(
        IHttpClientProvider httpClientProvider,
        ISteamPathService steamPathService,
        ScriptDownloadViewModel importViewModel)
    {
        _httpClientProvider = httpClientProvider;
        _steamPathService = steamPathService;
        _importViewModel = importViewModel;
    }

    [RelayCommand]
    private Task LoadAsync() => LoadCoreAsync(false);

    [RelayCommand]
    private Task RefreshAsync() => LoadCoreAsync(true);

    private async Task LoadCoreAsync(bool forceRefresh)
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = forceRefresh ? "正在刷新 Steam 热门游戏..." : "正在获取 Steam 热门游戏...";

        CacheDocument? cached = ReadCache();
        if (!forceRefresh && cached != null && DateTimeOffset.UtcNow - cached.FetchedAt < CacheLifetime)
        {
            ShowItems(cached.Items);
            LastUpdatedText = $"更新于 {cached.FetchedAt.ToLocalTime():MM-dd HH:mm}";
            StatusMessage = $"已加载 {Games.Count} 款热门游戏";
            IsLoading = false;
            return;
        }

        try
        {
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-top-sellers", TimeSpan.FromSeconds(20),
                client => client.GetStringAsync(TopSellersUrl));
            var items = ParseTopSellers(json);
            if (items.Count == 0)
                throw new InvalidDataException("Steam 返回的游戏列表为空");

            ShowItems(items);
            var fetchedAt = DateTimeOffset.UtcNow;
            LastUpdatedText = $"更新于 {fetchedAt.ToLocalTime():MM-dd HH:mm}";
            WriteCache(new CacheDocument(fetchedAt, items));
            StatusMessage = $"已加载 {Games.Count} 款热门游戏";
        }
        catch (Exception ex)
        {
            if (cached is { Items.Count: > 0 })
            {
                ShowItems(cached.Items);
                LastUpdatedText = $"缓存于 {cached.FetchedAt.ToLocalTime():MM-dd HH:mm}";
                StatusMessage = "Steam 暂时无法访问，当前显示上次缓存，可稍后手动刷新";
            }
            else
            {
                StatusMessage = $"获取失败：{DescribeNetworkError(ex)}";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ShowItems(IEnumerable<CacheItem> items)
    {
        Games.Clear();
        foreach (var item in items.Take(30))
        {
            bool imported = IsImported(item.AppId);
            Games.Add(new NewGameItem
            {
                AppId = item.AppId,
                Name = item.Name,
                CoverUrl = item.CoverUrl,
                PriceText = item.PriceText,
                DiscountText = item.DiscountText,
                PlatformText = item.PlatformText,
                ReleaseDateText = item.ReleaseDateText,
                IsImported = imported,
                ImportStatus = imported ? "在 Steam 库中可见" : string.Empty
            });
        }
    }

    private static List<CacheItem> ParseTopSellers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results_html", out var htmlNode))
            throw new InvalidDataException("Steam 返回的数据格式异常");

        string html = htmlNode.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(html))
            return new List<CacheItem>();

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);
        var rows = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'search_result_row')]");
        if (rows == null || rows.Count == 0)
            return new List<CacheItem>();

        var result = new List<CacheItem>();
        foreach (var row in rows)
        {
            // AppId
            var appIdAttr = row.GetAttributeValue("data-ds-appid", string.Empty);
            if (string.IsNullOrWhiteSpace(appIdAttr) || !int.TryParse(appIdAttr, out int appId) || appId <= 0)
                continue;

            // Title
            var titleNode = row.SelectSingleNode(".//span[@class='title']");
            string name = WebUtility.HtmlDecode(titleNode?.InnerText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = $"App {appId}";

            // Exclude DLC, Soundtracks or other non-games if any leak through
            if (name.Contains("DLC", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Soundtrack", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Expansion", StringComparison.OrdinalIgnoreCase))
                continue;

            // Price & Discount
            var priceBlock = row.SelectSingleNode(".//div[contains(@class, 'search_price_discount_combined')]")
                             ?? row.SelectSingleNode(".//div[contains(@class, 'discount_block')]");
            int priceFinal = -1;
            if (priceBlock != null)
            {
                var pfStr = priceBlock.GetAttributeValue("data-price-final", "-1");
                _ = int.TryParse(pfStr, out priceFinal);
            }

            var finalPriceNode = row.SelectSingleNode(".//div[contains(@class, 'discount_final_price')]")
                                 ?? row.SelectSingleNode(".//div[contains(@class, 'search_price')]");
            string priceText = WebUtility.HtmlDecode(finalPriceNode?.InnerText ?? string.Empty).Trim();

            // Exclude free items (Free-to-Play, 0 price, or containing 免费 / Free)
            if (priceFinal == 0 ||
                priceText.Contains("免费", StringComparison.Ordinal) ||
                priceText.Contains("Free", StringComparison.OrdinalIgnoreCase))
                continue;

            var discountNode = row.SelectSingleNode(".//div[contains(@class, 'discount_pct')]");
            string discount = WebUtility.HtmlDecode(discountNode?.InnerText ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(priceText))
            {
                priceText = priceFinal > 0 ? $"¥{priceFinal / 100m:0.##}" : "价格暂无";
            }

            // Cover
            var imgNode = row.SelectSingleNode(".//div[contains(@class, 'search_capsule')]//img");
            string cover = imgNode?.GetAttributeValue("src", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cover))
            {
                cover = $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
            }

            // Platforms
            var win = row.SelectSingleNode(".//span[contains(@class, 'platform_img') and contains(@class, 'win')]") != null;
            var mac = row.SelectSingleNode(".//span[contains(@class, 'platform_img') and contains(@class, 'mac')]") != null;
            var linux = row.SelectSingleNode(".//span[contains(@class, 'platform_img') and contains(@class, 'linux')]") != null;
            var platforms = new List<string>();
            if (win) platforms.Add("Windows");
            if (mac) platforms.Add("macOS");
            if (linux) platforms.Add("Linux");
            string platformText = platforms.Count > 0 ? string.Join(" / ", platforms) : "Windows";

            // Release Date
            var relNode = row.SelectSingleNode(".//div[contains(@class, 'search_released')]");
            string rawRelease = WebUtility.HtmlDecode(relNode?.InnerText ?? string.Empty).Trim();
            string releaseDateText;
            if (string.IsNullOrWhiteSpace(rawRelease))
            {
                releaseDateText = "上线日期未公布";
            }
            else if (rawRelease.Contains("即将") || rawRelease.Contains("预"))
            {
                releaseDateText = $"预计上线 {rawRelease}";
            }
            else
            {
                releaseDateText = $"上线于 {rawRelease}";
            }

            result.Add(new CacheItem(
                appId, name, cover, priceText, discount, platformText, releaseDateText));

            if (result.Count >= 30)
                break;
        }

        return result;
    }

    [RelayCommand]
    private async Task ImportGameAsync(NewGameItem game)
    {
        if (game == null || game.IsImporting) return;
        game.IsImporting = true;
        game.ImportStatus = "正在查询清单与密钥数据...";
        try
        {
            bool success = await _importViewModel.ImportGameAsync(game.AppId);
            game.IsImported = IsImported(game.AppId);
            if (success || game.IsImported)
            {
                game.IsImported = true;
                game.ImportStatus = "入库成功（重启查看）";
                StatusMessage = $"《{game.Name}》入库成功，重启 Steam 即可查看";
            }
            else
            {
                game.ImportStatus = ExplainImportFailure(_importViewModel.StatusMessage);
                StatusMessage = $"《{game.Name}》{game.ImportStatus}";
            }
        }
        catch (Exception ex)
        {
            game.ImportStatus = $"入库失败：{ex.Message}";
            StatusMessage = $"《{game.Name}》入库失败";
        }
        finally
        {
            game.IsImporting = false;
        }
    }

    [RelayCommand]
    private static void OpenStore(NewGameItem game)
    {
        if (game == null) return;
        Process.Start(new ProcessStartInfo($"https://store.steampowered.com/app/{game.AppId}/")
        {
            UseShellExecute = true
        });
    }

    private bool IsImported(int appId)
    {
        try
        {
            var folder = _steamPathService.GetLuaFolder();
            return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, $"{appId}.lua"));
        }
        catch { return false; }
    }

    private static string ExplainImportFailure(string status) => status switch
    {
        var s when s.Contains("缺少解密密钥", StringComparison.Ordinal) =>
            "缺少解密密钥（等待更新）",
        var s when s.Contains("查询失败", StringComparison.Ordinal) || s.Contains("获取短码失败", StringComparison.Ordinal) =>
            "数据源尚未收录（等待更新）",
        var s when s.Contains("未找到 Lua", StringComparison.Ordinal) || s.Contains("生成失败", StringComparison.Ordinal) =>
            "未生成有效清单（等待更新）",
        var s when s.Contains("下载密钥", StringComparison.Ordinal) =>
            "密钥文件下载失败，请检查网络",
        var s when s.Contains("Steam 路径", StringComparison.Ordinal) =>
            "未配置 Steam 安装路径，请先在设置中配置",
        var s when s.Contains("下载失败", StringComparison.Ordinal) =>
            "远程文件下载失败，请检查网络",
        var s when s.Contains("无效", StringComparison.Ordinal) =>
            "无效的游戏 ID",
        _ => string.IsNullOrWhiteSpace(status) ? "入库失败（等待更新）" : $"入库失败：{status}"
    };

    private static string FormatPrice(int cents, string currency)
    {
        if (cents < 0) return "价格暂无";
        if (cents == 0) return "免费或暂未定价";
        var symbol = currency.Equals("CNY", StringComparison.OrdinalIgnoreCase) ? "¥" : currency + " ";
        return $"{symbol}{cents / 100m:0.##}";
    }

    private static string DescribeNetworkError(Exception ex) => ex switch
    {
        TaskCanceledException => "连接 Steam 超时，请检查网络或代理后重试",
        HttpRequestException => "无法连接 Steam 商店，请检查网络或代理后重试",
        JsonException => "Steam 返回的数据格式异常，请稍后重试",
        _ => ex.Message
    };

    private CacheDocument? ReadCache()
    {
        try
        {
            return File.Exists(_cachePath)
                ? JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(_cachePath))
                : null;
        }
        catch { return null; }
    }

    private void WriteCache(CacheDocument cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache));
        }
        catch { }
    }

    private sealed record CacheDocument(DateTimeOffset FetchedAt, List<CacheItem> Items);

    private sealed class CacheItem
    {
        public CacheItem(int appId, string name, string coverUrl, string priceText,
            string discountText, string platformText, string releaseDateText)
        {
            AppId = appId;
            Name = name;
            CoverUrl = coverUrl;
            PriceText = priceText;
            DiscountText = discountText;
            PlatformText = platformText;
            ReleaseDateText = releaseDateText;
        }

        public int AppId { get; set; }
        public string Name { get; set; }
        public string CoverUrl { get; set; }
        public string PriceText { get; set; }
        public string DiscountText { get; set; }
        public string PlatformText { get; set; }
        public string ReleaseDateText { get; set; }
    }
}
