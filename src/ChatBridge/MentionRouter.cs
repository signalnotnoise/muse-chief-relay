namespace ChatBridge;

internal enum RouteKind
{
    Delivered,
    Duplicate,
    NoMention,
    OwnEcho,
    ReplySuppressed,
    HopLimited,
    SelfOnly,
    /// <summary>
    /// Durable, but not in the recipient inbox.jsonl yet. A recipient lock timeout is on that
    /// agent's deferred.jsonl. A sender lock timeout during fan-out is on ingress.jsonl.
    /// </summary>
    Deferred,

    /// <summary>Fan-out parent or root does not match a chain in the sender inbox. Not delivered.</summary>
    Unresolved
}

internal sealed record RouteResult(RouteKind Kind, IReadOnlyList<InboxEvent> Delivered)
{
    public static RouteResult Of(RouteKind kind, IReadOnlyList<InboxEvent>? delivered = null) =>
        new(kind, delivered ?? Array.Empty<InboxEvent>());
}

/// <summary>
/// Routes explicit mentions into per-agent inboxes. One delivery per tagged agent.
/// Agent senders do not fan out again unless the line explicitly requests it, and that
/// extra hop is capped by <c>mentions.max_fanout_hop</c> inside one causal chain.
/// A fan-out is journaled to ingress.jsonl before that chain is read from the sender inbox.
/// </summary>
internal sealed class MentionRouter : IMentionSink
{
    private readonly AgentDirectory _directory;
    private readonly string _bridgeNick;
    private readonly int _maxHop;
    private readonly string _root;
    private readonly ConversationStore _conversations;
    private readonly IngressJournal _ingress;
    private readonly Dictionary<string, AgentInbox> _inboxes = new(StringComparer.OrdinalIgnoreCase);

    private MentionRouter(RelayConfig cfg, TimeSpan? lockBudget)
    {
        _directory = cfg.Roster;
        _bridgeNick = cfg.Nick;
        _maxHop = cfg.MentionRouting.MaxFanoutHop;
        _root = Path.Combine(cfg.BaseDir, "agents");
        _conversations = new ConversationStore(_root);
        _ingress = new IngressJournal(Path.Combine(_root, IngressJournal.FileName), lockBudget ?? AgentInbox.DefaultLockBudget);
        Directory.CreateDirectory(_root);
        foreach (var agent in _directory.Agents)
        {
            var inbox = AgentInbox.Open(Path.Combine(_root, agent.Id), lockBudget);
            inbox.OnFiled = _conversations.AppendRoom;
            _inboxes[agent.Id] = inbox;
        }

        foreach (var inbox in _inboxes.Values)
            inbox.CatchUp();
        DrainIngress();
    }

    public ConversationStore Conversations => _conversations;

    public static MentionRouter Open(RelayConfig cfg, TimeSpan? lockBudget = null) => new(cfg, lockBudget);

    public AgentInbox InboxFor(string agentId)
    {
        if (!_inboxes.TryGetValue(agentId, out var inbox))
            throw new ArgumentException($"no inbox for agent '{agentId}'");
        return inbox;
    }

    public void Accept(RoomMessage message) => Route(message);

    public RouteResult Route(RoomMessage message)
    {
        DrainIngress();
        return RouteFresh(message);
    }

    private void DrainIngress()
    {
        foreach (var retained in _ingress.Pending())
        {
            var result = RouteFresh(retained.Message);
            if (result.Kind is RouteKind.OwnEcho or RouteKind.NoMention or RouteKind.ReplySuppressed)
                _ingress.Forget(retained.SourceId);
            else if (result.Kind == RouteKind.Deferred && _ingress.Pending().Any(p => p.SourceId == retained.SourceId))
                break;
        }
    }

