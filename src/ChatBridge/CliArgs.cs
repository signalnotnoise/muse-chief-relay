namespace ChatBridge;

/// <summary>
/// Command line: <c>[--config path] [say|status|watch|hook|inbox|help] [args…]</c>. <c>--config path</c> and
/// <c>--config=path</c> are accepted anywhere before <c>--</c>. For the bridge run, a single bare
/// argument is still taken as the config path (the original interface).
/// </summary>
internal sealed record CliArgs(string Command, string? ConfigPath, IReadOnlyList<string> Rest)
{
    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        string? config = null;
        var rest = new List<string>();
        var endOfOptions = false;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!endOfOptions && a == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && (a == "--config" || a.StartsWith("--config=", StringComparison.Ordinal)))
            {
                string value;
                if (a == "--config")
                {
                    if (i + 1 >= args.Count)
                        throw new ArgumentException("--config needs a path");
                    value = args[++i];
                }
                else
                {
                    value = a["--config=".Length..];
                }

                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("--config needs a path");
                if (config is not null)
                    throw new ArgumentException("--config given more than once");
                config = value;
                continue;
            }

            rest.Add(a);
        }

        if (rest.Count > 0 && rest[0] is "say" or "status" or "watch" or "hook" or "inbox" or "reconcile" or "stop" or "restart" or "outbox")
            return new CliArgs(rest[0], config, rest.Skip(1).ToList());
        if (rest.Count > 0 && rest[0] is "help" or "-h" or "--help")
            return new CliArgs("help", config, rest.Skip(1).ToList());

        // Bridge run. Optional bare config path as the only argument.
        if (rest.Count > 1)
            throw new ArgumentException($"unexpected arguments: {string.Join(' ', rest.Skip(1))}");
        if (rest.Count == 1)
        {
            if (config is not null)
                throw new ArgumentException("give the config path either as --config or as the argument, not both");
            config = rest[0];
        }

        return new CliArgs("run", config, Array.Empty<string>());
    }
}
