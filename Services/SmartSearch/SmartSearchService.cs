using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services.SmartSearch;

public static class SmartSearchService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private const string WikiBase = "https://wiki.vrcnext.com/api.php";
    private const int MaxCandidatePages = 6;
    private const int MaxKeywordSearches = 6;
    private const int MaxSectionChars = 7000;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "to", "in", "on", "of", "for", "how", "do", "i", "my", "me", "you", "your",
        "can", "what", "where", "when", "why", "does", "is", "are", "and", "or", "with", "have", "has",
        "it", "this", "that", "use", "using", "get", "if", "from", "at", "be", "as", "which", "there",
        "their", "them", "they", "we", "want", "need", "please", "some", "any", "all", "vrcnext", "vrchat"
    };

    private const int MaxAlternatives = 3;
    private const double MinAlternativeScore = 2.0;
    private const double MinAlternativeRatio = 0.25;

    public sealed record WikiHit(string title, string slug, string heading, string answer);
    public sealed record LookupResult(bool ok, string answer, string title, string slug, List<WikiHit> alternatives);

    public static async Task<LookupResult> LookupAsync(string question)
    {
        var none = new LookupResult(false, "", "", "", new List<WikiHit>());
        if (string.IsNullOrWhiteSpace(question)) return none;

        try
        {
            var kws = Keywords(question);
            if (kws.Count == 0) return none;

            var pages = await FetchPagesTreeAsync();
            var titles = pages.ToDictionary(p => p.slug, p => p.title, StringComparer.OrdinalIgnoreCase);

            var candidates = new List<string>();
            void Add(string s) { if (!string.IsNullOrEmpty(s) && !candidates.Contains(s)) candidates.Add(s); }

            foreach (var s in LocalKeywordScored(pages, question).Select(x => x.slug)) Add(s);
            foreach (var kw in kws.Take(MaxKeywordSearches))
                foreach (var s in await WikiSearchSlugsAsync(kw)) Add(s);

            if (candidates.Count == 0) return none;
            if (candidates.Count > MaxCandidatePages) candidates = candidates.Take(MaxCandidatePages).ToList();

            var hits = new List<(double score, string md, string title, string slug, string heading)>();

            foreach (var slug in candidates)
            {
                var content = await FetchPageContentAsync(slug);
                if (string.IsNullOrWhiteSpace(content)) continue;
                var pageTitle = titles.TryGetValue(slug, out var t) ? t : slug;

                foreach (var (heading, raw) in SplitSections(content))
                {
                    double score = ScoreSection(heading, raw, pageTitle, kws);
                    if (score <= 0) continue;
                    var md = raw.Trim();
                    if (string.IsNullOrWhiteSpace(md)) continue;
                    hits.Add((score, md, pageTitle, slug, heading));
                }
            }

            if (hits.Count == 0) return none;
            hits.Sort((a, b) => b.score.CompareTo(a.score));
            var best = hits[0];

            var alternatives = new List<WikiHit>();
            foreach (var h in hits.Skip(1))
            {
                if (alternatives.Count >= MaxAlternatives) break;
                if (h.score < MinAlternativeScore || h.score < best.score * MinAlternativeRatio) break;
                if (h.slug == best.slug || alternatives.Any(a => a.slug == h.slug)) continue;
                alternatives.Add(new WikiHit(h.title, h.slug, CleanHeading(h.heading), Clip(h.md)));
            }

            return new LookupResult(true, Clip(best.md), best.title, best.slug, alternatives);
        }
        catch
        {
            return none;
        }
    }

    private static string Clip(string md) => md.Length > MaxSectionChars ? md.Substring(0, MaxSectionChars) : md;

    private static string CleanHeading(string heading) => (heading ?? "").Replace("*", "").Replace("`", "").Replace("_", " ").Trim();

    private static List<string> Keywords(string question)
    {
        return System.Text.RegularExpressions.Regex.Matches(question.ToLowerInvariant(), "[a-z]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Distinct()
            .ToList();
    }

    private static List<(string slug, int score)> LocalKeywordScored(List<(string slug, string title, string category)> pages, string question)
    {
        var kws = Keywords(question);
        var scored = new List<(string slug, int score)>();
        if (kws.Count == 0) return scored;

        foreach (var p in pages)
        {
            var hay = (p.title + " " + p.slug.Replace('-', ' ') + " " + p.category).ToLowerInvariant();
            int score = kws.Count(k => hay.Contains(k));
            if (score > 0) scored.Add((p.slug, score));
        }
        return scored.OrderByDescending(s => s.score).ToList();
    }

    private static IEnumerable<(string heading, string raw)> SplitSections(string content)
    {
        var lines = content.Replace("\r", "").Split('\n');
        var buf = new List<string>();
        string heading = "";
        bool started = false;

        foreach (var line in lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (m.Success)
            {
                if (started && buf.Count > 0)
                    yield return (heading, string.Join("\n", buf));
                buf = new List<string> { line };
                heading = m.Groups[2].Value;
                started = true;
            }
            else
            {
                buf.Add(line);
                started = true;
            }
        }
        if (buf.Count > 0)
            yield return (heading, string.Join("\n", buf));
    }

    private static double ScoreSection(string heading, string raw, string pageTitle, List<string> kws)
    {
        var bodyLower = raw.ToLowerInvariant();
        var headLower = (heading ?? "").ToLowerInvariant();
        var titleLower = (pageTitle ?? "").ToLowerInvariant();

        int distinct = 0, headHits = 0, titleHits = 0, occurrences = 0;
        foreach (var k in kws)
        {
            int c = CountOccurrences(bodyLower, k);
            if (c > 0) distinct++;
            occurrences += c;
            if (headLower.Contains(k)) headHits++;
            if (titleLower.Contains(k)) titleHits++;
        }

        if (distinct == 0 && headHits == 0) return 0;
        return distinct * 2.0 + headHits * 5.0 + titleHits * 1.0 + Math.Min(occurrences, 12) * 0.1;
    }

    private static int CountOccurrences(string hay, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = hay.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static async Task<List<(string slug, string title, string category)>> FetchPagesTreeAsync()
    {
        var pages = new List<(string, string, string)>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{WikiBase}?a=pages_tree");
            req.Headers.UserAgent.ParseAdd("VRCNext");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return pages;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            if (json["pages"] is not JArray arr) return pages;
            foreach (var p in arr)
            {
                var slug = p["slug"]?.ToString();
                if (string.IsNullOrEmpty(slug)) continue;
                pages.Add((slug, p["title"]?.ToString() ?? slug, p["category"]?.ToString() ?? ""));
            }
        }
        catch { }
        return pages;
    }

    private static async Task<List<string>> WikiSearchSlugsAsync(string query)
    {
        var slugs = new List<string>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{WikiBase}?a=search&q={Uri.EscapeDataString(query)}");
            req.Headers.UserAgent.ParseAdd("VRCNext");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return slugs;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            if (json["results"] is not JArray results) return slugs;
            foreach (var r in results)
            {
                var slug = r["slug"]?.ToString();
                if (!string.IsNullOrEmpty(slug) && !slugs.Contains(slug)) slugs.Add(slug);
            }
        }
        catch { }
        return slugs;
    }

    private static async Task<string> FetchPageContentAsync(string slug)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{WikiBase}?a=page_get&slug={Uri.EscapeDataString(slug)}");
            req.Headers.UserAgent.ParseAdd("VRCNext");
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return "";
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["page"]?["content"]?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
