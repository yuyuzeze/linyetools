using System.Windows;

namespace KikuCaption.App.Views;

public partial class EasterEggAboutWindow : Window
{
    public EasterEggAboutWindow()
    {
        InitializeComponent();
        var version = typeof(EasterEggAboutWindow).Assembly.GetName().Version;
        VersionTextBlock.Text = version is null ? string.Empty : $"Version {version.ToString(3)}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
