using System.IO;
using System.Windows;
using KikuCaption.App.Localization;
using KikuCaption.App.Views;
using Microsoft.Extensions.Logging;

namespace KikuCaption.App.Playback;

/// <summary>Owns the single playback window and prevents duplicate players/audio.</summary>
public sealed class MeetingPlaybackWindowManager
{
    private readonly MeetingPlaybackCoordinator _coordinator;
    private readonly LocalizationService _localization;
    private readonly ILogger<MeetingPlaybackWindowManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MeetingPlaybackWindow? _window;
    private Guid? _sessionId;

    public MeetingPlaybackWindowManager(MeetingPlaybackCoordinator coordinator,
        LocalizationService localization, ILogger<MeetingPlaybackWindowManager> logger)
    {
        _coordinator = coordinator;
        _localization = localization;
        _logger = logger;
    }

    /// <returns>Null on success; otherwise a localized, non-sensitive error message.</returns>
    public async Task<string?> OpenAsync(Guid sessionId, Window? owner, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_window is { IsLoaded: true } && _sessionId == sessionId)
            {
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _window.Activate();
                return null;
            }

            if (_window is { IsLoaded: true }) _window.Close();
            _window = null;
            _sessionId = null;

            var session = await _coordinator.LoadAsync(sessionId, cancellationToken).ConfigureAwait(true);
            // Keep playback independent from the main window. If the main window is
            // minimized/hidden to the tray, an owned window would disappear while
            // LibVLC continued playing audio in the background.
            var window = new MeetingPlaybackWindow(session);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window)) { _window = null; _sessionId = null; }
            };
            _window = window;
            _sessionId = sessionId;
            window.Show();
            return null;
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Playback recording is missing for session {SessionId}.", sessionId);
            return _localization["Playback.FileMissing"];
        }
        catch (PlaybackEngineUnavailableException ex)
        {
            _logger.LogWarning("Bundled playback engine is unavailable ({ErrorType}).", ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return _localization["Playback.EngineMissing"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Opening playback failed for session {SessionId} ({ErrorType}).", sessionId, ex.GetType().Name);
            return _localization["Playback.OpenFailed"];
        }
        finally { _gate.Release(); }
    }
}
