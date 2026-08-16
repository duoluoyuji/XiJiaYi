using System.Windows;

namespace SteamLuaManager.Services;

/// <summary>皮肤目录：负责皮肤选项列表与运行时切换。</summary>
public static class SkinCatalog
{
    public record SkinOption(string Display, string Value);

    public static IReadOnlyList<SkinOption> Options { get; } = new List<SkinOption>
    {
        new("动漫·粉樱", "Sakura"),
        new("动漫·暗夜", "Starry"),
        new("动漫·蓝色幻想", "Neon"),
        new("动漫·夏日晴空", "Seaside"),
        new("动漫·校服少女", "School"),
        new("纯黑", "PureBlack"),
        new("深空灰", "DarkGray"),
        new("深海蓝", "DeepBlue"),
        new("暗夜紫", "DeepPurple"),
        new("墨绿", "DeepGreen"),
        new("酒红", "WineRed"),
    };

    /// <summary>把当前激活的皮肤字典替换为指定皮肤。</summary>
    public static void Apply(string value)
    {
        var app = Application.Current;
        if (app == null) return;

        var dicts = app.Resources.MergedDictionaries;
        for (var i = 0; i < dicts.Count; i++)
        {
            var source = dicts[i].Source?.ToString() ?? string.Empty;
            if (source.Contains("Skins/", StringComparison.OrdinalIgnoreCase))
            {
                dicts.RemoveAt(i);
                break;
            }
        }

        var assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
        var uri = new Uri($"/{assemblyName};component/Skins/{value}.xaml", UriKind.Relative);
        dicts.Add(new ResourceDictionary { Source = uri });
    }
}
