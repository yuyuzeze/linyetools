using System.Windows;
using System.Windows.Controls;
using KikuCaption.App.ViewModels;
using KikuCaption.App.ViewModels.Pages;

namespace KikuCaption.App.Views.Pages;

/// <summary>
/// Home page view. Code-behind is limited to unavoidable view behaviours: opening the modal
/// start-meeting dialog and closing the timeline overflow menu. All logic lives in the view models.
/// </summary>
public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private HomePageViewModel? ViewModel => DataContext as HomePageViewModel;

    // Opens the start-meeting dialog with an independent draft seeded from the live target. The draft
    // is applied to the meeting view model only on a valid confirm (via MeetingStartCoordinator);
    // cancel / Esc / close leave the live target untouched (UI-R2 dialog-draft fix).
    private async void StartMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.Realtime.IsRunning)
        {
            return;
        }

        // UI-R5B: the start-meeting flow lives in the shared IMeetingLauncher (reused by the tray), so
        // the home button and the tray "Start session" behave identically. Same dialog, same confirm,
        // same persistence, same StartCommand — no duplicated start logic.
        await ViewModel.StartMeetingAsync();
    }

    private void TimelineMenuItem_Click(object sender, RoutedEventArgs e) => TimelineMenuToggle.IsChecked = false;

    // UI-R5C: open the generate-summary dialog for the current session (built from an immutable snapshot).
    private void GenerateSummary_Click(object sender, RoutedEventArgs e)
    {
        TimelineMenuToggle.IsChecked = false;
        var vm = ViewModel?.CreateSummaryDialogVm();
        if (vm is null)
        {
            return;
        }

        new MeetingSummaryDialog(vm) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OpenSummary_Click(object sender, RoutedEventArgs e)
    {
        TimelineMenuToggle.IsChecked = false;
        ViewModel?.OpenSummaryFile();
    }

    private void ShowSummaryFolder_Click(object sender, RoutedEventArgs e)
    {
        TimelineMenuToggle.IsChecked = false;
        ViewModel?.ShowSummaryFolder();
    }
}
