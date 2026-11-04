namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>Slug choice for categories and brands: slugs are globally unique (they are URLs), the portal may only keep them unique per parent or per seller.</summary>
public static class SlugRules
{
    /// <summary>
    /// The feed's slug when it is free or already ours; otherwise the row's current slug (URLs stay stable); a new row gets an id suffix
    /// (6, then 12, then 32 hex characters) so it can never collide, whatever generated or fed slugs exist.
    /// </summary>
    public static string Choose(string desired, Guid id, string? currentSlug, IReadOnlyDictionary<string, Guid> used)
    {
        desired = SnapshotLimits.Clip(desired, SnapshotLimits.Slug);
        bool FreeOrOurs(string slug) => !used.TryGetValue(slug, out var owner) || owner == id;
        if (FreeOrOurs(desired)) return desired;
        if (currentSlug is not null) return currentSlug;
        var n = id.ToString("N");
        foreach (var len in new[] { 6, 12, 32 })
        {
            var candidate = SnapshotLimits.Clip(desired, SnapshotLimits.Slug - 1 - len) + "-" + n[..len];
            if (FreeOrOurs(candidate)) return candidate;
        }
        return n;
    }
}

public static class CategoryRules
{
    /// <summary>The deepest level a category may have (roots are level 1). Deeper ones are attached to their ancestor at this level: the tree stays readable and bounded.</summary>
    public const int MaxDepth = 10;

    /// <summary>Drops records with no slug or name, cuts names and slugs to their columns, keeps one record per id, and makes the parent links a forest of at most <see cref="MaxDepth"/> levels.</summary>
    public static IReadOnlyList<CategoryDto> Normalise(IReadOnlyList<CategoryDto> feed)
    {
        var byId = new Dictionary<Guid, CategoryDto>();
        foreach (var c in feed.OrderBy(c => c.SortOrder).ThenBy(c => c.Id))
        {
            if (string.IsNullOrWhiteSpace(c.Slug) || string.IsNullOrWhiteSpace(c.Name)) continue;
            byId[c.Id] = c with { Slug = SnapshotLimits.Clip(c.Slug.Trim(), SnapshotLimits.Slug), Name = SnapshotLimits.Clip(c.Name.Trim(), SnapshotLimits.DisplayName) };
        }
        // a parent that is itself, or not in the feed, makes a root
        var parent = byId.ToDictionary(kv => kv.Key, kv => kv.Value.ParentId is { } p && p != kv.Key && byId.ContainsKey(p) ? p : (Guid?)null);
        foreach (var start in byId.Keys.OrderBy(k => k))        // break cycles at the edge that closes them
        {
            var seen = new HashSet<Guid> { start };
            var cur = start;
            while (parent[cur] is { } up) { if (!seen.Add(up)) { parent[cur] = null; break; } cur = up; }
        }
        var acyclic = new Dictionary<Guid, Guid?>(parent);
        foreach (var id in byId.Keys)       // attach anything deeper than MaxDepth to its ancestor at MaxDepth - 1
        {
            var chain = new List<Guid>();   // root first
            for (Guid? cur = acyclic[id]; cur is { } c; cur = acyclic[c]) chain.Insert(0, c);
            if (chain.Count + 1 > MaxDepth) parent[id] = chain[MaxDepth - 2];
        }
        return byId.Values.Select(c => c with { ParentId = parent[c.Id] }).ToList();
    }
}

public static class BrandRules
{
    public static IReadOnlyList<BrandDto> Normalise(IReadOnlyList<BrandDto> feed)
    {
        var byId = new Dictionary<Guid, BrandDto>();
        foreach (var b in feed.OrderBy(b => b.Id))
        {
            if (string.IsNullOrWhiteSpace(b.Slug) || string.IsNullOrWhiteSpace(b.Name)) continue;
            byId[b.Id] = b with { Slug = SnapshotLimits.Clip(b.Slug.Trim(), SnapshotLimits.Slug), Name = SnapshotLimits.Clip(b.Name.Trim(), SnapshotLimits.DisplayName) };
        }
        return byId.Values.ToList();
    }
}
