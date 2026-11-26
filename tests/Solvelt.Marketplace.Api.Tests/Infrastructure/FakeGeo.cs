using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>A scripted ZIP lookup: no network. Remembers every ZIP it was asked about.</summary>
public sealed class FakePostcodeLookup : IPostcodeLookup
{
    public bool IsConfigured { get; set; } = true;
    public List<string> Calls { get; } = [];
    public Func<string, ZipInfo?> Answer { get; set; } = zip => Zips.GetValueOrDefault(zip);
    public bool Unavailable { get; set; }

    /// <summary>The fixtures of the telephone system's own tests: Albany NY, Homestead FL, Mountain View CA.</summary>
    public static readonly Dictionary<string, ZipInfo> Zips = new()
    {
        ["12207"] = new("Albany", "NY", new GeoViewport(42.64, -73.77, 42.67, -73.74)),
        ["33034"] = new("Homestead", "FL", new GeoViewport(25.40, -80.60, 25.50, -80.40)),
        ["94043"] = new("Mountain View", "CA", null),
    };

    public Task<ZipInfo?> FindAsync(string zip5, CancellationToken ct)
    {
        Calls.Add(zip5);
        return Unavailable ? throw new LookupUnavailableException() : Task.FromResult(Answer(zip5));
    }
}

public sealed class FakeStreetSuggest : IStreetSuggest
{
    public bool IsConfigured { get; set; } = true;
    public List<(string Partial, GeoViewport? Viewport)> Calls { get; } = [];
    public Func<string, IReadOnlyList<string>> Answer { get; set; } = _ => ["Mazel Lane", "Maple Avenue"];
    public bool Unavailable { get; set; }

    public Task<IReadOnlyList<string>> StreetsAsync(string partial, GeoViewport? viewport, CancellationToken ct)
    {
        Calls.Add((partial, viewport));
        return Unavailable ? throw new LookupUnavailableException() : Task.FromResult(Answer(partial));
    }
}
