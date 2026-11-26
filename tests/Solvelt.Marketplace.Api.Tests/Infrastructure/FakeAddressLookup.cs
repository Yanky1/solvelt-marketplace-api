using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>A scripted address lookup: no network. Remembers what it was asked, so a test can prove nothing but the query went out.</summary>
public sealed class FakeAddressLookup : IAddressLookup
{
    public bool IsConfigured { get; set; } = true;
    public string? UnavailableReason => "fake";
    public string? Attribution { get; set; }
    public List<string?> Sessions { get; } = [];
    public List<AddressQuery> Queries { get; } = [];
    public List<string> Resolved { get; } = [];
    public Func<AddressQuery, IReadOnlyList<AddressSuggestion>> Suggestions { get; set; } = _ => [];
    public Func<string, AddressParts?> Parts { get; set; } = _ => null;
    public Exception? Fail { get; set; }

    public Task<IReadOnlyList<AddressSuggestion>> SuggestAsync(AddressQuery query, CancellationToken ct)
    {
        Queries.Add(query); Sessions.Add(query.Session);
        return Fail is null ? Task.FromResult(Suggestions(query)) : throw Fail;
    }

    public Task<AddressParts?> ResolveAsync(string suggestionId, string? session, CancellationToken ct)
    {
        Resolved.Add(suggestionId); Sessions.Add(session);
        return Fail is null ? Task.FromResult(Parts(suggestionId)) : throw Fail;
    }
}
