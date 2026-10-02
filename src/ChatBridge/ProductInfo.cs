namespace ChatBridge;

/// <summary>
/// Product names. The log tag is the program, not an agent nick. Agent identity comes from config.
/// </summary>
internal static class ProductInfo
{
    public const string Name = "ChatBridge";
    public const string LegacyName = "Chief.Bridge";
    public const string Command = "chat-bridge";
    public const string LegacyCommand = "chief-bridge";
    public const string LogTag = "chatbridge";

    /// <summary>Original config environment variable. Still checked first.</summary>
    public const string LegacyConfigEnv = "MUSE_RELAY_CONFIG";

    /// <summary>Accepted when <see cref="LegacyConfigEnv"/> is unset.</summary>
    public const string ConfigEnv = "CHATBRIDGE_CONFIG";

    public static string Prefix(string message) => $"[{LogTag}] {message}";
}
