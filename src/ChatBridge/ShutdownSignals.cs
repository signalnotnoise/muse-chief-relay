using System.Runtime.InteropServices;

namespace ChatBridge;

/// <summary>
/// SIGTERM / SIGINT → cancel <paramref name="cts"/> instead of killing the process, so the caller can finish
/// cleanly (final state write, offset save). Only the first signal is intercepted; a second one gets the
/// runtime's default handling, which ends the process if a shutdown ever hangs.
/// </summary>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = new();
    private int _count;

    public PosixSignal? Received { get; private set; }

    public ShutdownSignals(CancellationTokenSource cts, Action<PosixSignal>? onSignal)
    {
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(sig, ctx =>
                {
                    if (Interlocked.Increment(ref _count) > 1)
                        return;
                    ctx.Cancel = true;
                    Received = ctx.Signal;
                    onSignal?.Invoke(ctx.Signal);
                    cts.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every signal exists on every OS; the others still work.
            }
        }
    }

    public void Dispose()
    {
        foreach (var r in _registrations)
            r.Dispose();
    }
}
