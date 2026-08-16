using System.Windows.Controls;

namespace SteamLuaManager.Views;

public partial class SaveView : UserControl
{
    public SaveView()
    {
        InitializeComponent();
    }

    private void WebDavPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SaveViewModel vm)
            vm.WebDavPassword = WebDavPasswordBox.Password;
    }
}
