using System.Windows;
using System.Windows.Controls;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.Views.Pages;

/// <summary>
/// Settings page view. Code-behind is limited to view concerns: the PasswordBox → DPAPI bridge
/// (the key is never bound, echoed, or logged) and the folder-browse dialog.
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private SettingsPageViewModel? ViewModel => DataContext as SettingsPageViewModel;

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.Translation.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择会话输出目录" };
        var current = ViewModel.General.OutputDirectory;
        if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            ViewModel.General.SetOutputDirectory(dialog.FolderName);
        }
    }
}
