using System.Windows.Controls;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class NewGamesView : UserControl
{
    public NewGamesView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is not NewGamesViewModel vm) return;
            _ = vm.LoadCommand.ExecuteAsync(null);
        };
    }
}
