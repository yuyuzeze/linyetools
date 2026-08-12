using System.ComponentModel;
using KikuCaption.App.ViewModels;
using KikuCaption.Core.Enums;

namespace KikuCaption.App.Tray;

/// <summary>
/// Production <see cref="ITraySessionSource"/>: a thin adapter over <see cref="RealtimeCaptionViewModel"/>.
/// It exposes the session state / overlay visibility and forwards stop / overlay-toggle to the view
/// model's EXISTING commands — so the tray drives the same flows as the home page (no duplication).
/// </summary>
public sealed class RealtimeTraySessionSource : ITraySessionSource
{
    private readonly RealtimeCaptionViewModel _vm;

    public event Action? Changed;

    public RealtimeTraySessionSource(RealtimeCaptionViewModel vm)
    {
        _vm = vm;
        _vm.PropertyChanged += OnVmChanged;
        _vm.Overlay.PropertyChanged += OnOverlayChanged;
    }

    public bool IsRunning => _vm.IsRunning;
    public SessionState State => _vm.CurrentSessionState;
    public bool OverlayVisible => _vm.Overlay.IsVisible;
    public bool CanStop => _vm.StopCommand.CanExecute(null);

    public Task StopAsync()
        => _vm.StopCommand.CanExecute(null) ? _vm.StopCommand.ExecuteAsync(null) : Task.CompletedTask;

    public void ToggleOverlay() => _vm.ToggleOverlayCommand.Execute(null);

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RealtimeCaptionViewModel.IsRunning)
            or nameof(RealtimeCaptionViewModel.SessionStateText))
        {
            Changed?.Invoke();
        }
    }

    private void OnOverlayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubtitleOverlayViewModel.IsVisible))
        {
            Changed?.Invoke();
        }
    }
}
