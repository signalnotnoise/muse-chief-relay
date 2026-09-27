using System.Globalization;
using System.Text;

namespace Chief.Knowledge;

/// <summary>
/// BERT WordPiece tokenizer matching the uncased MiniLM vocab
/// (lowercase, strip accents, punctuation split, ## continuations).
/// </summary>
public sealed class BertWordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;

    public BertWordPieceTokenizer(string vocabPath)
    {
        var bytes = File.ReadAllBytes(vocabPath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (hash != MiniLm.VocabSha256)
            throw new KnowledgeException(2, $"vocab.txt hash is {hash}, expected {MiniLm.VocabSha256}");
        var lines = new UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        _vocab = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
            _vocab[lines[i]] = i;
        _ = Id("[CLS]");
        _ = Id("[SEP]");
        _ = Id("[UNK]");
    }

    public int[] Encode(string text, int maxTokens = 256)
    {
        var pieces = WordPiece(BasicTokenize(text));
        var limit = Math.Max(2, maxTokens) - 2;
        if (pieces.Count > limit)
            pieces.RemoveRange(limit, pieces.Count - limit);
        var ids = new int[pieces.Count + 2];
        ids[0] = Id("[CLS]");
        for (var i = 0; i < pieces.Count; i++)
            ids[i + 1] = Id(pieces[i]);
        ids[^1] = Id("[SEP]");
        return ids;
    }

    private int Id(string token)
    {
        if (!_vocab.TryGetValue(token, out var id))
            throw new KnowledgeException(2, $"vocab is missing {token}");
        return id;
    }

    private List<string> BasicTokenize(string text)
    {
        var cleaned = SeparateChinese(Clean(text));
        var output = new List<string>();
        foreach (var token in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = StripAccents(token.ToLowerInvariant());
            output.AddRange(SplitPunctuation(lower));
        }
        return output;
    }

    private List<string> WordPiece(List<string> tokens)
    {
        var result = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length > 100)
            {
                result.Add("[UNK]");
                continue;
            }
            var start = 0;
            var sub = new List<string>();
            var bad = false;
            while (start < token.Length)
            {
                var end = token.Length;
                string? cur = null;
                while (start < end)
                {
                    var substr = token[start..end];
                    if (start > 0)
                        substr = "##" + substr;
                    if (_vocab.ContainsKey(substr))
                    {
                        cur = substr;
                        break;
                    }
                    end--;
                }
                if (cur is null)
                {
                    bad = true;
                    break;
                }
                sub.Add(cur);
                start = end;
            }
            if (bad)
                result.Add("[UNK]");
            else
                result.AddRange(sub);
        }
        return result;
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\0' || c == '\uFFFD')
                continue;
            if (IsControl(c))
                continue;
            sb.Append(IsWhitespace(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    private static string SeparateChinese(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsChinese(c))
            {
                sb.Append(' ');
                sb.Append(c);
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string StripAccents(string text)
    {
        var norm = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<string> SplitPunctuation(string token)
    {
        var output = new List<string>();
        var current = new StringBuilder();
        foreach (var c in token)
        {
            if (IsPunctuation(c))
            {
                if (current.Length > 0)
                {
                    output.Add(current.ToString());
                    current.Clear();
                }
                output.Add(c.ToString());
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            output.Add(current.ToString());
        return output;
    }

    private static bool IsWhitespace(char c) =>
        c is ' ' or '\t' or '\n' or '\r' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    private static bool IsControl(char c)
    {
        if (c is '\t' or '\n' or '\r')
            return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate;
    }

    private static bool IsPunctuation(char c)
    {
        var cp = (int)c;
        if ((cp is >= 33 and <= 47) || (cp is >= 58 and <= 64) || (cp is >= 91 and <= 96) || (cp is >= 123 and <= 126))
            return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private static bool IsChinese(char c)
    {
        var cp = (int)c;
        return cp is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0xF900 and <= 0xFAFF);
    }
}
