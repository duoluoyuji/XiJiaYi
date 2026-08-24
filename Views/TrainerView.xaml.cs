using System.Windows.Controls;
using System.Windows.Input;

namespace SteamLuaManager.Views;

public partial class TrainerView : UserControl
{
    public TrainerView()
    {
        InitializeComponent();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ViewModels.TrainerViewModel vm && vm.SearchCommand.CanExecute(null))
            vm.SearchCommand.Execute(null);
    }
}