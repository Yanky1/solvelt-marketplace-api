using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>
/// A software authenticator for the tests: makes the registration and assertion responses a browser would hand over (ES256, "none" attestation), from the options the server
/// sent, and lets a test misbehave in exactly one way (wrong origin, wrong relying party, unverified user, stale counter, foreign key, ...).
/// </summary>
public sealed class VirtualAuthenticator
{
    public const string DefaultOrigin = "http://localhost:3000";
    public const string DefaultRpId = "localhost";

    public ECDsa Key { get; set; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] CredentialId { get; set; } = RandomNumberGenerator.GetBytes(32);
    public uint Counter { get; set; }
    public byte[]? UserHandle { get; set; }
    public string Origin { get; set; } = DefaultOrigin;
    public string RpId { get; set; } = DefaultRpId;
    public bool UserVerified { get; set; } = true;
    public bool IncludeUserHandle { get; set; } = true;
    /// <summary>When set, the next response signs this challenge instead of the one in the options.</summary>
    public string? ChallengeOverride { get; set; }
    public string ClientDataType { get; set; } = "";

    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private string ClientData(string type, string challenge)
        => JsonSerializer.Serialize(new { type = string.IsNullOrEmpty(ClientDataType) ? type : ClientDataType, challenge = ChallengeOverride ?? challenge, origin = Origin, crossOrigin = false });

    private byte[] CoseKey()
    {
        var p = Key.ExportParameters(false);
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(5);
        w.WriteInt32(1); w.WriteInt32(2);        // kty: EC2
        w.WriteInt32(3); w.WriteInt32(-7);       // alg: ES256
        w.WriteInt32(-1); w.WriteInt32(1);       // crv: P-256
        w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
        w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
        w.WriteEndMap();
        return w.Encode();
    }

    private byte[] AuthData(bool attested)
    {
        var flags = (byte)(0x01 | (UserVerified ? 0x04 : 0) | (attested ? 0x40 : 0));
        var data = new List<byte>();
        data.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        data.Add(flags);
        data.AddRange(BitConverter.GetBytes(Counter).Reverse());
        if (attested)
        {
            data.AddRange(new byte[16]);                                          // AAGUID: none
            data.AddRange(BitConverter.GetBytes((ushort)CredentialId.Length).Reverse());
            data.AddRange(CredentialId);
            data.AddRange(CoseKey());
        }
        return data.ToArray();
    }

    /// <summary>The credential a browser returns from <c>navigator.credentials.create()</c> for these creation options.</summary>
    public JsonElement Register(JsonElement creationOptions)
    {
        UserHandle = FromB64(creationOptions.GetProperty("user").GetProperty("id").GetString()!);
        var challenge = creationOptions.GetProperty("challenge").GetString()!;
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(3);
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(AuthData(attested: true));
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteEndMap();
        var node = new JsonObject
        {
            ["id"] = B64(CredentialId), ["rawId"] = B64(CredentialId), ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["attestationObject"] = B64(w.Encode()),
                ["clientDataJSON"] = B64(Encoding.UTF8.GetBytes(ClientData("webauthn.create", challenge))),
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
        ChallengeOverride = null;
        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    /// <summary>The credential a browser returns from <c>navigator.credentials.get()</c> for these request options. The counter goes up by one unless the test set it.</summary>
    public JsonElement Assert(JsonElement requestOptions, bool bumpCounter = true)
    {
        if (bumpCounter) Counter++;
        var challenge = requestOptions.GetProperty("challenge").GetString()!;
        var authData = AuthData(attested: false);
        var clientData = Encoding.UTF8.GetBytes(ClientData("webauthn.get", challenge));
        var signature = Key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var response = new JsonObject
        {
            ["authenticatorData"] = B64(authData), ["clientDataJSON"] = B64(clientData), ["signature"] = B64(signature),
        };
        if (IncludeUserHandle && UserHandle is not null) response["userHandle"] = B64(UserHandle);
        var node = new JsonObject { ["id"] = B64(CredentialId), ["rawId"] = B64(CredentialId), ["type"] = "public-key", ["response"] = response, ["clientExtensionResults"] = new JsonObject() };
        ChallengeOverride = null;
        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }
}
