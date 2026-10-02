namespace ChatBridge;

/// <summary>
/// Wake/inbox contract an adapter consumes. This program does not call a model.
/// A long-lived bridge only appends durable events. An adapter (another process, or
/// <see cref="IAgentWakeAdapter"/> in a host) lists due events, does its own work, and acks.
/// </summary>
internal static class InboxContract
{
    public const int Version = 1;
    public const string Name = "chatbridge.inbox.wake";
    public const string RoomScope = "room";
    public const string SideScope = "side";

    /// <summary>Room history and side chats are different scopes. Side text is never a room wake.</summary>
    public static void RefuseSideCopy(string scope)
    {
        if (!string.Equals(scope, RoomScope, StringComparison.Ordinal))
            throw new InvalidOperationException("side context cannot be copied into the room");
    }
}

/// <summary>
/// One due room event, shaped for an adapter. Scope is always <see cref="InboxContract.RoomScope"/>.
/// Side-chat fields are not on this type.
/// </summary>
internal sealed class InboxWakeRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("v")] public int V { get; init; } = InboxContract.Version;
    [System.Text.Json.Serialization.JsonPropertyName("contract")] public string Contract { get; init; } = InboxContract.Name;
    [System.Text.Json.Serialization.JsonPropertyName("agent")] public string Agent { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("seq")] public long Seq { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("source_id")] public string SourceId { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("room")] public string Room { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("from")] public string From { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("text")] public string Text { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("mentions")] public IReadOnlyList<string> Mentions { get; init; } = Array.Empty<string>();
    [System.Text.Json.Serialization.JsonPropertyName("hop")] public int Hop { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("scope")] public string Scope { get; init; } = InboxContract.RoomScope;
    [System.Text.Json.Serialization.JsonPropertyName("attempts")] public int Attempts { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("next_unix")] public long? NextUnix { get; init; }

    public static InboxWakeRequest Create(InboxView view) => new()
    {
        Agent = view.Event.Agent,
        Id = view.Event.Id,
        Seq = view.Event.Seq,
        SourceId = view.Event.SourceId,
        Room = view.Event.Room,
        From = view.Event.From,
        Text = view.Event.Text,
        Mentions = view.Event.Mentions,
        Hop = view.Event.Hop,
        Scope = InboxContract.RoomScope,
        Attempts = view.Attempts,
        NextUnix = view.NextUnix
    };
}

/// <summary>
/// In-process adapter hook. Throw to signal a soft failure: the event stays pending and is
/// retried with backoff. Do not perform inference here; a host implements this to hand the
/// wake request to whatever runtime it owns.
/// </summary>
internal interface IAgentWakeAdapter
{
    ValueTask WakeAsync(InboxWakeRequest request, CancellationToken cancellationToken);
}

/// <summary>Result of one ordered wake pass. A soft failure does not throw.</summary>
internal sealed record WakePumpResult(int Acked, string? SoftFailedId, string? Error)
{
    public bool SoftFailed => SoftFailedId is not null;
}
