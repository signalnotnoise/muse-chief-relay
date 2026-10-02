using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatBridge;

/// <summary>One history line. Room and side files are separate; this type records which scope it belongs to.</summary>
internal sealed class ConversationLine
{
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("agent")] public string Agent { get; set; } = "";
    [JsonPropertyName("peer")] public string? Peer { get; set; }
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("ts")] public long? Ts { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
}

/// <summary>
/// Per-agent histories. The same <c>agent</c> id may appear in the room and in a side chat.
/// Room reads never open the side directory. Nothing in this type copies a side line into the room.
/// </summary>
internal sealed class ConversationStore
{
    private readonly string _root;

    public ConversationStore(string agentsRoot) => _root = agentsRoot;

    public void AppendRoom(InboxEvent ev)
    {
        InboxContract.RefuseSideCopy(InboxContract.RoomScope);
        if (!string.Equals(ev.Scope, InboxContract.RoomScope, StringComparison.Ordinal))
            throw new InvalidOperationException("room history accepts scope room only");
        var dir = AgentDir(ev.Agent);
        Directory.CreateDirectory(dir);
        var line = new ConversationLine
        {
            Scope = InboxContract.RoomScope,
            Agent = ev.Agent,
            Author = ev.From,
            Text = ev.Text,
            Ts = ev.Ts,
            SourceId = ev.SourceId
        };
        File.AppendAllText(Path.Combine(dir, "room.jsonl"), JsonSerializer.Serialize(line, JsonUtil.Opts) + "\n");
    }

    public void AppendSide(string agentId, string peerId, string author, string text, long? ts)
    {
        var agent = RequireId(agentId);
        var peer = RequireId(peerId);
        var dir = Path.Combine(AgentDir(agent), "side");
        Directory.CreateDirectory(dir);
        var line = new ConversationLine
        {
            Scope = InboxContract.SideScope,
            Agent = agent,
            Peer = peer,
            Author = author,
            Text = text,
            Ts = ts
        };
        File.AppendAllText(Path.Combine(dir, peer + ".jsonl"), JsonSerializer.Serialize(line, JsonUtil.Opts) + "\n");
    }

    public IReadOnlyList<ConversationLine> Read(string agentId, string scope, string? peerId)
    {
        if (scope == InboxContract.RoomScope)
        {
            if (peerId is not null)
                throw new ArgumentException("room history has no peer");
            return ReadFile(Path.Combine(AgentDir(agentId), "room.jsonl"));
        }

        if (scope != InboxContract.SideScope)
            throw new ArgumentException("unknown conversation scope");
        if (string.IsNullOrEmpty(peerId))
            throw new ArgumentException("side history needs a peer");
        return ReadFile(Path.Combine(AgentDir(agentId), "side", RequireId(peerId) + ".jsonl"));
    }

    /// <summary>
    /// A room reply is the text the caller supplies. This method does not read side files.
    /// </summary>
    public string ComposeRoomReply(string roomText)
    {
        InboxContract.RefuseSideCopy(InboxContract.RoomScope);
        return roomText ?? throw new ArgumentNullException(nameof(roomText));
    }

    private string AgentDir(string agentId) => Path.Combine(_root, RequireId(agentId));

    private static string RequireId(string id)
    {
        if (!AgentPath.IsSafeId(id))
            throw new ArgumentException("agent and peer ids must be single path segments");
        return id;
    }

    private static List<ConversationLine> ReadFile(string path)
    {
        var list = new List<ConversationLine>();
        if (!File.Exists(path))
            return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var item = JsonSerializer.Deserialize<ConversationLine>(line, JsonUtil.Opts);
                if (item is not null)
                    list.Add(item);
            }
            catch (JsonException)
            {
                // Skip a torn line.
            }
        }

        return list;
    }
}
