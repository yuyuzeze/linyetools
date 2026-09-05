using System.Security.Cryptography;
using System.Text;

namespace KikuCaption.App.Services.Python;

/// <summary>
/// A current-user, cross-process install lock (R7C.1). A null acquire result means the lock is held
/// elsewhere (another KikuCaption instance is installing) or the wait timed out.
/// </summary>
public interface IInstallLock
{
    /// <summary>
    /// Try to acquire the lock, waiting at most <paramref name="wait"/> (<see cref="Timeout.InfiniteTimeSpan"/>
    /// to wait indefinitely). Returns a disposable that releases on dispose, or null if not acquired.
    /// The wait honours <paramref name="cancellationToken"/> (throws <see cref="OperationCanceledException"/>).
    /// </summary>
    Task<IDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>
/// Cross-process install mutual exclusion (R7C.1): two KikuCaption instances can never build / switch the
/// managed venv at the same time. Backed by a named <see cref="Mutex"/> in the <c>Local\</c> (per-user,
/// per-session) namespace so it needs no admin rights and the OS ABANDONS (releases) it automatically if
/// the owning process dies — a crash never wedges future installs. Because a mutex is thread-affine, it
/// is acquired AND released on one dedicated background thread, which lets it compose with async install
/// code that hops threads across awaits. The name embeds a non-reversible hash of the user name (never a
/// raw SID), and nothing about the lock is logged with identity.
/// </summary>
public sealed class CrossProcessInstallLock : IInstallLock
{
    private readonly string _name;

    public CrossProcessInstallLock(string? name = null) => _name = name ?? DefaultName();

    /// <summary>The per-user default lock name (a stable, non-reversible hash of the user name — no SID).</summary>
    public static string DefaultName()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName ?? string.Empty)));
        return $@"Local\KikuCaption.PythonEnvironmentInstall.{hash[..16]}";
    }

    public Task<IDisposable?> TryAcquireAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => AcquireAndHold(wait, cancellationToken, tcs))
        {
            IsBackground = true,
            Name = "KikuPyInstallLock"
        };
        thread.Start();
        return tcs.Task;
    }

    // Runs entirely on the dedicated thread: acquire → hand the releaser to the caller → hold until it is
    // disposed → release. Keeping acquire + release on the same thread satisfies the mutex's affinity.
    private void AcquireAndHold(TimeSpan wait, CancellationToken ct, TaskCompletionSource<IDisposable?> tcs)
    {
        Mutex mutex;
        try { mutex = new Mutex(false, _name); }
        catch (Exception ex) { tcs.SetException(ex); return; }

        var release = new ManualResetEventSlim(false);
        var owned = false;
        try
        {
            int index;
            try { index = WaitHandle.WaitAny(new[] { mutex, ct.WaitHandle }, wait); }
            catch (AbandonedMutexException) { index = 0; } // previous owner died → ownership is now ours

            if (index == 0)
            {
                owned = true;
                tcs.SetResult(new Releaser(release));
                release.Wait(); // hold the mutex until the caller disposes the releaser
            }
            else if (index == 1)
            {
                tcs.TrySetCanceled(ct); // cancellation signalled during the wait
            }
            else
            {
                tcs.TrySetResult(null); // WaitHandle.WaitTimeout — lock held elsewhere
            }
        }
        catch (Exception ex)
        {
            if (!tcs.Task.IsCompleted) tcs.TrySetException(ex);
        }
        finally
        {
            if (owned) { try { mutex.ReleaseMutex(); } catch { /* best effort */ } }
            mutex.Dispose();
            release.Dispose();
        }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly ManualResetEventSlim _signal;
        private int _disposed;
        public Releaser(ManualResetEventSlim signal) => _signal = signal;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { _signal.Set(); } catch { /* thread already exited */ }
            }
        }
    }
}
