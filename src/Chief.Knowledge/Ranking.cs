using System.Text.RegularExpressions;

namespace Chief.Knowledge;

public static class Ranking
{
    public const int RrfK = 60;

    // Small on purpose. A perfect retrieval match (about 1/61 per list) still
    // beats a weak match that is merely newer or flagged.
    public const double ImportantBoost = 0.15;
    public const double RecencyWeight = 0.10;
    public const double SupersededFactor = 0.25;

    public static string? FtsMatch(string query)
    {
        var parts = new List<string>();
        foreach (var segment in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = Regex.Matches(segment, "[A-Za-z0-9_]+")
                .Select(m => m.Value.ToLowerInvariant())
                .ToList();
            if (tokens.Count == 0)
                continue;
            parts.Add("\"" + string.Join(' ', tokens) + "\"");
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    public static double AgeDays(DateOnly created, DateOnly today)
    {
        var days = today.DayNumber - created.DayNumber;
        return days < 0 ? 0 : days;
    }

    /// <summary>
    /// Reciprocal Rank Fusion of the two lists, then a small recency and
    /// importance multiplier. Ranks are 1-based and already limited to the
    /// filtered set. A null rank means that list did not return the note.
    /// </summary>
    public static double Fuse(int? ftsRank, int? vectorRank, bool important, double ageDays, bool demoteSuperseded)
    {
        if (ftsRank is null && vectorRank is null)
            return 0;
        double rrf = 0;
        if (ftsRank is int f)
            rrf += 1.0 / (RrfK + f);
        if (vectorRank is int v)
            rrf += 1.0 / (RrfK + v);
        var freshness = Math.Exp(-Math.Max(0, ageDays) / 365.0);
        var boost = 1.0 + (important ? ImportantBoost : 0) + RecencyWeight * freshness;
        var score = rrf * boost;
        if (demoteSuperseded)
            score *= SupersededFactor;
        return score;
    }
}
