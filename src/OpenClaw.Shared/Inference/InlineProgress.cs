using System;

namespace OpenClaw.Shared.Inference;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its handler synchronously on the
/// reporting thread.
///
/// <para><see cref="Progress{T}"/> posts each report to the captured
/// synchronization context, or to the thread pool when there is none, so two
/// reports can be delivered out of order. The download managers translate
/// per-file byte counts into a running aggregate, and an out-of-order delivery
/// there makes the aggregate jump backwards: a progress bar that visibly
/// rewinds, and in the worst case a "downloaded" figure that briefly exceeds or
/// undershoots reality. Forwarding inline keeps the sequence monotonic.</para>
///
/// <para>The handler runs on whichever thread reported, so callers that touch UI
/// state are still responsible for marshalling.</para>
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly Action<T> _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public void Report(T value) => _handler(value);
}
