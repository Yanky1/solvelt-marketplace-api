using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Runtime.CompilerServices;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>Where the recorded audio is kept: a private container and nothing else (decision V-46). Names are <see cref="RecordingName"/> paths. The audio is never copied anywhere else by this code.</summary>
public interface ITestSetStore
{
    IAsyncEnumerable<string> ListAsync(string set, CancellationToken ct);
    Task<byte[]> ReadAsync(string name, CancellationToken ct);
    /// <summary>Stores a new recording; an existing name is never replaced (a second take gets another speaker label or set name).</summary>
    Task WriteAsync(string name, byte[] wav, CancellationToken ct);
}

public sealed class RecordingExistsException(string name) : IOException($"The recording {name} already exists; use another speaker label or set name.");

/// <summary>
/// Azure Blob Storage. Signs in with the managed identity (DefaultAzureCredential: the managed identity in Azure, the developer's own Azure sign-in elsewhere), so no storage key is kept anywhere.
/// The container is created by the owner as PRIVATE; this class never creates it (a container made here could be made public by mistake) and never changes its access.
/// </summary>
public sealed class AzureTestSetStore : ITestSetStore
{
    private readonly BlobContainerClient _container;

    public AzureTestSetStore(TestSetOptions o, Azure.Core.TokenCredential? credential = null)
    {
        var url = o.StorageAccountUrl?.Trim() ?? "";
        _container = url.Length > 0
            ? new BlobContainerClient(new Uri(url.TrimEnd('/') + "/" + o.Container), credential ?? new DefaultAzureCredential())
            : new BlobContainerClient(o.ConnectionString ?? throw new InvalidOperationException("Voice:TestSet storage is not configured."), o.Container);
    }

    public async IAsyncEnumerable<string> ListAsync(string set, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var b in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, set + "/", ct)) yield return b.Name;
    }

    public async Task<byte[]> ReadAsync(string name, CancellationToken ct)
    {
        var r = await _container.GetBlobClient(name).DownloadContentAsync(ct);
        return r.Value.Content.ToArray();
    }

    public async Task WriteAsync(string name, byte[] wav, CancellationToken ct)
    {
        try { await _container.GetBlobClient(name).UploadAsync(new BinaryData(wav), overwrite: false, ct); }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.ErrorCode == "BlobAlreadyExists") { throw new RecordingExistsException(name); }
    }
}

/// <summary>Memory only, for tests: nothing is written to disk.</summary>
public sealed class MemoryTestSetStore : ITestSetStore
{
    private readonly SortedDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public async IAsyncEnumerable<string> ListAsync(string set, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var k in _blobs.Keys.Where(k => k.StartsWith(set + "/", StringComparison.Ordinal)).ToList()) { await Task.Yield(); yield return k; }
    }
    public Task<byte[]> ReadAsync(string name, CancellationToken ct) => _blobs.TryGetValue(name, out var b) ? Task.FromResult(b) : throw new FileNotFoundException(name);
    public Task WriteAsync(string name, byte[] wav, CancellationToken ct)
    {
        if (_blobs.ContainsKey(name)) throw new RecordingExistsException(name);
        _blobs[name] = wav;
        return Task.CompletedTask;
    }
}
