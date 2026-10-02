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
    /// <summary>Durable on deferred.jsonl. The inbox lock timed out, so it is not in inbox.jsonl yet.</summary>
    Deferred
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
/// </summary>
internal sealed class MentionRouter : IMentionSink
{
    private readonly AgentDirectory _directory;
    private readonly string _bridgeNick;
    private readonly int _maxHop;
    private readonly string _root;
    private readonly ConversationStore _conversations;
    private readonly Dictionary<string, AgentInbox> _inboxes = new(StringComparer.OrdinalIgnoreCase);

    private MentionRouter(RelayConfig cfg, TimeSpan? lockBudget)
    {
        _directory = cfg.Roster;
        _bridgeNick = cfg.Nick;
        _maxHop = cfg.MentionRouting.MaxFanoutHop;
        _root = Path.Combine(cfg.BaseDir, "agents");
        _conversations = new ConversationStore(_root);
        Directory.CreateDirectory(_root);
        foreach (var agent in _directory.Agents)
        {
            var inbox = AgentInbox.Open(Path.Combine(_root, agent.Id), lockBudget);
            inbox.OnFiled = _conversations.AppendRoom;
            _inboxes[agent.Id] = inbox;
        }

        foreach (var inbox in _inboxes.Values)
            inbox.CatchUp();
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
        // A human mention starts a chain. An agent fan-out continues one chain: the named parent
        // or root, otherwise the latest delivery to that sender. A hop of 0 does not clear the
        // hop already stored for that chain, and a different chain does not inherit it.
        if (sender is not null && !TryCarry(sender.Id, parsed, sourceId, out parentSource, out root, out hop))
            return RouteResult.Of(RouteKind.HopLimited);

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

        if (delivered.Count > 0)
            return RouteResult.Of(RouteKind.Delivered, delivered);
        if (deferred > 0)
            return RouteResult.Of(RouteKind.Deferred);
        if (duplicates > 0)
            return RouteResult.Of(RouteKind.Duplicate);
        return RouteResult.Of(RouteKind.SelfOnly);
    }

    private bool TryCarry(string senderId, MentionParseResult parsed, string sourceId, out string? parentSource, out string root, out int hop)
    {
        parentSource = null;
        root = sourceId;
        hop = 0;
        if (!_inboxes.TryGetValue(senderId, out var inbox))
        {
            if (parsed.Hop >= _maxHop)
                return false;
            hop = parsed.Hop + 1;
            return true;
        }

        InboxEvent? parent = null;
        if (!string.IsNullOrEmpty(parsed.Parent))
            parent = inbox.Find(parsed.Parent);
        else if (string.IsNullOrEmpty(parsed.Root))
            parent = inbox.Latest();

        if (parent is not null)
        {
            parentSource = parent.SourceId;
            root = AgentInbox.ChainRoot(parent);
        }
        else if (!string.IsNullOrEmpty(parsed.Root))
        {
            root = parsed.Root;
        }

        var carried = Math.Max(parsed.Hop, inbox.HighestFanoutHop(root));
        if (carried >= _maxHop)
            return false;
        hop = carried + 1;
        return true;
    }
}

internal interface IMentionSink
{
    void Accept(RoomMessage message);
}

/// <summary>
/// Socket-side entry. A failure here is logged and swallowed so mention routing cannot drop the relay.
/// A busy inbox lock is retried, then the mention is spilled to deferred.jsonl and this returns true.
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
