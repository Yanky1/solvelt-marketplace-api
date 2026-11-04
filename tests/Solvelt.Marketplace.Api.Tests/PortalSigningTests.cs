using Solvelt.Marketplace.Api.Portal;

namespace Solvelt.Marketplace.Api.Tests;

public class PortalSigningTests
{
    // Portal test vector (docs/ORDERS.md §3.1, pinned in CONTRACT.md §1.1).
    private const string Secret = "test-secret", Ts = "1700000000", Nonce = "abc123", Path = "/api/internal/v1/orders";
    private static readonly byte[] Body = "{\"a\":1}"u8.ToArray();

    [Fact]
    public void Portal_test_vector_body_hash()
        => Assert.Equal("015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862", PortalSigning.BodyHash(Body));

    [Fact]
    public void Portal_test_vector_signature()
    {
        var canonical = PortalSigning.Canonical(Ts, Nonce, "POST", Path, Body);
        Assert.Equal("v1=8a1a05a14201d38431d0c9816b6b127ff9193117e81e0530895a25934cbaadea", PortalSigning.Sign(Secret, canonical));
    }

    [Fact]
    public void Canonical_is_five_newline_separated_lines_with_upper_case_method()
    {
        var lines = PortalSigning.Canonical(Ts, Nonce, "post", Path + "?a=b%3Ac", Body).Split('\n');
        Assert.Equal([Ts, Nonce, "POST", Path + "?a=b%3Ac", PortalSigning.BodyHash(Body)], lines);
    }

    [Fact]
    public void Empty_body_hash_is_sha256_of_nothing()
        => Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", PortalSigning.BodyHash([]));

    [Fact]
    public void Nonces_are_32_lowercase_hex_and_unique()
    {
        var a = PortalSigning.NewNonce();
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.NotEqual(a, PortalSigning.NewNonce());
    }
}