    private RouteResult RouteFresh(RoomMessage message)
    {
        if (!string.IsNullOrEmpty(_bridgeNick)
            && string.Equals(message.From, _bridgeNick, StringComparison.OrdinalIgnoreCase))
            return RouteResult.Of(RouteKind.OwnEcho);

        var parsed = MentionParse.Parse(message.Text, _directory);
        if (parsed.Agents.Count == 0)
            return RouteResult.Of(RouteKind.NoMention);

        var sender = _directory.FindByNick(message.From);
        if (sender is not null && !parsed.ExplicitFanout)
            return RouteResult.Of(RouteKind.ReplySuppressed);

        var sourceId = MessageIds.Source(message);
        string? parentSource = null;
        var root = sourceId;
        var hop = 0;
        var journaled = false;
        // A human mention starts a chain. An agent fan-out continues one chain: the named parent
        // or root, otherwise the latest delivery to that sender. The line is durable before that
        // read, so a busy sender lock cannot drop it. A named parent or root that does not match
        // this sender's inbox does not start a new root. A hop of 0 does not clear the hop already
        // stored for that chain, and a different chain does not inherit it.
        if (sender is not null)
        {
            _ingress.Retain(message, sourceId);
            journaled = true;
            try
            {
                var carry = TryCarry(sender.Id, parsed, sourceId, out parentSource, out root, out hop);
                if (carry != CarryDecision.Ready)
                {
                    _ingress.Forget(sourceId);
                    journaled = false;
                    return RouteResult.Of(carry == CarryDecision.HopLimited ? RouteKind.HopLimited : RouteKind.Unresolved);
                }
            }
            catch (Exception ex) when (AgentInbox.IsLockBusy(ex))
            {
                return RouteResult.Of(RouteKind.Deferred);
            }
        }

        var delivered = new List<InboxEvent>();
        var duplicates = 0;
        var deferred = 0;
        foreach (var agent in parsed.Agents)
        {
            if (agent.OwnsNick(message.From) || (sender is not null && string.Equals(sender.Id, agent.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            var draft = new InboxEvent
            {
                Id = MessageIds.Delivery(agent.Id, sourceId),
                Agent = agent.Id,
                SourceId = sourceId,
                Room = message.Room,
                From = message.From,
                Trip = message.Trip,
                Text = message.Text,
                Mentions = parsed.Agents.Select(a => a.Id).ToList(),
                Scope = InboxContract.RoomScope,
                Hop = hop,
                Fanout = parsed.ExplicitFanout,
                Parent = parentSource,
                Root = root,
                Ts = message.Ts
            };
            if (!_inboxes.TryGetValue(agent.Id, out var inbox))
                continue;
            var write = inbox.Write(draft);
            switch (write.Kind)
            {
                case InboxWriteKind.Stored:
                    delivered.Add(write.Event);
                    break;
                case InboxWriteKind.Deferred:
                    deferred++;
                    break;
                default:
                    duplicates++;
                    break;
            }
        }

        // Recipient deferrals are already on that agent's deferred.jsonl. The ingress row's job was
        // the gap before the sender inbox could be read.
        if (journaled)
            _ingress.Forget(sourceId);

        if (delivered.Count > 0)
            return RouteResult.Of(RouteKind.Delivered, delivered);
        if (deferred > 0)
            return RouteResult.Of(RouteKind.Deferred);
        if (duplicates > 0)
            return RouteResult.Of(RouteKind.Duplicate);
        return RouteResult.Of(RouteKind.SelfOnly);
    }

    private enum CarryDecision
    {
        Ready,
        HopLimited,
        Unresolved
    }

    private CarryDecision TryCarry(string senderId, MentionParseResult parsed, string sourceId, out string? parentSource, out string root, out int hop)
    {
        parentSource = null;
        root = sourceId;
        hop = 0;
        if (!_inboxes.TryGetValue(senderId, out var inbox))
        {
            if (!string.IsNullOrEmpty(parsed.Parent) || !string.IsNullOrEmpty(parsed.Root))
                return CarryDecision.Unresolved;
            if (parsed.Hop >= _maxHop)
                return CarryDecision.HopLimited;
            hop = parsed.Hop + 1;
            return CarryDecision.Ready;
        }

        var look = inbox.InspectChain(parsed.Parent, parsed.Root);
        if (look.Unresolved)
            return CarryDecision.Unresolved;

        if (look.Parent is not null)
            parentSource = look.Parent.SourceId;
        if (!string.IsNullOrEmpty(look.Root))
            root = look.Root;

        var carried = Math.Max(parsed.Hop, look.HighestHop);
        if (carried >= _maxHop)
            return CarryDecision.HopLimited;
        hop = carried + 1;
        return CarryDecision.Ready;
    }
}

internal interface IMentionSink
{
    void Accept(RoomMessage message);
}

/// <summary>
/// Socket-side entry. A failure here is logged and swallowed so mention routing cannot drop the relay.
/// A busy inbox lock is retried. A recipient that stays busy is spilled to deferred.jsonl. A sender
/// lock that stays busy during fan-out leaves the line on ingress.jsonl. Either way this returns true.
/// </summary>
internal static class MentionIngress
{
    public static bool TryAccept(IMentionSink sink, RoomMessage message, out string? error)
    {
        try
        {
            sink.Accept(message);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The inbox already retried the lock and, if it stayed busy, spilled the mention.
            // Reaching here means that spill did not land, or some other failure did.
            error = ex is IOException or UnauthorizedAccessException
                ? "not filed: " + ex.GetType().Name
                : ex.GetType().Name;
            return false;
        }
    }
}
