namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>
/// The <c>Voice:TestSet</c> section (decision V-46): a small set of recorded phrases, English and Yiddish, that the owner replays through every recogniser to compare them with numbers.
/// The audio is a caller's voice, so it lives ONLY in a private Azure Blob container: never in the repository, a CI artifact or a log. Off by default.
/// </summary>
public sealed class TestSetOptions
{
    /// <summary>Lets the owner's test numbers (<c>Voice:PipelineMenu:TestNumbers</c>) record phrases. Reading the audio back (the scoring tool) needs only the storage settings, not this switch.</summary>
    public bool Enabled { get; set; }
    /// <summary>https://&lt;account&gt;.blob.core.windows.net . The API and the tool sign in with the managed identity (or the developer's own Azure sign-in); no key is stored. The identity needs the role Storage Blob Data Contributor on the container.</summary>
    public string StorageAccountUrl { get; set; } = "";
    /// <summary>The private container (created by the owner, public access off).</summary>
    public string Container { get; set; } = "voice-test-set";
    /// <summary>A development fallback only: an Azure storage connection string. Refused when <see cref="StorageAccountUrl"/> is also set, and never for production.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>The name of the set (the blob folder), so a second round of recordings never overwrites the first.</summary>
    public string SetName { get; set; } = "set1";
    /// <summary>The longest one phrase may be recorded, in seconds (1 to 60).</summary>
    public int MaxRecordSeconds { get; set; } = 20;

    public const string NamePattern = "^[a-z0-9][a-z0-9-]{0,39}$";

    /// <summary>Invalid values; empty when fine. Only checked when the set is switched on.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(SetName ?? "", NamePattern)) p.Add("Voice:TestSet:SetName must be 1 to 40 lower-case letters, digits or hyphens.");
        if (MaxRecordSeconds is < 1 or > 60) p.Add("Voice:TestSet:MaxRecordSeconds must be between 1 and 60.");
        var url = StorageAccountUrl?.Trim() ?? "";
        if (url.Length == 0 && string.IsNullOrWhiteSpace(ConnectionString)) p.Add("Voice:TestSet:StorageAccountUrl is required (the private container the audio is kept in).");
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.AbsolutePath.Trim('/').Equals("") || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0))
            p.Add("Voice:TestSet:StorageAccountUrl must be an https address with no path, like https://account.blob.core.windows.net .");
        if (url.Length > 0 && !string.IsNullOrWhiteSpace(ConnectionString)) p.Add("Voice:TestSet:ConnectionString must not be set together with StorageAccountUrl (managed identity is the way; the connection string is a development fallback only).");
        if (!System.Text.RegularExpressions.Regex.IsMatch(Container ?? "", "^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$")) p.Add("Voice:TestSet:Container must be 3 to 63 lower-case letters, digits or single hyphens.");
        return p;
    }
}
