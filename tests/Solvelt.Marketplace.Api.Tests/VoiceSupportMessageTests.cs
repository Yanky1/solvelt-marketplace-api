using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Messages a caller leaves for staff when no person can be reached (decision V-55): who may leave one, what is refused, the personal-data rules (eraser, export, retention) and the audited staff access. All on fakes.</summary>
public class VoiceSupportMessageTests
{
    private const string Email = "jane@example.com", Number = "+15551234567";

    private static async Task<(VoiceScenario S, Guid Jane)> AccountCall(Dictionary<string, string?>? more = null)
    {
        var cfg = new Dictionary<string, string?> { ["Voice:PhonePaymentProvider"] = "fake" };
        if (more is not null) foreach (var kv in more) cfg[kv.Key] = kv.Value;
        var s = new VoiceScenario(config: cfg);
        await s.SyncCatalog(); s.PortalShipping();
        await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified: true);
        return (s, (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id);
    }

    private static async Task Verify(VoiceScenario s)
    {
        await s.Tool("identify_caller"); await s.EndStreamAfterGoodbye(); await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode()); await s.Attach(token);
    }

    private static Task<List<VoiceSupportMessage>> Messages(VoiceScenario s) => s.K.Db(db => db.VoiceSupportMessages.AsNoTracking().OrderBy(m => m.Text).ToListAsync());

    [Fact]
    public async Task A_verified_account_caller_leaves_a_message_stored_with_the_account_and_the_verified_number()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await s.Dial(); await Verify(s);
            var r = await s.Tool("leave_support_message", new { message = "  Please call me about my   order\u0007 tomorrow.  " });
            Assert.Equal("saved", r["status"]!.GetValue<string>());
            var m = Assert.Single(await Messages(s));
            Assert.Equal(("Please call me about my order tomorrow.", jane, "new"), (m.Text, m.AccountId, m.Status));      // plain text, spaces and control characters cleaned
            Assert.False(string.IsNullOrEmpty(m.Phone));                                                                      // the number proven by the code in this call
            Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains("Please call me"));                                         // never logged
            Assert.DoesNotContain(s.F.Logs.Lines, l => l.Contains(Number));
        }
    }

    [Fact]
    public async Task A_guest_with_a_verified_number_leaves_one_with_the_number_only_and_an_unverified_caller_is_sent_to_verify()
    {
        using var s = new VoiceScenario(config: null);
        await s.SyncCatalog(); await s.Dial();
        Assert.Equal("verify_first", (await s.Tool("leave_support_message", new { message = "hello" }))["error"]!.GetValue<string>());
        Assert.Empty(await Messages(s));
        await Verify(s);                                                                                                   // a number nobody connected to an account: a guest with a verified phone
        Assert.Equal(CallerLevel.Guest, s.State().Level);
        Assert.Equal("saved", (await s.Tool("leave_support_message", new { message = "Where is my order?" }))["status"]!.GetValue<string>());
        var m = Assert.Single(await Messages(s));
        Assert.Null(m.AccountId); Assert.False(string.IsNullOrEmpty(m.Phone));
    }

    [Fact]
    public async Task The_server_refuses_while_a_person_can_be_reached_and_for_empty_long_card_like_and_too_many_messages()
    {
        var (s, _) = await AccountCall();
        using (s)
        {
            await s.Dial(); await Verify(s);
            var transfer = await s.Tool("transfer_to_person");                                                                // no support number: nobody can be reached, and the model is told to offer a message
            Assert.Equal("unavailable", transfer["status"]!.GetValue<string>());
            Assert.Contains("leave_support_message", transfer["instruction"]!.GetValue<string>());
            Assert.Equal("empty", (await s.Tool("leave_support_message", new { message = "   " }))["error"]!.GetValue<string>());
            Assert.Equal("empty", (await s.Tool("leave_support_message", new { }))["error"]!.GetValue<string>());
            Assert.Equal("too_long", (await s.Tool("leave_support_message", new { message = new string('x', LeaveSupportMessageTool.MaxChars + 1) }))["error"]!.GetValue<string>());
            Assert.Equal("not_this", (await s.Tool("leave_support_message", new { message = "my card is 4111 1111 1111 1111 please" }))["error"]!.GetValue<string>());
            Assert.Equal("not_this", (await s.Tool("leave_support_message", new { message = "my card is 4111-1111-1111-1111" }))["error"]!.GetValue<string>());
            Assert.Equal("saved", (await s.Tool("leave_support_message", new { message = "order 20261110 and call 5551234567 please" }))["status"]!.GetValue<string>());   // ordinary numbers are fine
            for (var i = 0; i < 2; i++) Assert.Equal("saved", (await s.Tool("leave_support_message", new { message = "more " + i }))["status"]!.GetValue<string>());
            Assert.Equal("too_many", (await s.Tool("leave_support_message", new { message = "and more" }))["error"]!.GetValue<string>());
            Assert.Equal(3, (await Messages(s)).Count);
        }
        var (t, _) = await AccountCall(new() { ["Voice:SupportNumber"] = "+15550001111" });                                     // a person IS reachable: no hours set means always open
        using (t)
        {
            await t.Dial(); await Verify(t);
            Assert.Equal("person_available", (await t.Tool("leave_support_message", new { message = "hello" }))["error"]!.GetValue<string>());
            Assert.Empty(await Messages(t));
        }
    }

    [Fact]
    public async Task The_account_eraser_deletes_the_accounts_messages_the_export_lists_them_and_the_retention_deletes_the_rest()
    {
        var (s, jane) = await AccountCall();
        using (s)
        {
            await s.Dial(); await Verify(s);
            await s.Tool("leave_support_message", new { message = "first message" });
            var other = Guid.NewGuid();
            await s.K.Db(async db => { db.VoiceSupportMessages.Add(new VoiceSupportMessage { Id = Guid.NewGuid(), AccountId = other, Text = "someone else's", CreatedAt = s.F.Clock.GetUtcNow() }); db.VoiceSupportMessages.Add(new VoiceSupportMessage { Id = Guid.NewGuid(), Phone = "+15550009999", Text = "number only", CreatedAt = s.F.Clock.GetUtcNow() }); await db.SaveChangesAsync(); return 0; });
            // the buyer's own data export lists theirs, and only theirs
            using (var scope = s.F.Services.CreateScope())
            {
                var exported = await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "support-messages").ExportAsync(jane, default);
                Assert.Contains("first message", JsonSerializer.Serialize(exported)); Assert.DoesNotContain("someone else", JsonSerializer.Serialize(exported));
            }
            // erasing the account deletes its messages and nothing else
            using (var scope = s.F.Services.CreateScope()) foreach (var eraser in scope.ServiceProvider.GetServices<IAccountEraser>().OfType<VoiceSupportMessageEraser>()) await eraser.EraseAsync(jane, default);
            Assert.Equal(["number only", "someone else's"], (await Messages(s)).Select(m => m.Text).ToArray());
            // the retention (90 days by default) removes the number-only message too, whatever happens
            s.F.Clock.Advance(TimeSpan.FromDays(91));
            using (var scope = s.F.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<VoiceCallStore>().PurgeAsync(default);
            Assert.Empty(await Messages(s));
        }
    }

    [Fact]
    public void The_registry_names_the_module_and_the_retention_setting_is_checked()
    {
        var module = Assert.Single(PiiRegistry.Modules, m => m.Name == "voice-support-messages");
        Assert.Equal((typeof(VoiceSupportMessage), typeof(VoiceSupportMessageEraser)), (Assert.Single(module.Entities), module.Eraser));
        Assert.Contains(VoiceConfig.Problems(new VoiceOptions { Enabled = true, SupportMessageRetentionDays = 0 }, new SignalWireOptions(), true), p => p.Contains("SupportMessageRetentionDays"));
        Assert.DoesNotContain(VoiceConfig.Problems(new VoiceOptions { Enabled = true }, new SignalWireOptions(), true), p => p.Contains("SupportMessageRetentionDays"));
    }

    // ---------- staff ----------

    private static async Task<(StaffKit S, string Admin, string Support, string Finance, Guid Id)> Staff()
    {
        var s = new StaffKit(VoiceFactory.Config());
        var (admin, _, _) = await s.Admin();
        var (support, _, _) = await s.Member(admin, "support@example.com", "support");
        var (finance, _, _) = await s.Member(admin, "finance@example.com", "finance");
        var id = Guid.NewGuid();
        await s.Db(async db =>
        {
            db.VoiceSupportMessages.Add(new VoiceSupportMessage { Id = id, AccountId = Guid.NewGuid(), Phone = "+15551234567", Text = "Please call me back about order MP-1.", CreatedAt = s.F.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
            return 0;
        });
        return (s, admin, support, finance, id);
    }

    private const string R = "ticket 12: answering the caller";

    [Fact]
    public async Task Staff_list_the_messages_with_the_number_masked_every_look_is_audited_and_a_stranger_sees_nothing()
    {
        var (s, admin, support, finance, id) = await Staff();
        using (s)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await s.Post("/api/v1/staff/voice/support-messages", new { reason = R })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Post("/api/v1/staff/voice/support-messages", new { reason = "x" }, support)).StatusCode);     // a reason is required
            var r = await s.Post("/api/v1/staff/voice/support-messages", new { reason = R }, support);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var text = await r.Content.ReadAsStringAsync();
            var m = JsonDocument.Parse(text).RootElement.GetProperty("messages").EnumerateArray().Single();
            Assert.Equal(("***4567", "Please call me back about order MP-1.", "new"), (m.GetProperty("phone").GetString(), m.GetProperty("text").GetString(), m.GetProperty("status").GetString()));
            Assert.DoesNotContain("+15551234567", text);
            Assert.Equal(HttpStatusCode.OK, (await s.Post("/api/v1/staff/voice/support-messages", new { reason = R, status = "all" }, finance)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/staff/voice/support-messages", new { reason = R, status = "open" }, admin)).StatusCode);
            var audit = (await s.Audit()).Where(a => a.Action == VoiceSupportMessageEndpoints.ListAction).ToList();
            Assert.Equal(2, audit.Count);
            Assert.All(audit, a => Assert.DoesNotContain("call me back", a.Detail ?? ""));                                   // the text is never copied into the audit
            Assert.All(audit, a => Assert.Equal(R, a.Reason));
        }
    }

    [Fact]
    public async Task The_full_number_needs_the_reveal_permission_and_marking_handled_is_audited_and_happens_once()
    {
        var (s, admin, support, finance, id) = await Staff();
        using (s)
        {
            var reveal = $"/api/v1/staff/voice/support-messages/{id}/reveal";
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Post(reveal, new { reason = R }, finance)).StatusCode);          // finance may not reveal
            var ok = await s.Post(reveal, new { reason = R }, support);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("+15551234567", JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("phone").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"/api/v1/staff/voice/support-messages/{Guid.NewGuid()}/reveal", new { reason = R }, support)).StatusCode);
            var handled = $"/api/v1/staff/voice/support-messages/{id}/handled";
            Assert.Equal(HttpStatusCode.NoContent, (await s.Post(handled, new { reason = R }, support)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await s.Post(handled, new { reason = R }, support)).StatusCode);
            var row = await s.Db(db => db.VoiceSupportMessages.AsNoTracking().SingleAsync(x => x.Id == id));
            Assert.Equal(("handled", true), (row.Status, row.HandledBy is not null && row.HandledAt is not null));
            var actions = (await s.Audit()).Select(a => a.Action).ToList();
            Assert.Contains(VoiceSupportMessageEndpoints.RevealAction, actions); Assert.Contains(VoiceSupportMessageEndpoints.HandledAction, actions);
            Assert.Empty(JsonDocument.Parse(await (await s.Post("/api/v1/staff/voice/support-messages", new { reason = R }, admin)).Content.ReadAsStringAsync()).RootElement.GetProperty("messages").EnumerateArray());   // handled ones leave the default list
        }
    }
}
