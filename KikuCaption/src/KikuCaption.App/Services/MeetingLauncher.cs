using System.Windows;
using KikuCaption.App.ViewModels;
using KikuCaption.App.ViewModels.Pages;
using KikuCaption.App.Views;

namespace KikuCaption.App.Services;

/// <summary>
/// The single "start a meeting from the dialog" entry point (UI-R5B). Both the Home page button and
/// the tray "Start session" menu call this, so the start flow — build the draft from the live capture
/// target + audio inputs, show <see cref="StartMeetingDialog"/>, apply on a valid confirm, persist,
/// and invoke the EXISTING <c>StartCommand</c> — exists in exactly one place (no duplicated start
/// logic). It reuses <see cref="HomePageViewModel"/>'s device/meter/persist helpers unchanged.
/// </summary>
public interface IMeetingLauncher
{
    Task StartFromDialogAsync();
}

/// <inheritdoc />
public sealed class MeetingLauncher : IMeetingLauncher
{
    private readonly Func<HomePageViewModel> _home;

    // Resolved lazily so the launcher and the home page can reference each other without a DI cycle.
    public MeetingLauncher(Func<HomePageViewModel> home) => _home = home;

    public async Task StartFromDialogAsync()
    {
        var vm = _home();
        var realtime = vm.Realtime;
        if (realtime.IsRunning)
        {
            return; // a meeting is already running — the dialog would be meaningless
        }

        var owner = Application.Current?.MainWindow;
        var draft = new StartMeetingDialogViewModel(
            realtime.CaptureTarget, realtime.Windows, vm.OutputRootSummary,
            realtime.AudioOptions, vm.GetMicDevices(), vm.GetMicDevices);
        var dialog = new StartMeetingDialog(draft, vm.CreateLevelMeter()) { Owner = owner };

        var result = dialog.ShowDialog();
        if (MeetingStartCoordinator.ResolveStart(result, draft, realtime))
        {
            vm.PersistCaptureTarget(realtime.CaptureTarget);
            vm.PersistAudioOptions(realtime.AudioOptions);
            if (realtime.StartCommand.CanExecute(null))
            {
                await realtime.StartCommand.ExecuteAsync(null);
            }
        }
    }
}
