using System.Text.Json.Serialization;

namespace ChatBridge;

/// <summary>One configured agent. Identity is <see cref="Id"/>; room nicks are how mentions match.</summary>
internal sealed record AgentRecord(string Id, IReadOnlyList<string> Nicks, string? Trip)
{
    public bool OwnsNick(string? nick) =>
        !string.IsNullOrEmpty(nick) && Nicks.Any(n => string.Equals(n, nick, StringComparison.OrdinalIgnoreCase));

    public bool Owns(string? token) =>
        OwnsNick(token) || string.Equals(Id, token, StringComparison.OrdinalIgnoreCase);
}

internal sealed class AgentConfig
{
    public string Id { get; set; } = "";
    public List<string> Nicks { get; set; } = new();
    public string? Trip { get; set; }
}

internal sealed class MentionConfig
{
    public bool Enabled { get; set; }

    /// <summary>
    /// How many explicit reply fan-outs are allowed. A room message from a non-agent is hop 0.
    /// An agent reply needs an explicit fan-out marker and a hop below this value. Default 1.
    /// </summary>
    [JsonPropertyName("max_fanout_hop")] public int MaxFanoutHop { get; set; } = 1;
}

/// <summary>Agents named in config. The directory is empty when the config lists none. No nick is implied.</summary>
internal sealed class AgentDirectory
{
    public static readonly AgentDirectory Empty = new(Array.Empty<AgentRecord>());

    private readonly Dictionary<string, AgentRecord> _byId;

    private AgentDirectory(IReadOnlyList<AgentRecord> agents)
    {
        Agents = agents;
        _byId = agents.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AgentRecord> Agents { get; }

    public AgentRecord? FindByNick(string? nick) =>
        string.IsNullOrEmpty(nick) ? null : Agents.FirstOrDefault(a => a.OwnsNick(nick));

    public AgentRecord? Find(string? token) =>
        string.IsNullOrEmpty(token) ? null : Agents.FirstOrDefault(a => a.Owns(token));

    public static AgentDirectory Parse(string configPath, IReadOnlyList<AgentConfig>? agents, MentionConfig mentions)
    {
        if (mentions.MaxFanoutHop is < 1 or > 3)
            throw new ConfigException($"{configPath}: mentions.max_fanout_hop must be from 1 to 3");

        if (agents is null || agents.Count == 0)
            return Empty;

        var records = new List<AgentRecord>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNicks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            var id = (agent.Id ?? "").Trim();
            if (!AgentPath.IsSafeId(id))
                throw new ConfigException($"{configPath}: agent id must be 1-64 letters, digits, '.', '_' or '-' and must not be a path");
            if (!seenIds.Add(id))
                throw new ConfigException($"{configPath}: duplicate agent id '{id}'");

            var nicks = (agent.Nicks ?? new()).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (nicks.Count == 0)
                throw new ConfigException($"{configPath}: agent '{id}' needs at least one nick");
            foreach (var nick in nicks)
            {
                if (nick.Length > 64 || nick.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
                    throw new ConfigException($"{configPath}: agent '{id}' has a nick that is not a single token");
                if (!seenNicks.Add(nick))
                    throw new ConfigException($"{configPath}: nick '{nick}' is listed on more than one agent");
            }

            var trip = string.IsNullOrWhiteSpace(agent.Trip) ? null : PublicTrip.Canonical(agent.Trip);
            if (!string.IsNullOrWhiteSpace(agent.Trip) && trip is null)
                throw new ConfigException($"{configPath}: agent '{id}' trip must be a public code like Ab12Cd, not a password");
            records.Add(new AgentRecord(id, nicks, trip));
        }

        return new AgentDirectory(records);
    }
}

internal static class AgentPath
{
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return false;
        if (!char.IsAsciiLetterOrDigit(id[0]))
            return false;
        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        }

        return id is not "." and not "..";
    }
}
