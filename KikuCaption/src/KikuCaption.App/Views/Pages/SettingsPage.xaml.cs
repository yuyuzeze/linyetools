using System.Windows;
using System.Windows.Controls;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.Views.Pages;

/// <summary>
/// Settings page view. Code-behind is limited to the PasswordBox → DPAPI bridge; the API key is
/// never bound, echoed, or logged (PROJECT.md 5.6, M6 §8).
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsPageViewModel vm)
        {
            return;
        }

        vm.Translation.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }
}
