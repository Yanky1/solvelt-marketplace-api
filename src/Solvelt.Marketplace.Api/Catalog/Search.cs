using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>One ranked hit: higher <see cref="Score"/> is more relevant. Scores are comparable only inside one result.</summary>
public sealed record SearchMatch(Guid ListingId, double Score);

/// <summary>Turns user text into the terms both index implementations match on.</summary>
public static class SearchTerms
{
    public const int MaxQueryLength = 100, MaxTerms = 8, MaxTermLength = 40, MaxMatches = 1000;

    /// <summary>Normalised (lower-case ASCII, accents folded), de-duplicated words; at most <see cref="MaxTerms"/>, each cut to <see cref="MaxTermLength"/>.</summary>
    public static IReadOnlyList<string> From(string? q)
        => Slug.Words(q).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Length > MaxTermLength ? t[..MaxTermLength] : t).Distinct().Take(MaxTerms).ToList();
}

/// <summary>
/// Full-text search over the catalog (M2-01). Only <b>visible</b> listings of active sellers are returned (the join is part of every
/// implementation, so an unlisted listing disappears at once). Matching: every term must match, the last one as a prefix; when nothing
/// matches, a typo-tolerant pass runs. Ranking: title above brand above seller search terms above description.
/// </summary>
public interface ISearchIndex
{
    /// <summary>The best <paramref name="limit"/> matches, most relevant first (ties by id). Empty when <paramref name="terms"/> is empty.</summary>
    Task<IReadOnlyList<SearchMatch>> SearchAsync(MarketplaceDb db, IReadOnlyList<string> terms, int limit, CancellationToken ct);
}

/// <summary>Keeps <c>catalog_search</c> in step with the read model. Called from the same unit of work that applies a snapshot.</summary>
public static class SearchIndexer
{
    public const int MaxDescription = 4000, MaxTermsText = 2000;

    public static void Upsert(MarketplaceDb db, Guid listingId, Guid brandId, string title, string brandName, IEnumerable<string>? searchTerms, string description)
    {
        var row = db.CatalogSearch.Local.FirstOrDefault(r => r.ListingId == listingId) ?? db.CatalogSearch.Find(listingId);
        if (row is null) { row = new CatalogSearchRow { ListingId = listingId }; db.CatalogSearch.Add(row); }
        Fill(row, brandId, title, brandName, searchTerms, description);
    }

