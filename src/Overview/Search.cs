using System;
using System.Text;

namespace Overview;

/// <summary>Small fuzzy matcher: exact > prefix > word-start > acronym > substring > subsequence.</summary>
internal static class Search
{
    public static string[] Tokenize(string query) =>
        query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Every token must match somewhere; returns 0 for no match.</summary>
    public static double Score(string[] tokens, string? text)
    {
        if (tokens.Length == 0 || string.IsNullOrEmpty(text)) return 0;
        string t = text.ToLowerInvariant();
        double total = 0;
        foreach (var q in tokens)
        {
            double s = TokenScore(q, t);
            if (s <= 0) return 0;
            total += s;
        }
        return total / tokens.Length;
    }

    static double TokenScore(string q, string t)
    {
        if (t == q) return 100;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 90 - Math.Min(15, (t.Length - q.Length) * 0.2);

        int idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx > 0)
        {
            for (int i = idx; i > 0; i = t.IndexOf(q, i + 1, StringComparison.Ordinal))
            {
                if (!char.IsLetterOrDigit(t[i - 1])) return 72 - Math.Min(12, i * 0.25);
            }
        }

        if (q.Length >= 2 && Acronym(t).StartsWith(q, StringComparison.Ordinal)) return 62;
        if (idx > 0) return 45 - Math.Min(12, idx * 0.25);
        if (q.Length >= 3 && Subsequence(q, t, out int spread)) return Math.Max(5, 28 - spread * 0.6);
        return 0;
    }

    static string Acronym(string t)
    {
        var sb = new StringBuilder();
        bool start = true;
        foreach (char c in t)
        {
            bool word = char.IsLetterOrDigit(c);
            if (word && start) sb.Append(c);
            start = !word;
        }
        return sb.ToString();
    }

    static bool Subsequence(string q, string t, out int spread)
    {
        spread = 0;
        int pos = -1, first = -1;
        foreach (char c in q)
        {
            pos = t.IndexOf(c, pos + 1);
            if (pos < 0) return false;
            if (first < 0) first = pos;
        }
        spread = pos - first + 1 - q.Length;
        return true;
    }
}
