using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Catalog;

/// <summary>
/// The category table, read once per request (it is small and several parts of one request need it: the filter, the facets, the cards) with the parent to children
/// lookup built once, so "this category and everything below it" is one pass over the subtree instead of a scan of every category per step (M2-14).
/// </summary>
public sealed class CategoryMap(MarketplaceDb db)
{
    private List<CatalogCategory>? _all;
    private ILookup<Guid, CatalogCategory>? _children;
    private Dictionary<Guid, CatalogCategory>? _byId;

    /// <summary>Every category in the portal's order.</summary>
    public async Task<List<CatalogCategory>> AllAsync(CancellationToken ct)
    {
        if (_all is not null) return _all;
        var all = await db.CatalogCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        _byId = all.ToDictionary(c => c.Id);
        _children = all.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value);
        return _all = all;
    }

    public async Task<IReadOnlyDictionary<Guid, CatalogCategory>> ByIdAsync(CancellationToken ct) { await AllAsync(ct); return _byId!; }

    /// <summary>A category by id or slug; null when unknown.</summary>
    public async Task<CatalogCategory?> FindAsync(string slugOrId, CancellationToken ct)
    {
        var all = await AllAsync(ct);
        return Guid.TryParse(slugOrId, out var id) ? _byId!.GetValueOrDefault(id) : all.FirstOrDefault(c => c.Slug == slugOrId);
    }

    /// <summary>The category and every category below it. Each is visited once, so a cycle in portal data ends instead of looping, and the cost is the size of the subtree.</summary>
    public async Task<List<Guid>> DescendantsAsync(Guid root, CancellationToken ct)
    {
        await AllAsync(ct);
        var result = new List<Guid> { root };
        var seen = new HashSet<Guid> { root };
        for (var i = 0; i < result.Count; i++)
            foreach (var child in _children![result[i]])
                if (seen.Add(child.Id)) result.Add(child.Id);
        return result;
    }

    /// <summary>The root first, then its ancestors up to the top; at most <see cref="CategoryRules.MaxDepth"/> + 2 steps, and a repeat ends the walk.</summary>
    public async Task<List<CatalogCategory>> PathAsync(Guid id, CancellationToken ct)
    {
        await AllAsync(ct);
        var byId = _byId!;
        var path = new List<CatalogCategory>();
        for (var c = byId.GetValueOrDefault(id); c is not null && path.Count < CategoryRules.MaxDepth + 2 && path.All(p => p.Id != c.Id); c = c.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            path.Insert(0, c);
        return path;
    }
}
