using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KikuCaption.App.Navigation;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.ViewModels;

/// <summary>
/// Main-window shell view model (UI-R1). Owns the top function bar (navigation + environment status
/// cluster) and hosts the current page through <see cref="Navigation"/>. It exposes the environment
/// page as the single source of the health indicator, and the home page for the window's
/// close-while-recording guard. It never contains page/feature logic itself.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(
        INavigationService navigation,
        HomePageViewModel home,
        EnvironmentPageViewModel environment)
    {
        Navigation = navigation;
        Home = home;
        Environment = environment;
    }

    public INavigationService Navigation { get; }

    /// <summary>Home page — exposed so the window can safely stop a running session on close.</summary>
    public HomePageViewModel Home { get; }

    /// <summary>Environment page — also drives the top-bar status dot/text/tooltip.</summary>
    public EnvironmentPageViewModel Environment { get; }

    /// <summary>
    /// Startup: show Home immediately, then run the environment check and crash recovery off the UI
    /// thread. The check runs asynchronously so the window never blocks (UI-R1 §5, §12.1).
    /// </summary>
    public async Task InitializeAsync()
    {
        Navigation.Navigate(PageKey.Home);

        var check = Environment.CheckCommand.ExecuteAsync(null);
        var recover = Home.RunRecoveryAsync();
        await Task.WhenAll(check, recover).ConfigureAwait(true);
    }

    [RelayCommand]
    private void GoHome() => Navigation.Navigate(PageKey.Home);

    [RelayCommand]
    private void GoEnvironment() => Navigation.Navigate(PageKey.Environment);

    [RelayCommand]
    private void GoAudio() => Navigation.Navigate(PageKey.Audio);

    [RelayCommand]
    private void GoDictionary() => Navigation.Navigate(PageKey.Dictionary);

    [RelayCommand]
    private void GoSettings() => Navigation.Navigate(PageKey.Settings);

    /// <summary>Re-runs the environment check asynchronously (top-bar "重新检查" menu item).</summary>
    [RelayCommand]
    private async Task RecheckEnvironmentAsync()
    {
        if (Environment.CheckCommand.CanExecute(null))
        {
            await Environment.CheckCommand.ExecuteAsync(null);
        }
    }
}
