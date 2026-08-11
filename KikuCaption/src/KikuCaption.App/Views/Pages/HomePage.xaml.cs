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

        var realtime = ViewModel.Realtime;
        var draft = new StartMeetingDialogViewModel(realtime.CaptureTarget, realtime.Windows, ViewModel.OutputRootSummary);
        var dialog = new StartMeetingDialog(draft) { Owner = Window.GetWindow(this) };

        var result = dialog.ShowDialog();
        if (MeetingStartCoordinator.ResolveStart(result, draft, realtime) && realtime.StartCommand.CanExecute(null))
        {
            await realtime.StartCommand.ExecuteAsync(null);
        }
    }

    private void TimelineMenuItem_Click(object sender, RoutedEventArgs e) => TimelineMenuToggle.IsChecked = false;
}
