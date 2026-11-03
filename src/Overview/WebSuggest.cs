using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Overview;

/// <summary>
/// Search suggestions ("autocomplete") from the same engine the default browser searches with.
/// All three endpoints return the OpenSearch format: ["query", ["suggestion", ...]].
/// </summary>
internal static class WebSuggest
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Overview/1.3");
        return c;
    }

    const string Google = "https://suggestqueries.google.com/complete/search?client=firefox&ie=UTF-8&oe=UTF-8&q=";
    const string Brave = "https://search.brave.com/api/suggest?q=";
    const string Bing = "https://api.bing.com/osjson.aspx?query=";
    const string DuckDuckGo = "https://duckduckgo.com/ac/?type=list&q=";

    public static async Task<List<string>> FetchAsync(string query, int max, CancellationToken ct)
    {
        string q = Uri.EscapeDataString(query);
        // Suggestions from the engine chosen in Settings, or the one the default browser uses.
        string engine = App.Settings.SearchEngine;
        if (engine == "default")
        {
            string browser = Path.GetFileNameWithoutExtension(WebSearch.BrowserExe() ?? "").ToLowerInvariant();
            engine = browser switch { "brave" => "brave", "msedge" => "bing", _ => "google" };
        }
        string primary = engine switch
        {
            "brave" => Brave,
            "bing" => Bing,
            "duckduckgo" => DuckDuckGo,
            _ => Google
        };

        try
        {
            return await FetchFrom(primary + q, query, max, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            if (primary == Google) { Log.Write("suggestions: " + e.Message); return new List<string>(); }
            // The browser's engine didn't answer in the expected format: fall back to Google.
            return await FetchFrom(Google + q, query, max, ct);
        }
    }

    static async Task<List<string>> FetchFrom(string url, string query, int max, CancellationToken ct)
    {
        var bytes = await Http.GetByteArrayAsync(url, ct);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var result = new List<string>();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return result;
        foreach (var item in root[1].EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(s) || string.Equals(s, query.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Contains(s)) continue;
            result.Add(s);
            if (result.Count >= max) break;
        }
        return result;
    }
}
