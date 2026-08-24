using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

/// <summary>修改器功能：集成开源项目 Game-Cheats-Manager（GCM），支持中英文搜索与下载管理。</summary>
public partial class TrainerViewModel : ObservableObject
{
    private const string ProjectPage = "https://github.com/dyang886/Game-Cheats-Manager/releases";
    private const string ApiUrl = "https://api.github.com/repos/dyang886/Game-Cheats-Manager/releases/latest";

    private string ToolDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "GameCheatsManager");

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private int _downloadProgress;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isReady;

    public TrainerViewModel()
    {
        RefreshState();
    }

    private string? FindExe()
    {
        if (!Directory.Exists(ToolDir)) return null;
        try
        {
            return Directory.GetFiles(ToolDir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private void RefreshState()
    {
        IsReady = FindExe() != null;
        StatusText = IsReady ? "已下载，可点击「启动 Game-Cheats-Manager」" : "尚未下载，点击下方按钮从官方发布页获取（支持中英文搜索）";
    }

    [RelayCommand]
    private void OpenProjectPage()
    {
        Process.Start(new ProcessStartInfo(ProjectPage) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        DownloadProgress = 0;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("XiJiaYi/1.7");

            StatusText = "正在获取官方最新版本信息...";
            var json = await client.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            var assetUrl = string.Empty;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.Contains("Portable", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                    break;
                }
            }
            if (string.IsNullOrEmpty(assetUrl))
                throw new InvalidOperationException("官方发布页未找到免安装包");

            Directory.CreateDirectory(ToolDir);
            var zipPath = Path.Combine(Path.GetTempPath(), $"GCM_{Guid.NewGuid():N}.zip");
            try
            {
                StatusText = "正在下载 Game-Cheats-Manager...";
                using var resp = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                        DownloadProgress = (int)(read * 100 / total);
                }

                StatusText = "正在解压安装（免安装版）...";
                var appDir = Path.Combine(ToolDir, "app");
                if (Directory.Exists(appDir)) Directory.Delete(appDir, true);
                ZipFile.ExtractToDirectory(zipPath, appDir);
                if (FindExe() == null)
                    throw new InvalidOperationException("解压后未找到主程序");
                RefreshState();
                StatusText = $"下载完成（{tag.TrimStart('v')}），点击「启动 Game-Cheats-Manager」即可使用";
                LogService.Info("修改器", $"Game-Cheats-Manager {tag} 下载完成");
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败：{ex.Message}";
            LogService.Error("修改器", $"GCM 下载失败: {ex}");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void Launch()
    {
        var exe = FindExe();
        if (exe == null)
        {
            StatusText = "请先下载 Game-Cheats-Manager";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });
            StatusText = "已启动 Game-Cheats-Manager";
            LogService.Info("修改器", "已启动 Game-Cheats-Manager");
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败：{ex.Message}";
            LogService.Error("修改器", $"GCM 启动失败: {ex}");
        }
    }
}