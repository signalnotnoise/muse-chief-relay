namespace ChatBridge;

internal enum RouteKind
{
    Delivered,
    Duplicate,
    NoMention,
    OwnEcho,
    ReplySuppressed,
    HopLimited,
    SelfOnly
}

internal sealed record RouteResult(RouteKind Kind, IReadOnlyList<InboxEvent> Delivered)
{
    public static RouteResult Of(RouteKind kind, IReadOnlyList<InboxEvent>? delivered = null) =>
        new(kind, delivered ?? Array.Empty<InboxEvent>());
}

/// <summary>
/// Routes explicit mentions into per-agent inboxes. One delivery per tagged agent.
/// Agent senders do not fan out again unless the line explicitly requests it, and that
/// extra hop is capped by <c>mentions.max_fanout_hop</c>.
/// </summary>
internal sealed class MentionRouter : IMentionSink
{
    private readonly AgentDirectory _directory;
    private readonly string _bridgeNick;
    private readonly int _maxHop;
    private readonly string _root;
    private readonly ConversationStore _conversations;
    private readonly Dictionary<string, AgentInbox> _inboxes = new(StringComparer.OrdinalIgnoreCase);

    private MentionRouter(RelayConfig cfg)
    {
        _directory = cfg.Roster;
        _bridgeNick = cfg.Nick;
        _maxHop = cfg.MentionRouting.MaxFanoutHop;
        _root = Path.Combine(cfg.BaseDir, "agents");
        _conversations = new ConversationStore(_root);
        Directory.CreateDirectory(_root);
        foreach (var agent in _directory.Agents)
            _inboxes[agent.Id] = AgentInbox.Open(Path.Combine(_root, agent.Id));
    }

    public ConversationStore Conversations => _conversations;

    public static MentionRouter Open(RelayConfig cfg) => new(cfg);

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
        if (sender is not null && parsed.ExplicitFanout && parsed.Hop >= _maxHop)
            return RouteResult.Of(RouteKind.HopLimited);

        var sourceId = MessageIds.Source(message);
        var delivered = new List<InboxEvent>();
        var duplicates = 0;
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
                Hop = sender is null ? 0 : parsed.Hop + 1,
                Fanout = parsed.ExplicitFanout,
                Ts = message.Ts
            };
            if (!_inboxes.TryGetValue(agent.Id, out var inbox))
                continue;
            if (!inbox.TryAppend(draft, out var stored))
            {
                duplicates++;
                continue;
            }

            _conversations.AppendRoom(stored);
            delivered.Add(stored);
        }

        if (delivered.Count > 0)
            return RouteResult.Of(RouteKind.Delivered, delivered);
        if (duplicates > 0)
            return RouteResult.Of(RouteKind.Duplicate);
        return RouteResult.Of(RouteKind.SelfOnly);
    }
}

internal interface IMentionSink
{
    void Accept(RoomMessage message);
}

/// <summary>
/// Socket-side entry. A failure here is logged and swallowed so mention routing cannot drop the relay.
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
            error = ex.GetType().Name;
            return false;
        }
    }
}
