using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>Counts the password work done, so a test can show every path does the same amount (no path is cheaper for an unknown address).</summary>
public sealed class CountingPasswordService(IOptions<AuthOptions> options) : PasswordService(options)
{
    private int _hash, _verify, _burn;
    public int Hashes => Volatile.Read(ref _hash);
    public int Verifies => Volatile.Read(ref _verify);
    public int Burns => Volatile.Read(ref _burn);
    /// <summary>Every password operation that costs a full hash: real hashing, real verifying or the stand-in for an unknown address.</summary>
    public int HashWork => Hashes + Verifies + Burns;
    public void Reset() { Interlocked.Exchange(ref _hash, 0); Interlocked.Exchange(ref _verify, 0); Interlocked.Exchange(ref _burn, 0); }

    public override string Hash(string password) { Interlocked.Increment(ref _hash); return base.Hash(password); }
    public override (bool Ok, bool Rehash) Verify(string? hash, string password) { Interlocked.Increment(ref _verify); return base.Verify(hash, password); }
    public override void BurnTime(string password) { Interlocked.Increment(ref _burn); base.BurnTime(password); }
}

/// <summary>Records the verb and table of every statement, and can fail one on demand. A request that does the same statements in the same order takes the same time.</summary>
public sealed class CommandLog : DbCommandInterceptor
{
    private readonly object _lock = new();
    private readonly List<string> _seen = [];
    private Func<string, bool>? _failWhen;
    public bool Recording { get; set; }

    public IReadOnlyList<string> Seen { get { lock (_lock) return _seen.ToList(); } }
    public void Clear() { lock (_lock) _seen.Clear(); }
    /// <summary>The next statement whose text matches throws, once.</summary>
    public void FailNext(Func<string, bool> when) => _failWhen = when;

    private void Note(string text)
    {
        var t = text.TrimStart();
        var verb = t.Split(' ', 2)[0].ToUpperInvariant();
        var table = System.Text.RegularExpressions.Regex.Match(t, "(?:FROM|INTO|UPDATE)\\s+\"?(\\w+)\"?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (Recording) lock (_lock) _seen.Add(verb + " " + (table.Success ? table.Groups[1].Value : "?"));
        if (_failWhen is { } f && f(t)) { _failWhen = null; throw new InvalidOperationException("injected failure"); }
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Note(command.CommandText); return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken); }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Note(command.CommandText); return base.ReaderExecutingAsync(command, eventData, result, cancellationToken); }
}
