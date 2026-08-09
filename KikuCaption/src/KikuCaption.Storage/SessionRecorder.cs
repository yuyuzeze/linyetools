using System.Threading.Channels;
using KikuCaption.Core.Enums;
using KikuCaption.Core.Exceptions;
using KikuCaption.Core.Interfaces;
using KikuCaption.Core.Models;
using KikuCaption.Storage.Sqlite;
using Microsoft.Extensions.Logging;

namespace KikuCaption.Storage;

public sealed class StorageFailedEventArgs : EventArgs
{
    public required string Message { get; init; }
}

/// <summary>
/// Real-time persistence pipeline (PROJECT.md 4, 6): final segments → bounded queue → SQLite
/// (immediate commit) → debounced file re-export. Never silently drops finals (back-pressure via
/// the bounded queue); write failures and low disk are surfaced, not hidden. Not coupled to WPF.
/// </summary>
public sealed class SessionRecorder : IAsyncDisposable
{
    private readonly ITranscriptStore _store;
    private readonly ITranscriptExporter _exporter;
    private readonly StorageOptions _options;
    private readonly ILogger<SessionRecorder> _logger;
    private readonly object _seenGate = new();
    private readonly HashSet<Guid> _seen = new();

    private Channel<TranscriptSegment>? _queue;
    private Task? _writerTask;
    private Task? _exportTask;
    private CancellationTokenSource? _cts;
    private string _outputRoot = string.Empty;

    private long _savedFinalCount;
    private volatile bool _acceptingInput;
    private volatile bool _diskFull;
    private volatile int _dirty;
    private DateTime _lastWriteUtc;

    public SessionRecorder(
        ITranscriptStore store,
        ITranscriptExporter exporter,
        StorageOptions options,
        ILogger<SessionRecorder> logger)
    {
        _store = store;
        _exporter = exporter;
        _options = options;
        _logger = logger;
    }

    public event EventHandler? SavedFinal;
    public event EventHandler<StorageFailedEventArgs>? StorageFailed;
    public event EventHandler? DiskLow;

    public bool IsRunning { get; private set; }
    public Guid SessionId { get; private set; }
    public string OutputDirectory { get; private set; } = string.Empty;
    public long SavedFinalCount => Interlocked.Read(ref _savedFinalCount);
    public DateTimeOffset? LastSavedAt { get; private set; }
    public string? StorageError { get; private set; }

    public async Task StartSessionAsync(MeetingSession session, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("已有活动会话在保存中。");
        }

        _outputRoot = _options.ResolveOutputRoot();
        Directory.CreateDirectory(_outputRoot);

        // Refuse to start if free disk is below the configured minimum.
        if (!DiskSpace.HasAtLeastGb(_outputRoot, _options.MinimumFreeSpaceGb))
        {
            throw new StorageException("insufficient_disk",
                $"磁盘可用空间不足（需 ≥ {_options.MinimumFreeSpaceGb} GB，当前 {DiskSpace.GetFreeGb(_outputRoot):0.0} GB）。");
        }

