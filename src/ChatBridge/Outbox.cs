using System.Text;

namespace ChatBridge;

internal sealed record OutboxLine(string Text, long EndOffset);

/// <summary>
/// Reads outbox.jsonl from a byte position. Only complete, newline-terminated lines are returned, so a
/// line that is still being written waits for its newline. Reading never moves the position: the caller
/// calls <see cref="Commit"/> after a line was sent, so a line whose send failed is returned again
/// next time (after a reconnect).
/// </summary>
internal sealed class OutboxReader(string path, long startPosition)
{
    public string Path { get; } = path;
    public long Position { get; private set; } = startPosition;

    /// <summary>Start at the current end of the file: lines written before the bridge started are not replayed.</summary>
    public static OutboxReader AtEnd(string path) =>
        new(path, File.Exists(path) ? new FileInfo(path).Length : 0);

    public IReadOnlyList<OutboxLine> ReadPending()
    {
        if (!File.Exists(Path))
            return Array.Empty<OutboxLine>();

        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        if (length < Position)
            Position = 0; // truncated or replaced: start over from the top
        if (length == Position)
            return Array.Empty<OutboxLine>();

        var count = checked((int)(length - Position));
        var buf = new byte[count];
        fs.Seek(Position, SeekOrigin.Begin);
        fs.ReadExactly(buf, 0, count);

        var lines = new List<OutboxLine>();
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            if (buf[i] != (byte)'\n')
                continue;
            var text = Encoding.UTF8.GetString(buf, start, i - start).TrimEnd('\r');
            lines.Add(new OutboxLine(text, Position + i + 1));
            start = i + 1;
        }

        // Bytes after the last newline are an incomplete line; they stay unread.
        return lines;
    }

    public void Commit(OutboxLine line)
    {
        if (line.EndOffset > Position)
            Position = line.EndOffset;
    }
}