    public static void Fill(CatalogSearchRow row, Guid brandId, string title, string brandName, IEnumerable<string>? searchTerms, string description)
    {
        row.BrandId = brandId;
        row.Title = Cut(Slug.Words(title), 400);
        row.Brand = Cut(Slug.Words(brandName), 300);
        row.Terms = Cut(Slug.Words(string.Join(' ', searchTerms ?? [])), MaxTermsText);
        row.Description = Cut(Slug.Words(description), MaxDescription);
        row.Text = Cut($"{row.Title} {row.Brand} {row.Terms}".Trim(), 2800);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();

    /// <summary>Indexes every listing that has no search row yet (a database that predates the index, or a crash between steps). Idempotent.</summary>
    public static async Task<int> BackfillAsync(MarketplaceDb db, CancellationToken ct)
    {
        var missing = await db.CatalogListings.AsNoTracking().Where(l => !l.IsStub && !db.CatalogSearch.Any(s => s.ListingId == l.Id))
            .Select(l => new { l.Id, l.BrandId, l.Title, l.Description }).Take(500).ToListAsync(ct);
        if (missing.Count == 0) return 0;
        var brands = await db.CatalogBrands.AsNoTracking().Where(b => missing.Select(m => m.BrandId).Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        foreach (var m in missing)
            Upsert(db, m.Id, m.BrandId, m.Title, brands.GetValueOrDefault(m.BrandId) ?? "", null, m.Description);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return missing.Count;
    }

    /// <summary>A brand was renamed: the brand weight of its listings must follow.</summary>
    public static async Task RenameBrandAsync(MarketplaceDb db, Guid brandId, string newName, CancellationToken ct)
    {
        var name = Cut(Slug.Words(newName), 300);
        foreach (var row in await db.CatalogSearch.Where(s => s.BrandId == brandId && s.Brand != name).ToListAsync(ct))
        {
            row.Brand = name;
            row.Text = Cut($"{row.Title} {row.Brand} {row.Terms}".Trim(), 2800);
        }
    }
}

/// <summary>PostgreSQL-only objects (the generated weighted tsvector, GIN indexes, pg_trgm). EF cannot model them; run after the tables exist.</summary>
public static class SearchSchema
{
    /// <summary>Idempotent. M1's migration slice must include this in InitialCreate (<c>migrationBuilder.Sql</c>): Production has no EnsureCreated (M1-26).</summary>
    public const string PostgresDdl = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
        ALTER TABLE catalog_search ADD COLUMN IF NOT EXISTS document tsvector GENERATED ALWAYS AS (
            setweight(to_tsvector('simple', title), 'A') || setweight(to_tsvector('simple', brand), 'B') ||
            setweight(to_tsvector('simple', terms), 'C') || setweight(to_tsvector('simple', description), 'D')) STORED;
        CREATE INDEX IF NOT EXISTS ix_catalog_search_document ON catalog_search USING gin (document);
        CREATE INDEX IF NOT EXISTS ix_catalog_search_text_trgm ON catalog_search USING gin (text gin_trgm_ops);
        """;

    public static void Ensure(MarketplaceDb db)
    {
        if (db.Database.IsNpgsql()) db.Database.ExecuteSqlRaw(PostgresDdl);
    }
}

public sealed record SearchScore(Guid ListingId, double Score);

/// <summary>PostgreSQL: weighted tsvector with prefix matching and ts_rank; pg_trgm word similarity when nothing matches.</summary>
public sealed class PostgresSearchIndex : ISearchIndex
{
    // ts_rank weights are {D, C, B, A}: description 0.1, terms 0.2, brand 0.4, title 1.0.
    private const string Visible = " FROM catalog_search s JOIN catalog_listings l ON l.\"Id\" = s.listing_id AND l.\"Visible\" JOIN catalog_sellers sl ON sl.\"Id\" = l.\"SellerId\" AND sl.\"Active\" ";

    public async Task<IReadOnlyList<SearchMatch>> SearchAsync(MarketplaceDb db, IReadOnlyList<string> terms, int limit, CancellationToken ct)
    {
        if (terms.Count == 0) return [];
        // terms are [a-z0-9]+ (Slug.Words), so the tsquery text cannot carry operators; it is a parameter anyway.
        var prefix = string.Join(" & ", terms.Select(t => t + ":*"));
        var exact = string.Join(" & ", terms);
        // Rank = prefix match + exact-word match, so "ladle" ranks above "ladleful" for the query "ladle".
        var sql = "SELECT s.listing_id AS \"ListingId\", (ts_rank('{{0.1,0.2,0.4,1.0}}', s.document, q.query) + ts_rank('{{0.1,0.2,0.4,1.0}}', s.document, e.query))::float8 AS \"Score\" "
            + Visible + ", (SELECT to_tsquery('simple', {0}) AS query) q, (SELECT to_tsquery('simple', {1}) AS query) e WHERE s.document @@ q.query ORDER BY 2 DESC, s.listing_id LIMIT {2}";
        var hits = await db.Database.SqlQueryRaw<SearchScore>(sql, prefix, exact, limit).ToListAsync(ct);
        if (hits.Count == 0) hits = await Fuzzy(db, terms, limit, ct);
        return hits.Select(h => new SearchMatch(h.ListingId, h.Score)).ToList();
    }

    private static async Task<List<SearchScore>> Fuzzy(MarketplaceDb db, IReadOnlyList<string> terms, int limit, CancellationToken ct)
    {
        // Every term must be similar to a word of title/brand/terms. Terms of three letters or fewer are not guessed at.
        if (terms.Any(t => t.Length < 4)) return [];
        var args = new List<object>();
        var sims = new List<string>();
        foreach (var t in terms) { args.Add(t); sims.Add("word_similarity({" + (args.Count - 1) + "}, s.text)"); }
        args.Add(limit);
        var where = string.Join(" AND ", sims.Select(x => x + " >= 0.5"));
        var score = "(" + string.Join(" + ", sims) + $") / {terms.Count}";
        var sql = "SELECT s.listing_id AS \"ListingId\", (" + score + ")::float8 AS \"Score\" " + Visible
            + " WHERE " + where + " ORDER BY 2 DESC, s.listing_id LIMIT {" + (args.Count - 1) + "}";
        return await db.Database.SqlQueryRaw<SearchScore>(sql, args.ToArray()).ToListAsync(ct);
    }
}

/// <summary>
/// SQLite fallback for tests and local development (LIKE for the match, scoring and edit distance in code). Same contract as the PostgreSQL
/// index; at most <see cref="Candidates"/> rows are scored, which is far above the size of a test or dev catalog.
/// </summary>
public sealed class SqliteSearchIndex : ISearchIndex
{
    private const int Candidates = 2000;

    public async Task<IReadOnlyList<SearchMatch>> SearchAsync(MarketplaceDb db, IReadOnlyList<string> terms, int limit, CancellationToken ct)
    {
        if (terms.Count == 0) return [];
        var q = VisibleRows(db);
        foreach (var t in terms)
        {
            var pattern = "% " + t + "%";    // a word that starts with the term; terms are [a-z0-9]+, so there is nothing to escape
            q = q.Where(s => EF.Functions.Like(" " + s.Title + " " + s.Brand + " " + s.Terms + " " + s.Description, pattern));
        }
        var rows = await q.OrderBy(s => s.ListingId).Take(Candidates).ToListAsync(ct);
        var hits = rows.Select(r => new SearchMatch(r.ListingId, Score(r, terms))).ToList();
        if (hits.Count == 0) hits = await FuzzyAsync(db, terms, ct);
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.ListingId).Take(limit).ToList();
    }

    private static IQueryable<CatalogSearchRow> VisibleRows(MarketplaceDb db)
        => from s in db.CatalogSearch.AsNoTracking()
           join l in db.CatalogListings on s.ListingId equals l.Id
           where l.Visible && db.CatalogSellers.Any(sl => sl.Id == l.SellerId && sl.Active)
           select s;

    private static double Score(CatalogSearchRow r, IReadOnlyList<string> terms)
    {
        double total = 0;
        foreach (var t in terms)
            total += Field(r.Title, t, 4.0) + Field(r.Brand, t, 2.0) + Field(r.Terms, t, 1.2) + Field(r.Description, t, 0.4);
        return total;
    }

    /// <summary>Whole-word hit scores the weight, a prefix hit 75 % of it; the best word in the field counts once.</summary>
    private static double Field(string field, string term, double weight)
    {
        var best = 0.0;
        foreach (var w in field.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w == term) return weight;
            if (w.StartsWith(term, StringComparison.Ordinal)) best = weight * 0.75;
        }
        return best;
    }

    private static async Task<List<SearchMatch>> FuzzyAsync(MarketplaceDb db, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (terms.Any(t => t.Length < 4)) return [];
        var rows = await VisibleRows(db).OrderBy(s => s.ListingId).Take(Candidates).Select(s => new { s.ListingId, s.Text }).ToListAsync(ct);
        var hits = new List<SearchMatch>();
        foreach (var r in rows)
        {
            var words = r.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double score = 0;
            var all = true;
            foreach (var t in terms)
            {
                var allowed = t.Length >= 8 ? 2 : 1;
                var best = words.Min(w => Math.Min(Distance(t, w), w.Length > t.Length ? Distance(t, w[..t.Length]) : int.MaxValue));
                if (best > allowed) { all = false; break; }
                score += 1.0 / (1 + best);
            }
            if (all) hits.Add(new SearchMatch(r.ListingId, score / terms.Count));
        }
        return hits;
    }

    private static int Distance(string a, string b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }
}