        SessionPaths.EnsureWithinRoot(_outputRoot, session.OutputDirectory);
        Directory.CreateDirectory(session.OutputDirectory);

        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _store.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);

        SessionId = session.Id;
        OutputDirectory = session.OutputDirectory;
        StorageError = null;
        _diskFull = false;
        _acceptingInput = true;
        Interlocked.Exchange(ref _savedFinalCount, 0);
        lock (_seenGate) { _seen.Clear(); }

        // Ensure files (incl. session.json) exist immediately.
        await _exporter.ExportAsync(SessionId, OutputDirectory, cancellationToken).ConfigureAwait(false);

        _queue = Channel.CreateBounded<TranscriptSegment>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
        _exportTask = Task.Run(() => ExportLoopAsync(_cts.Token));
    }

    /// <summary>Records the session's MP4 path (Milestone 5) and refreshes session.json.</summary>
    public async Task SetRecordingPathAsync(string recordingPath, CancellationToken cancellationToken = default)
    {
        await _store.SetRecordingPathAsync(SessionId, recordingPath, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _dirty, 1);
    }

    public async Task RecordFinalAsync(TranscriptSegment segment, CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _queue is null)
        {
            throw new InvalidOperationException("没有活动的保存会话。");
        }

        if (segment.Status != TranscriptStatus.Final)
        {
            return; // partial is never persisted
        }

        if (!_acceptingInput)
        {
            throw new StorageException("not_accepting", "存储已停止接收字幕（磁盘不足或写入错误）。");
        }

        lock (_seenGate)
        {
            if (!_seen.Add(segment.Id))
            {
                return; // duplicate final — dedup
            }
        }

        await _queue.Writer.WriteAsync(segment, cancellationToken).ConfigureAwait(false); // bounded back-pressure
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var segment in _queue!.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _store.UpsertSegmentAsync(segment, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _savedFinalCount);
                    LastSavedAt = DateTimeOffset.Now;
                    _lastWriteUtc = DateTime.UtcNow;
                    Interlocked.Exchange(ref _dirty, 1);
                    SavedFinal?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    // Never fake success: stop accepting and surface the failure.
                    StorageError = ex.Message;
                    _acceptingInput = false;
                    _logger.LogError(ex, "Failed to persist final segment {SegmentId} (session {SessionId}).",
                        segment.Id, SessionId);
                    StorageFailed?.Invoke(this, new StorageFailedEventArgs { Message = ex.Message });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task ExportLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                // Low-disk guard while running.
                if (_acceptingInput && !DiskSpace.HasAtLeastGb(_outputRoot, _options.MinimumFreeSpaceGb))
                {
                    _diskFull = true;
                    _acceptingInput = false;
                    StorageError = "磁盘空间不足，已安全停止接收新字幕。";
                    _logger.LogWarning("Low disk space; stopping intake for session {SessionId}.", SessionId);
                    try { await _store.SetSessionStateAsync(SessionId, SessionStates.StoppedDiskFull, null, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark disk-full state."); }
                    DiskLow?.Invoke(this, EventArgs.Empty);
                }

                if (Interlocked.Exchange(ref _dirty, 0) == 1 &&
                    (DateTime.UtcNow - _lastWriteUtc).TotalMilliseconds >= _options.ExportDebounceMs)
                {
                    await SafeExportAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task SafeExportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _exporter.ExportAsync(SessionId, OutputDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StorageError = ex.Message;
            _logger.LogError(ex, "Failed to export files for session {SessionId}.", SessionId);
            StorageFailed?.Invoke(this, new StorageFailedEventArgs { Message = ex.Message });
        }
    }

    public async Task StopSessionAsync(DateTimeOffset endedAt)
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _queue?.Writer.TryComplete();

        if (_writerTask is not null)
        {
            try { await _writerTask.ConfigureAwait(false); } catch { /* observed */ }
        }

        // Mark the session ended, then export the final, complete set of files.
        try
        {
            if (_diskFull)
            {
                await _store.SetSessionStateAsync(SessionId, SessionStates.StoppedDiskFull, endedAt, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _store.CompleteSessionAsync(SessionId, endedAt, CancellationToken.None).ConfigureAwait(false);
            }

            await _exporter.ExportAsync(SessionId, OutputDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StorageError = ex.Message;
            _logger.LogError(ex, "Failed to finalize session {SessionId}.", SessionId);
            StorageFailed?.Invoke(this, new StorageFailedEventArgs { Message = ex.Message });
        }

        _cts?.Cancel();
        if (_exportTask is not null)
        {
            try { await _exportTask.ConfigureAwait(false); } catch { /* observed */ }
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            await StopSessionAsync(DateTimeOffset.Now).ConfigureAwait(false);
        }
    }
}
