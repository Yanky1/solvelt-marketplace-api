using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>One scripted phone call through the real app: the fake SignalWire (webhooks and stream), the scripted fake OpenAI Realtime server, an in-memory SMS sender, and the fixture catalog.</summary>
public sealed class VoiceScenario : IDisposable
{
    public FakeRealtime Fake { get; } = new();
    public AuthKit K { get; }
    public TestFactory F => K.F;
    public HttpClient C { get; }
    public InMemorySmsSender Sms => (InMemorySmsSender)F.Services.GetRequiredService<ISmsSender>();
    public FakePhone? Phone { get; private set; }
    public string Sid { get; }
    public string From { get; }
    private int _calls;

    public VoiceScenario(string from = "+15551234567", Dictionary<string, string?>? config = null, string? sid = null)
    {
        From = from; Sid = sid ?? "CA-" + Guid.NewGuid().ToString("N")[..10];
        K = new AuthKit(VoiceFactory.Config(config), s => { s.RemoveAll<IRealtimeConnector>(); s.AddSingleton<IRealtimeConnector>(Fake); });
        C = K.F.CreateClient();
    }

    public async Task SyncCatalog() => await CatalogKit.Sync(F);

    public static async Task<(string Token, XElement Xml)> StreamFrom(HttpResponseMessage r)
    {
        var xml = XDocument.Parse(await r.Content.ReadAsStringAsync()).Root!;
        var url = xml.Descendants("Stream").SingleOrDefault()?.Attribute("url")?.Value;
        return (url is null ? "" : System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["token"]!, xml);
    }

    /// <summary>The inbound webhook, the stream and the start event; the model session is open when this returns.</summary>
    public async Task<FakePhone> Dial()
    {
        var (token, _) = await StreamFrom(await VoiceFactory.Hook(C, VoiceEndpoints.InboundPath, Sid, From));
        return await Attach(token);
    }

    public async Task<FakePhone> Attach(string token)
    {
        Phone?.Dispose();
        var before = Fake.Of("session.update").Count();
        Phone = await FakePhone.Connect(F, token);
        await Phone.Start();
        await FakeRealtime.Poll(() => Fake.Of("session.update").Count() > before ? "x" : null, 5);
        return Phone;
    }

    /// <summary>The model calls a tool; returns the server's answer to it (what the model would read).</summary>
    public async Task<JsonNode> Tool(string name, object? args = null)
    {
        var id = "call_" + Interlocked.Increment(ref _calls);
        await Fake.Send(new { type = "response.done", response = new { output = new object[] { new { type = "function_call", name, call_id = id, arguments = System.Text.Json.JsonSerializer.Serialize(args ?? new { }) } } } });
        var item = await Fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["call_id"]?.GetValue<string>() == id);
        return JsonNode.Parse(item["item"]!["output"]!.GetValue<string>())!;
    }

    /// <summary>The model's last words before a keypad step have played: the phone acknowledges the mark and the stream ends.</summary>
    public async Task EndStreamAfterGoodbye()
    {
        await Fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>() } });
        var mark = await Phone!.Wait("mark");
        await Phone.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = mark["mark"]!["name"]!.GetValue<string>() } });
        Assert.True(await Phone.WaitClosed());
    }

    public async Task<XElement> AfterStream() => XDocument.Parse(await (await VoiceFactory.Hook(C, VoiceEndpoints.AfterStreamPath, Sid, From)).Content.ReadAsStringAsync()).Root!;

    public async Task<(XElement Xml, string Token)> Type(string digits)
    {
        var r = await VoiceFactory.Hook(C, VoiceEndpoints.KeypadPath, Sid, From, extra: new() { ["Digits"] = digits });
        var (token, xml) = await StreamFrom(r);
        return (xml, token);
    }

    /// <summary>The six-digit code of the latest text message sent to <paramref name="to"/> (what the caller would read on their phone).</summary>
    public string LastCode(string? to = null)
        => Regex.Match(Sms.Sent.Last(m => to is null || m.To == to).Body, @"\b(\d{6})\b").Groups[1].Value;

    public async Task SetPhone(string email, string phone) => await K.Db(async db => await db.BuyerAccounts.Where(a => a.Email == email).ExecuteUpdateAsync(s => s.SetProperty(a => a.Phone, phone)));
    public Task<VoiceCall> Row() => Task.Run(async () => { using var s = F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.AsNoTracking().SingleAsync(c => c.CallSid == Sid); });
    public VoiceCallState State() => F.Services.GetRequiredService<VoiceStateStore>().Find(Row().Result.Id)!;

    public void Dispose() { Phone?.Dispose(); Fake.Dispose(); K.Dispose(); }
}
