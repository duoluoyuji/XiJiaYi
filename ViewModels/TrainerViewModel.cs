using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

/// <summary>修改器：内置搜索（支持中英文）、热门/最新、一键下载与管理。</summary>
public partial class TrainerViewModel : ObservableObject
{
    private readonly ITrainerService _trainerService;
    private readonly IHttpClientProvider _httpClientProvider;

    private string TrainersDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "修改器");

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _currentSection = "hot";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TrainerInfo> _hotTrainers = new();

    [ObservableProperty]
    private ObservableCollection<TrainerInfo> _newTrainers = new();

    [ObservableProperty]
    private ObservableCollection<TrainerInfo> _searchResults = new();

    [ObservableProperty]
    private ObservableCollection<TrainerInfo> _downloadedTrainers = new();

    private DispatcherTimer? _statusTimer;

    public TrainerViewModel(ITrainerService trainerService, IHttpClientProvider httpClientProvider)
    {
        _trainerService = trainerService;
        _httpClientProvider = httpClientProvider;
        LoadDownloaded();
    }

    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => { _statusTimer?.Stop(); StatusMessage = string.Empty; };
        _statusTimer.Start();
    }

    [RelayCommand]
    private async Task LoadSectionsAsync()
    {
        if (IsSearching) return;
        IsSearching = true;
        StatusMessage = "正在加载热门/最新修改器...";
        try
        {
            var hot = await _trainerService.GetHotTrainersAsync(12);
            var newest = await _trainerService.GetNewReleasesAsync(12);
            HotTrainers = new ObservableCollection<TrainerInfo>(hot);
            NewTrainers = new ObservableCollection<TrainerInfo>(newest);
            StatusMessage = hot.Count == 0 && newest.Count == 0 ? "未能获取列表，请检查网络后重试" : "加载完成";
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
            LogService.Error("修改器", $"加载列表失败: {ex}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText) || IsSearching) return;
        IsSearching = true;
        StatusMessage = $"正在搜索：{SearchText}（支持中文游戏名）...";
        try
        {
            var result = await _trainerService.SearchTrainersSmartAsync(SearchText);
            SearchResults = new ObservableCollection<TrainerInfo>(result);
            CurrentSection = "search";
            StatusMessage = result.Count == 0
                ? "未找到相关修改器，请换一个名称试试（如英文名）"
                : $"找到 {result.Count} 个相关修改器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败：{ex.Message}";
            LogService.Error("修改器", $"搜索失败: {ex}");
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void ShowSection(string section) => CurrentSection = section;

    [RelayCommand]
    private async Task DownloadAsync(TrainerInfo trainer)
    {
        if (trainer.IsDownloading) return;
        trainer.IsDownloading = true;
        trainer.DownloadProgress = 0;
        try
        {
            StatusMessage = $"正在获取 {trainer.GameName} 下载地址...";
            var url = trainer.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = await _trainerService.GetDownloadUrlAsync(trainer.PageUrl) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidOperationException("未找到下载链接（可能该修改器来源受限）");
                trainer.DownloadUrl = url;
            }

            var dir = Path.Combine(TrainersDir, SanitizeName(trainer.GameName));
            Directory.CreateDirectory(dir);

            using var client = _httpClientProvider.GetClient("trainer-download", TimeSpan.FromMinutes(20));
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            // 用服务器返回的真实文件名保存（否则 URL 末尾的 token 会变成无扩展名的乱码文件）
            var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
                           ?? resp.Content.Headers.ContentDisposition?.FileName
                           ?? ResolveFileName(url, trainer.GameName);
            fileName = SanitizeName(fileName.Trim('"'));
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 120)
                fileName = ResolveFileName(url, trainer.GameName);
            var dest = Path.Combine(dir, fileName);

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total > 0)
                    trainer.DownloadProgress = read * 100.0 / total;
            }

            // zip 压缩包自动解压并定位修改器主程序，保证“打开”能直接用
            if (dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(dest, dir, true);
                    var exe = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase));
                    if (exe != null)
                    {
                        try { File.Delete(dest); } catch { }
                        dest = exe;
                    }
                }
                catch (Exception zipEx)
                {
                    LogService.Warn("修改器", $"zip 解压失败（保留原包）: {zipEx.Message}");
                }
            }
            trainer.LocalPath = dest;
            trainer.IsDownloaded = true;
            LoadDownloaded();
            StatusMessage = $"已下载：{trainer.GameName} → {dir}";
            LogService.Info("修改器", $"下载完成: {trainer.GameName} -> {dest}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"下载失败：{ex.Message}";
            LogService.Error("修改器", $"下载失败: {ex}");
        }
        finally
        {
            trainer.IsDownloading = false;
        }
    }

    [RelayCommand]
    private void LaunchTrainer(TrainerInfo trainer)
    {
        var file = trainer.LocalPath;
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            StatusMessage = "文件不存在，可能已被移动";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file)
            });
            StatusMessage = $"已启动：{Path.GetFileName(file)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"启动失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenTrainerFolder() => Process.Start(new ProcessStartInfo(TrainersDir) { UseShellExecute = true });

    [RelayCommand]
    private void OpenTrainerPage(TrainerInfo trainer)
    {
        if (!string.IsNullOrWhiteSpace(trainer.PageUrl))
            Process.Start(new ProcessStartInfo(trainer.PageUrl) { UseShellExecute = true });
    }

    private void LoadDownloaded()
    {
        var list = new List<TrainerInfo>();
        try
        {
            if (Directory.Exists(TrainersDir))
            {
                foreach (var dir in Directory.GetDirectories(TrainersDir))
                {
                    var exe = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(dir, "*.zip", SearchOption.AllDirectories).FirstOrDefault();
                    if (exe == null) continue;
                    list.Add(new TrainerInfo
                    {
                        GameName = Path.GetFileName(dir),
                        LocalPath = exe,
                        IsDownloaded = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("修改器", $"读取已下载列表失败: {ex.Message}");
        }
        DownloadedTrainers = new ObservableCollection<TrainerInfo>(list);
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat((name ?? "Trainer").Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static string ResolveFileName(string url, string gameName)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name) && name.Length < 120) return name;
        }
        catch { }
        return $"{SanitizeName(gameName)}.zip";
    }
}