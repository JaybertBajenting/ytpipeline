using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace YtPipeline.Stages.Script;

/// <summary>One citation lifted out of a description's RESEARCH &amp; SOURCES block.</summary>
public sealed record SourceEntry(string Line, string Title, string? Year, string? NamedPerson);

/// <summary>What a bibliographic database says about a cited work.</summary>
/// <param name="Checked">
/// False when the databases could not be asked at all — rate limited, offline, timed out. This is
/// deliberately separate from <paramref name="Found"/>: an unanswered question is not a negative
/// answer, and treating it as one silently deletes real sources. An unchecked entry is left in the
/// description exactly as written.
/// </param>
public sealed record SourceCheck(
    SourceEntry Entry,
    bool Found,
    string? CanonicalTitle = null,
    string? FirstAuthor = null,
    int? Year = null,
    string? Venue = null,
    string? Doi = null,
    string? Note = null,
    bool Checked = true)
{
    /// <summary>Link to put in the description. Empty for anything without a DOI.</summary>
    public string Url => string.IsNullOrWhiteSpace(Doi) ? "" : $"https://doi.org/{Doi}";

    /// <summary>Asked, answered, and the work does not exist — the only case worth rewriting for.</summary>
    public bool Fabricated => Checked && !Found;
}

/// <summary>
/// Checks the sources in a generated description against real bibliographic records.
/// </summary>
/// <remarks>
/// A model asked for a bibliography invents citations that read exactly like real ones — in the
/// first description written for this channel, two entries out of eight were wrong (a paper
/// credited to a co-author rather than its first author, and a year that did not match the record),
/// and neither was detectable by reading. Asking another model to check is no help: that is the
/// same fallible process twice.
/// <para>
/// So the check is a lookup, not a judgement. Crossref covers DOI-registered papers and Open
/// Library covers books; both are free and need no key. A citation that cannot be found in either
/// is removed rather than published, and a citation that IS found is rewritten to match the record
/// — real first author, real year, real venue, real DOI link.
/// </para>
/// </remarks>
public sealed partial class SourceVerifier(IHttpClientFactory httpFactory, ILogger<SourceVerifier> logger)
{
    /// <summary>Crossref asks for contact details so it can warn about misbehaving clients.</summary>
    private const string UserAgent = "YtPipeline/1.0 (https://github.com/; mailto:noreply@example.com)";

    /// <summary>
    /// How much of the cited title must appear in the record's title to count as the same work.
    /// Set by hand rather than tuned: below about 0.8 a search starts matching sibling papers from
    /// the same dig, which is exactly the mistake this is here to prevent.
    /// </summary>
    private const double TitleMatchThreshold = 0.8;

