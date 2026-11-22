using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Catalog;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Settings of the shared recogniser vocabulary (<c>Voice:Vocabulary</c>, decision V-36).</summary>
public sealed class VocabularyOptions
{
    /// <summary>On by default: it only biases what the recognisers listen for. Off: every recogniser gets only its static settings.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Most terms kept (brands first, then categories, then title words).</summary>
    public int MaxTerms { get; set; } = 100;
    public int RefreshMinutes { get; set; } = 30;
}

/// <summary>
/// The words of the live catalog that a recogniser should favour: brand names, category names and the most common words of product titles (decision V-36). One provider is shared by every recogniser, so each
/// option listens for the same things. Public catalog data only: nothing about any caller, order or account, so nothing here is personal. The list is a snapshot held in memory, refreshed on a timer; it is bounded in
/// size and every term is plain text (no commas, quotes or control characters), so a vendor-specific format can be built from it without escaping.
/// </summary>
public interface IVoiceVocabulary
{
    /// <summary>The current terms, most useful first. Empty when the vocabulary is off, not yet built or the catalog is empty.</summary>
    IReadOnlyList<string> Terms { get; }
    /// <summary>Comma separated hints within <paramref name="maxChars"/> characters: the static ones first (as configured), then the catalog terms that still fit, whole terms only.</summary>
    string Hints(string? staticHints, int maxChars);
}

public sealed class CatalogVocabulary(IServiceScopeFactory scopes, IOptions<VoiceOptions> options, ILogger<CatalogVocabulary> log) : IVoiceVocabulary
{
    private volatile IReadOnlyList<string> _terms = [];
    public IReadOnlyList<string> Terms => options.Value.Vocabulary.Enabled ? _terms : [];

    public string Hints(string? staticHints, int maxChars) => Join(staticHints, Terms, maxChars);

    internal static string Join(string? staticHints, IReadOnlyList<string> terms, int maxChars)
    {
        var sb = new StringBuilder();
        void Add(string term)
        {
            term = Clean(term);
            if (term.Length == 0) return;
            var extra = (sb.Length == 0 ? 0 : 2) + term.Length;
            if (sb.Length + extra > maxChars) return;      // whole terms only; a longer one is skipped, a shorter one later may still fit
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(term);
        }
        foreach (var s in (staticHints ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Add(s);
        var seen = new HashSet<string>(sb.ToString().Split(", ", StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        foreach (var t in terms) if (seen.Add(Clean(t))) Add(t);
        return sb.ToString();
    }

    /// <summary>A free-text recogniser prompt: the configured prompt as written, then (when there are terms) the catalog terms after it, whole terms only, within <paramref name="maxChars"/>. A prompt already longer than the limit is cut, never the terms.</summary>
    internal static string Prompt(string? configured, IReadOnlyList<string>? terms, int maxChars)
    {
        var sb = new StringBuilder((configured ?? "").Trim());
        if (sb.Length > maxChars) sb.Length = maxChars;
        if (terms is null || terms.Count == 0) return sb.ToString();
        var first = true;
        foreach (var raw in terms)
        {
            var t = Clean(raw);
            if (t.Length == 0) continue;
            var lead = first ? (sb.Length == 0 ? "" : " Words: ") : ", ";
            if (sb.Length + lead.Length + t.Length > maxChars) continue;
            sb.Append(lead).Append(t); first = false;
        }
        return sb.ToString();
    }

    /// <summary>Plain text only: letters, digits, spaces, hyphen, apostrophe and ampersand; at most 40 characters.</summary>
    internal static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'' or '&') sb.Append(c);
        var t = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return t.Length > 40 ? t[..40].TrimEnd() : t;
    }

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "the", "and", "for", "with", "from", "pack", "set", "new", "size", "pcs", "inch", "oz", "x", "of", "in", "a", "an", "to", "by", "on" };

    /// <summary>Builds the term list from visible listings: brands (by listing count), categories (by listing count), then title words by frequency. Caps: a share of <paramref name="max"/> for each kind.</summary>
    internal static IReadOnlyList<string> Build(IEnumerable<(string Name, int Count)> brands, IEnumerable<(string Name, int Count)> categories, IEnumerable<string> titles, int max)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Take(IEnumerable<string> names, int limit)
        {
            var n = 0;
            foreach (var raw in names)
            {
                if (n >= limit || result.Count >= max) return;
                var t = Clean(raw);
                if (t.Length < 2 || !seen.Add(t)) continue;
                result.Add(t); n++;
            }
        }
        Take(brands.OrderByDescending(b => b.Count).ThenBy(b => b.Name, StringComparer.Ordinal).Select(b => b.Name), Math.Max(1, max * 3 / 10));
        Take(categories.OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Name), Math.Max(1, max * 2 / 10));
        var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var title in titles)
            foreach (var w in Clean(title).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = w.Trim('-', '\'', '&');
                if (word.Length < 3 || Skip.Contains(word) || word.All(char.IsDigit)) continue;
                words[word] = words.GetValueOrDefault(word) + 1;
            }
        Take(words.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key), max);
        return result;
    }

    /// <summary>Reads the visible catalog and replaces the snapshot. A failure keeps the last snapshot and logs the exception type only.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var o = options.Value.Vocabulary;
        if (!o.Enabled) { _terms = []; return; }
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var visible = CatalogEndpoints.Visible(db);
            var brandCounts = await visible.GroupBy(l => l.BrandId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
            var catCounts = await visible.GroupBy(l => l.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
            var brandIds = brandCounts.Select(b => b.Id).ToList(); var catIds = catCounts.Select(c => c.Id).ToList();
            var brandNames = await db.CatalogBrands.AsNoTracking().Where(b => brandIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            var catNames = await db.CatalogCategories.AsNoTracking().Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var titles = await visible.OrderBy(l => l.Id).Select(l => l.Title).Take(2000).ToListAsync(ct);
            _terms = Build(brandCounts.Where(b => brandNames.ContainsKey(b.Id)).Select(b => (brandNames[b.Id], b.N)),
                catCounts.Where(c => catNames.ContainsKey(c.Id)).Select(c => (catNames[c.Id], c.N)), titles, Math.Clamp(o.MaxTerms, 0, 500));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("The recogniser vocabulary could not be refreshed ({Type}); the last one stays.", ex.GetType().Name); }
    }
}

/// <summary>Builds the vocabulary once at start and then every <c>Voice:Vocabulary:RefreshMinutes</c>. Not run in tests (they call <see cref="CatalogVocabulary.RefreshAsync"/>).</summary>
public sealed class CatalogVocabularyWorker(CatalogVocabulary vocabulary, IOptions<VoiceOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            await vocabulary.RefreshAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(options.Value.Vocabulary.RefreshMinutes, 1, 1440)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
