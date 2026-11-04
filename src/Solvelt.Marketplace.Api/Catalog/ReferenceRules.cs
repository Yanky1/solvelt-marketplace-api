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
        // Linear time: a node whose walk reaches an already-checked node is acyclic from there on, so each node is walked once.
        var checkedOk = new HashSet<Guid>();
        foreach (var start in byId.Keys.OrderBy(k => k))        // break cycles at the edge that closes them
        {
            var path = new List<Guid> { start };
            var seen = new HashSet<Guid> { start };
            var cur = start;
            while (!checkedOk.Contains(cur) && parent[cur] is { } up) { if (!seen.Add(up)) { parent[cur] = null; break; } path.Add(up); cur = up; }
            foreach (var p in path) checkedOk.Add(p);
        }
        // Depth (roots are level 1) and the ancestor at level MaxDepth - 1 are memoised per node; anything deeper than MaxDepth attaches to that ancestor.
        var depth = new Dictionary<Guid, int>();
        var capAncestor = new Dictionary<Guid, Guid>();
        foreach (var id in byId.Keys)
        {
            var stack = new Stack<Guid>();
            for (Guid? cur = id; cur is { } c && !depth.ContainsKey(c); cur = parent[c]) stack.Push(c);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                var d = parent[c] is { } up ? depth[up] + 1 : 1;
                depth[c] = d;
                if (d == MaxDepth - 1) capAncestor[c] = c;
                else if (d > MaxDepth - 1) capAncestor[c] = capAncestor[parent[c]!.Value];
            }
        }
        var attached = new Dictionary<Guid, Guid?>(parent);
        foreach (var id in byId.Keys) if (depth[id] > MaxDepth) attached[id] = capAncestor[parent[id]!.Value];
        parent = attached;
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