    /// <summary>Pulls the citation lines out of the RESEARCH &amp; SOURCES block.</summary>
    public static IReadOnlyList<SourceEntry> Parse(string description)
    {
        var entries = new List<SourceEntry>();
        if (string.IsNullOrWhiteSpace(description)) return entries;

        var inSources = false;
        foreach (var raw in description.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Contains("RESEARCH & SOURCES", StringComparison.OrdinalIgnoreCase)) { inSources = true; continue; }
            // The block ends at the next heading — PEOPLE & SITES, hashtags, or a credit line.
            if (inSources && line.Contains("MENTIONED IN THIS VIDEO", StringComparison.OrdinalIgnoreCase)) break;
            if (!inSources || !line.TrimStart().StartsWith('>')) continue;

            var title = QuotedTitle().Match(line) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
            if (title.Length < 8) continue;   // a line with no real title can't be looked up

            var year = YearInParens().Match(line) is { Success: true } y ? y.Groups[1].Value : null;
            // "> Ruth Shahack-Gross and colleagues, Qesem Cave — ..." → the bit before the comma.
            var named = line.TrimStart('>', ' ').Split(',')[0].Split('—')[0].Trim();

            entries.Add(new SourceEntry(line, title, year, named.Length is > 2 and < 60 ? named : null));
        }
        return entries;
    }

    /// <summary>Looks every citation up. Order matches <see cref="Parse"/>.</summary>
    public async Task<IReadOnlyList<SourceCheck>> VerifyAsync(string description, CancellationToken ct)
    {
        var entries = Parse(description);
        var results = new List<SourceCheck>(entries.Count);

        foreach (var entry in entries)
        {
            var check = await LookUpAsync(entry, ct);
            results.Add(check);

            var title = entry.Title.Length > 60 ? entry.Title[..60] + "…" : entry.Title;
            if (!check.Checked)
                logger.LogWarning("Source UNCHECKED (kept as written): \"{Title}\" — {Note}", title, check.Note);
            else if (check.Found)
                logger.LogInformation("Source verified: \"{Title}\" → {Author} {Year}, {Venue}",
                    title, check.FirstAuthor, check.Year, check.Venue);
            else
                logger.LogWarning("Source NOT FOUND: \"{Title}\"", title);

            // Crossref asks for polite pacing rather than bursts.
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }
        return results;
    }

    /// <summary>
    /// Asks both databases and keeps the better answer.
    /// </summary>
    /// <remarks>
    /// Both are asked because they hold different things and each can mislead alone: Crossref
    /// indexes papers, Open Library indexes books, and searching Crossref for a book title finds
    /// the REVIEW of that book in some journal — which matched "Catching Fire" and would have
    /// published a 2010 Gastronomica review as if it were Wrangham's 2009 book. When both answer,
    /// the record whose year matches the citation wins, since that is the one the writer meant.
    /// </remarks>
    private async Task<SourceCheck> LookUpAsync(SourceEntry entry, CancellationToken ct)
    {
        var (paper, paperFailed) = await CrossrefAsync(entry, ct);
        var (book, bookFailed) = await OpenLibraryAsync(entry, ct);

        if (paper is null && book is null)
            // Neither could be reached — that is ignorance, not evidence of fabrication.
            return paperFailed && bookFailed
                ? new SourceCheck(entry, false, Note: "lookup unavailable", Checked: false)
                : new SourceCheck(entry, false, Note: "not found");

        if (paper is null) return book!;
        if (book is null) return paper;

        // Both found something, so decide which one the citation actually meant.

        // A record with no author at all is a poor match for a citation that credits a person —
        // and that is exactly what a journal's review of a book looks like. Searching Crossref for
        // "Catching Fire: How Cooking Made Us Human" finds a 2010 Gastronomica review with no
        // author, not Wrangham's book.
        var paperHasAuthor = !string.IsNullOrWhiteSpace(paper.FirstAuthor);
        var bookHasAuthor = !string.IsNullOrWhiteSpace(book.FirstAuthor);
        if (bookHasAuthor && !paperHasAuthor) return book;
        if (paperHasAuthor && !bookHasAuthor) return paper;

        // Then whichever names the person the line credits.
        if (entry.NamedPerson is { Length: > 1 } named)
        {
            var paperNamed = paperHasAuthor && named.Contains(paper.FirstAuthor!, StringComparison.OrdinalIgnoreCase);
            var bookNamed = bookHasAuthor && named.Contains(book.FirstAuthor!, StringComparison.OrdinalIgnoreCase);
            if (bookNamed && !paperNamed) return book;
            if (paperNamed && !bookNamed) return paper;
        }

        // Then whichever agrees with the year the citation claims.
        if (int.TryParse(entry.Year, out var cited))
        {
            if (book.Year == cited && paper.Year != cited) return book;
            if (paper.Year == cited && book.Year != cited) return paper;
        }

        return paper;   // otherwise the paper record, which carries a DOI
    }

    /// <summary>Papers. Crossref indexes anything with a DOI. Second value: the lookup failed.</summary>
    private async Task<(SourceCheck? Result, bool Failed)> CrossrefAsync(SourceEntry entry, CancellationToken ct)
    {
        try
        {
            var url = "https://api.crossref.org/works?rows=3&select=DOI,title,author,issued,container-title" +
                      $"&query.bibliographic={Uri.EscapeDataString(entry.Title)}";
            using var doc = await GetJsonAsync(url, ct);
            if (doc is null) return (null, true);

            if (!doc.RootElement.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("items", out var items)) return (null, false);

            foreach (var item in items.EnumerateArray())
            {
                var title = FirstString(item, "title");
                if (title is null || !SameWork(entry.Title, title)) continue;

                return (new SourceCheck(entry, true,
                    CanonicalTitle: title,
                    FirstAuthor: item.TryGetProperty("author", out var authors) && authors.GetArrayLength() > 0
                        && authors[0].TryGetProperty("family", out var fam) ? fam.GetString() : null,
                    Year: item.TryGetProperty("issued", out var issued)
                        && issued.TryGetProperty("date-parts", out var dp)
                        && dp.GetArrayLength() > 0 && dp[0].GetArrayLength() > 0
                        ? dp[0][0].GetInt32() : null,
                    Venue: FirstString(item, "container-title"),
                    Doi: item.TryGetProperty("DOI", out var doi) ? doi.GetString() : null), false);
            }
            return (null, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, true); }
        catch (Exception ex)
        {
            logger.LogWarning("Crossref lookup failed for \"{Title}\": {Message}", entry.Title, ex.Message);
            return (null, true);
        }
    }

    /// <summary>Books, which have no DOI and so are invisible to Crossref.</summary>
    private async Task<(SourceCheck? Result, bool Failed)> OpenLibraryAsync(SourceEntry entry, CancellationToken ct)
    {
        try
        {
            var url = "https://openlibrary.org/search.json?limit=3&fields=title,author_name,first_publish_year,publisher" +
                      $"&title={Uri.EscapeDataString(entry.Title)}";
            using var doc = await GetJsonAsync(url, ct);
            if (doc is null) return (null, true);
            if (!doc.RootElement.TryGetProperty("docs", out var docs)) return (null, false);

            foreach (var item in docs.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (title is null || !SameWork(entry.Title, title)) continue;

                return (new SourceCheck(entry, true,
                    CanonicalTitle: title,
                    FirstAuthor: item.TryGetProperty("author_name", out var an) && an.GetArrayLength() > 0
                        ? LastName(an[0].GetString()) : null,
                    Year: item.TryGetProperty("first_publish_year", out var y) ? y.GetInt32() : null,
                    Venue: item.TryGetProperty("publisher", out var p) && p.GetArrayLength() > 0
                        ? p[0].GetString() : null,
                    Note: "book"), false);
            }
            return (null, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, true); }
        catch (Exception ex)
        {
            logger.LogWarning("Open Library lookup failed for \"{Title}\": {Message}", entry.Title, ex.Message);
            return (null, true);
        }
    }

    /// <summary>
    /// GETs JSON, retrying a rate-limit or server error before giving up.
    /// </summary>
    /// <remarks>
    /// Crossref answered one lookup with 429 and the citation — a real paper — was dropped as
    /// fabricated. Returning null here means "could not ask", and the caller must never read that
    /// as "does not exist".
    /// </remarks>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var retryable = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            if (!retryable || attempt == 3) return null;

            logger.LogInformation("{Status} from the source database — retrying in {Seconds}s.",
                (int)response.StatusCode, attempt * 2);
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
        }
        return null;
    }

    /// <summary>
    /// Rewrites the sources block from the lookups: verified entries are corrected to match the
    /// record and given their real link; entries nothing could find are dropped.
    /// </summary>
    public static string ApplyCorrections(string description, IReadOnlyList<SourceCheck> checks)
    {
        var byLine = checks.ToDictionary(c => c.Entry.Line, c => c);
        var output = new StringBuilder();

        foreach (var raw in description.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!byLine.TryGetValue(line, out var check)) { output.AppendLine(line); continue; }
            // Only a citation the databases actually answered about, and denied, is removed.
            if (check.Fabricated) continue;
            if (!check.Checked) { output.AppendLine(line); continue; }
            output.AppendLine(Correct(line, check));
        }

        return output.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// Brings one citation line into line with the record: the year it was actually published, the
    /// journal it actually appeared in, the author who actually leads it, and its real link.
    /// </summary>
    private static string Correct(string line, SourceCheck check)
    {
        // The line splits at the first em dash: an attribution, then the citation itself. Only the
        // citation is touched.
        //
        // The attribution is deliberately left ALONE. Rewriting it was tried and abandoned: the
        // writer varies the shape of that half ("<topic>, <person>" as often as "<person>,
        // <topic>"), so a replacement guesses which side is the name and gets it wrong — it
        // produced "Hoberg et al., Eric Hoberg" and, worse, rewrote a site name inside a quoted
        // title into a paper that does not exist. A miscredited co-author is a far smaller problem
        // than mangled text, and the format rules are where that belongs.
        var split = line.IndexOf(" — ", StringComparison.Ordinal);
        var head = split > 0 ? line[..split] : line;
        var body = split > 0 ? line[split..] : "";

        // Year and venue live together in "(2014, Journal of Archaeological Science)", inside the
        // body — never in the attribution, so the title is untouched.
        if (check.Year is { } year)
        {
            var venue = string.IsNullOrWhiteSpace(check.Venue) ? null : check.Venue;
            body = YearVenueParens().Replace(body,
                venue is null ? $"({year})" : $"({year}, {venue})", 1);
        }

        // Real link, once, immediately after the (year, venue) it belongs to.
        if (check.Url.Length > 0 && !body.Contains("doi.org", StringComparison.OrdinalIgnoreCase))
        {
            var m = YearVenueParens().Match(body);
            body = m.Success
                ? string.Concat(body.AsSpan(0, m.Index + m.Length), " ", check.Url, body.AsSpan(m.Index + m.Length))
                : body + " " + check.Url;
        }

        return head + body;
    }

    /// <summary>Same work when nearly all of the cited title's words appear in the record's.</summary>
    internal static bool SameWork(string cited, string found)
    {
        var a = Tokens(cited);
        var b = Tokens(found);
        if (a.Count == 0) return false;
        var shared = a.Count(w => b.Contains(w));
        return (double)shared / a.Count >= TitleMatchThreshold;
    }

    private static HashSet<string> Tokens(string s) =>
        NonWord().Replace(s.ToLower(CultureInfo.InvariantCulture), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();

    private static string? FirstString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0].GetString() : null;

    private static string? LastName(string? full) =>
        string.IsNullOrWhiteSpace(full) ? null : full.Trim().Split(' ').Last();

    [GeneratedRegex("\"([^\"]{8,300})\"")]
    private static partial Regex QuotedTitle();

    [GeneratedRegex(@"\((\d{4})[,)]")]
    private static partial Regex YearInParens();

    [GeneratedRegex(@"\(\d{4}[^)]*\)")]
    private static partial Regex YearVenueParens();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();
}
