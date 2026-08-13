using System.Linq;
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
    private bool _dialogOpen;

    // Resolved lazily so the launcher and the home page can reference each other without a DI cycle.
    public MeetingLauncher(Func<HomePageViewModel> home) => _home = home;

    public async Task StartFromDialogAsync()
    {
        var vm = _home();
        var realtime = vm.Realtime;
        if (realtime.IsRunning || _dialogOpen)
        {
            return; // already running, or the dialog is already open — never stack a second dialog
        }

        var draft = new StartMeetingDialogViewModel(
            realtime.CaptureTarget, realtime.Windows, vm.OutputRootSummary,
            realtime.AudioOptions, vm.GetMicDevices(), vm.GetMicDevices);
        var dialog = new StartMeetingDialog(draft, vm.CreateLevelMeter()) { Owner = ResolveOwner() };

        bool? result;
        _dialogOpen = true;
        try
        {
            result = dialog.ShowDialog();
        }
        finally
        {
            _dialogOpen = false;
        }

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

    // The dialog owner MUST be the real main window (so it centers on it and is modal to it) — never
    // the subtitle overlay, which WPF instantiates first and auto-assigns to Application.MainWindow.
    private static Window? ResolveOwner()
    {
        var windows = Application.Current?.Windows.OfType<Window>().ToList() ?? new List<Window>();
        return windows.FirstOrDefault(w => w is MainWindow && w.IsVisible)
            ?? windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? (Application.Current?.MainWindow is { IsVisible: true } mw ? mw : null);
    }
}
