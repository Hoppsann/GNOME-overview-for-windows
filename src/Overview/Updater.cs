using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Overview;

internal sealed record Release(Version Version, string Tag, string? SetupUrl, string PageUrl);

/// <summary>
/// Checks GitHub Releases for a newer version and downloads its installer.
/// A release is picked up when its tag is a version ("v1.8.0") and it has an asset named Overview-Setup.exe.
/// </summary>
internal static class Updater
{
    public const string Repo = "Hoppsann/GNOME-overview-for-windows";
    public const string RepoUrl = "https://github.com/" + Repo;
    public const string SetupAssetName = "Overview-Setup.exe";

    // Must come before Http: static fields initialise top to bottom, and CreateClient uses it.
    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // room for slow downloads
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Overview-Updater/" + Current);
        return c;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>The latest published release, or null if there is none or GitHub can't be reached.</summary>
    public static async Task<Release?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var version)) return null;
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;

            string? url = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.Equals(name, SetupAssetName, StringComparison.OrdinalIgnoreCase)) continue;
                    url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
            }
            return new Release(version, tag, url, page);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write("update check: " + e.Message);
            return null;
        }
    }

    /// <summary>"v1.8.0", "1.8", "v1.8.0-beta" → 1.8.0</summary>
    static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string s = tag.Trim().TrimStart('v', 'V');
        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        if (!Version.TryParse(s.Substring(0, end).TrimEnd('.'), out var v)) return false;
        version = Normalize(v);
        return true;
    }

    public static bool IsNewer(Release r) => r.SetupUrl != null && r.Version > Current;

    /// <summary>Downloads the release's installer to the temp folder and returns its path.</summary>
    public static async Task<string> DownloadSetupAsync(Release r, CancellationToken ct)
    {
        if (r.SetupUrl == null) throw new InvalidOperationException("The release has no installer.");
        // The name must contain "Setup" so the file runs as the installer.
        string path = Path.Combine(Path.GetTempPath(), $"Overview-Setup-{r.Version}.exe");
        using var resp = await Http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using (var file = File.Create(path))
            await resp.Content.CopyToAsync(file, ct);
        return path;
    }
}
