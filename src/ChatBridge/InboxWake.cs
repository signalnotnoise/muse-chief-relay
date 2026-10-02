namespace ChatBridge;

/// <summary>
/// Delivers due inbox events to an adapter in per-agent seq order. An adapter exception is a soft
/// failure: the event stays pending, backoff is recorded, and later events wait behind it.
/// </summary>
internal sealed class InboxWakePump
{
    private readonly AgentInbox _inbox;

    public InboxWakePump(AgentInbox inbox) => _inbox = inbox;

    public async Task<WakePumpResult> PumpAsync(
        IAgentWakeAdapter adapter,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var acked = 0;
        foreach (var view in _inbox.Due(now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = InboxWakeRequest.Create(view);
            if (!string.Equals(request.Scope, InboxContract.RoomScope, StringComparison.Ordinal))
                throw new InvalidOperationException("wake requests are room-scoped");
            try
            {
                await adapter.WakeAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _inbox.RecordFailure(view.Event.Id, ex.GetType().Name, now);
                return new WakePumpResult(acked, view.Event.Id, ex.GetType().Name);
            }

            _inbox.Ack(view.Event.Id, now);
            acked++;
        }

        return new WakePumpResult(acked, null, null);
    }
}
