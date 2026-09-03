using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class DenuvoAuthDialog : Window, INotifyPropertyChanged
{
    private readonly IAuthorizationService _authService;
    public GameInfo Game { get; }

    public string CoverUrl => Game.CoverImagePath;
    public string GameName => Game.GameName;
    public uint AppId => (uint)Game.AppId;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(nameof(IsBusy)); }
    }

    private string _statusTitle = "正在检测...";
    public string StatusTitle
    {
        get => _statusTitle;
        set { _statusTitle = value; OnPropertyChanged(nameof(StatusTitle)); }
    }

    private Brush _statusTitleForeground = Brushes.Gray;
    public Brush StatusTitleForeground
    {
        get => _statusTitleForeground;
        set { _statusTitleForeground = value; OnPropertyChanged(nameof(StatusTitleForeground)); }
    }

    private Brush _statusBadgeBackground = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
    public Brush StatusBadgeBackground
    {
        get => _statusBadgeBackground;
        set { _statusBadgeBackground = value; OnPropertyChanged(nameof(StatusBadgeBackground)); }
    }

    private string _statusIconGlyph = "\uE7BA";
    public string StatusIconGlyph
    {
        get => _statusIconGlyph;
        set { _statusIconGlyph = value; OnPropertyChanged(nameof(StatusIconGlyph)); }
    }

    private Brush _statusIconForeground = Brushes.Gray;
    public Brush StatusIconForeground
    {
        get => _statusIconForeground;
        set { _statusIconForeground = value; OnPropertyChanged(nameof(StatusIconForeground)); }
    }

    private string _statusDetails = string.Empty;
    public string StatusDetails
    {
        get => _statusDetails;
        set { _statusDetails = value; OnPropertyChanged(nameof(StatusDetails)); }
    }

    private string _actionFeedbackMessage = "提示：支持正版号直接授权，或导入/拖入授权文件";
    public string ActionFeedbackMessage
    {
        get => _actionFeedbackMessage;
        set { _actionFeedbackMessage = value; OnPropertyChanged(nameof(ActionFeedbackMessage)); }
    }

    private Brush _actionFeedbackForeground = (Brush)Application.Current.FindResource("TextFillColorSecondaryBrush");
    public Brush ActionFeedbackForeground
    {
        get => _actionFeedbackForeground;
        set { _actionFeedbackForeground = value; OnPropertyChanged(nameof(ActionFeedbackForeground)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public DenuvoAuthDialog(GameInfo game, IAuthorizationService authService)
    {
        InitializeComponent();
        DataContext = this;
        Game = game;
        _authService = authService;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var (isAuth, appTicketLen, eTicketLen, steamId) = _authService.CheckAuthStatus(AppId);
        if (isAuth)
        {
            StatusTitle = "已授权";
            StatusTitleForeground = new SolidColorBrush(Color.FromRgb(0x67, 0xC5, 0x87));
            StatusBadgeBackground = new SolidColorBrush(Color.FromArgb(0x28, 0x67, 0xC5, 0x87));
            StatusIconGlyph = "\uE73E"; // Checkmark
            StatusIconForeground = new SolidColorBrush(Color.FromRgb(0x67, 0xC5, 0x87));

            var sIdText = steamId != 0 ? $" · 绑定账号: {steamId}" : "";
            StatusDetails = $"注册表中已写入有效票据（所有权票据: {appTicketLen} 字节, 加密票据: {eTicketLen} 字节{sIdText}）。OpenSteamTool 与 Steam 启动游戏时将直接通过防篡改检测。";
        }
        else
        {
            StatusTitle = "未授权";
            StatusTitleForeground = (Brush)Application.Current.FindResource("TextFillColorTertiaryBrush");
            StatusBadgeBackground = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            StatusIconGlyph = "\uE7BA"; // Warning
            StatusIconForeground = (Brush)Application.Current.FindResource("TextFillColorTertiaryBrush");
            StatusDetails = "注册表中未检测到该游戏的 Denuvo 授权票据。若您拥有该游戏正版，可直接点击“一键授权”；若已有授权文件，可直接点击“导入”或拖入窗口。";
        }
    }

    private async void LegitAuth_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        IsBusy = true;
        SetFeedback("正在连接 Steam 客户端并提取正版授权票据...", Brushes.SkyBlue);

        try
        {
            var result = await _authService.ExtractAsync(AppId);
            var importRes = _authService.ImportTicket(result.Ticket);

            if (!string.IsNullOrEmpty(importRes.Warning) && importRes.AppTicketBytes == 0)
            {
                SetFeedback($"❌ 写入授权失败：{importRes.Warning}", Brushes.Salmon);
            }
            else
            {
                var warnTip = !string.IsNullOrEmpty(importRes.Warning) ? $"（{importRes.Warning}）" : "";
                SetFeedback($"🎉 D加密授权成功！换号重启 Steam 即可直接启动{warnTip}", new SolidColorBrush(Color.FromRgb(0x67, 0xC5, 0x87)));
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            SetFeedback($"❌ 授权失败：{ex.Message}", Brushes.Salmon);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = $"选择《{GameName}》的授权文件",
            Filter = "授权文件 (*.txt;*.cw;*.shiki;*.json)|*.txt;*.cw;*.shiki;*.json|所有文件 (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ImportFileCore(dialog.FileName);
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files != null && files.Length > 0 && File.Exists(files[0]))
        {
            ImportFileCore(files[0]);
        }
    }

    private void ImportFileCore(string filePath)
    {
        try
        {
            var parsed = _authService.ParseAuthFile(filePath, AppId);
            if (!parsed.Ok)
            {
                SetFeedback($"❌ 导入失败：{parsed.Error}", Brushes.Salmon);
                return;
            }

            var ticket = new TicketData(parsed.AppId, parsed.SteamId, parsed.AppTicket!, parsed.ETicket!, filePath);
            var importRes = _authService.ImportTicket(ticket);

            if (!string.IsNullOrEmpty(importRes.Warning) && importRes.AppTicketBytes == 0)
            {
                SetFeedback($"❌ 写入失败：{importRes.Warning}", Brushes.Salmon);
            }
            else
            {
                var warnTip = !string.IsNullOrEmpty(importRes.Warning) ? $"（{importRes.Warning}）" : "";
                SetFeedback($"🎉 授权文件导入成功！后续直接从 Steam 正常启动该游戏即可{warnTip}", new SolidColorBrush(Color.FromRgb(0x67, 0xC5, 0x87)));
            }
            RefreshStatus();
        }
        catch (Exception ex)
        {
            SetFeedback($"❌ 导入异常：{ex.Message}", Brushes.Salmon);
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _authService.GetDefaultTicketsPath(AppId);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                SetFeedback($"已打开备份目录：{dir}", Brushes.LightGray);
            }
        }
        catch (Exception ex)
        {
            SetFeedback($"打开目录失败：{ex.Message}", Brushes.Salmon);
        }
    }

    private void ClearAuth_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var (ok, error) = _authService.ClearTickets(AppId);
        if (ok)
        {
            SetFeedback("已清除该游戏的本地注册表授权票据", Brushes.LightGray);
        }
        else
        {
            SetFeedback($"清除失败：{error}", Brushes.Salmon);
        }
        RefreshStatus();
    }

    private void SetFeedback(string msg, Brush foreground)
    {
        ActionFeedbackMessage = msg;
        ActionFeedbackForeground = foreground;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}