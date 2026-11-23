using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// W3 follow-up (decision W3-02, "not in this slice"): an account made by PHONE has no password, so the web's delete (which asks for one) was closed to it. It now deletes with a recent sign-in
/// AND a code texted for exactly that purpose to its own proven number, with the same semantics as the password delete (soft delete, every session ends, erasure queued) and an audit row.
/// </summary>
public sealed class PhoneAccountDeleteTests : IDisposable
{
    // these tests text one number more often than the phone line's per-number cap (3 an hour) allows; the cap itself is tested in PhoneWebSignInTests
    private readonly AuthKit _k = new(new() { ["Voice:MaxSmsPerNumberPerHour"] = "50" });
    private const string Number = "+17185550142";
    public void Dispose() => _k.Dispose();

    private InMemorySmsSender Sms
    {
        get
        {
            _k.F.Services.GetRequiredService<Solvelt.Marketplace.Api.Auth.PhoneSmsDispatcher>().DrainAsync().GetAwaiter().GetResult();
            return (InMemorySmsSender)_k.F.Services.GetRequiredService<ISmsSender>();
        }
    }
    private string LastCode(string to = Number) => Regex.Match(Sms.Sent.Last(s => s.To == to).Body, @"\b(\d{6})\b").Groups[1].Value;
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    private static async Task<string> Code(HttpResponseMessage r) => (await Json(r)).GetProperty("code").GetString()!;

    private async Task<Guid> PhoneAccount(string number = Number, string name = "Dana Levi")
    {
        using var scope = _k.F.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<PhoneAccountService>().CreateAsync(name, number, default))!.Value;
    }

    private async Task<string> SignedInByPhone(string number = Number)
    {
        var start = await _k.Post("/api/v1/auth/phone/start", new { phone = number });
        var id = (await Json(start)).GetProperty("challengeId").GetGuid();
        var r = await _k.Post("/api/v1/auth/phone/verify", new { challengeId = id, code = LastCode(number) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("token").GetString()!;
    }

    private async Task<Guid> StartDelete(string token)
    {
        var r = await _k.Post("/api/v1/account/delete/phone/start", null, token);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await Json(r)).GetProperty("challengeId").GetGuid();
    }

    private Task<HttpResponseMessage> Delete(Guid id, string code, string token) => _k.Post("/api/v1/account/delete/phone", new { challengeId = id, code }, token);

    [Fact]
    public async Task An_account_made_by_phone_deletes_itself_with_a_delete_code_and_everything_the_password_delete_does_happens()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        var second = await SignedInByPhone();                                                          // a second session ends too
        var challenge = await StartDelete(token);
        var text = Sms.Sent.Last();
        Assert.Equal(Number, text.To);                                                                 // the account's own number: the body names none
        Assert.Contains("DELETE", text.Body);                                                          // the text says what the code does

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(challenge, LastCode(), token)).StatusCode);
        var account = await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.NotNull(account.DeletedAt);
        Assert.Equal(0, await _k.Db(db => db.BuyerSessions.CountAsync(s => s.AccountId == id && s.RevokedAt == null)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Get("/api/v1/account/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Get("/api/v1/account/me", second)).StatusCode);
        Assert.Equal(1, await _k.Db(db => db.OutboxMessages.CountAsync(m => m.DedupeKey == $"redact:{id:N}")));   // erasure queued in the same transaction, as for the password delete

        var audit = await _k.Db(db => db.StaffAudit.AsNoTracking().SingleAsync(a => a.Action == "buyer.self_delete"));
        Assert.Equal(("buyer", id.ToString("D"), "buyer"), (audit.TargetType, audit.TargetId, audit.ActorRole));
        Assert.Null(audit.ActorId);
        Assert.Contains("\"method\":\"phone_code\"", audit.Detail);
        Assert.DoesNotContain("555", audit.Detail + audit.Reason);                                     // ids and the method only, never the number

        using (var scope = _k.F.Services.CreateScope())                                                // the erasure job runs: the audit row is ids only and survives it
            Assert.True(await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Account.AccountService>().RedactAsync(id, scope.ServiceProvider.GetServices<Solvelt.Marketplace.Api.Account.IAccountEraser>(), default));
        Assert.NotNull((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id))).RedactedAt);
        Assert.Equal(audit.Id, (await _k.Db(db => db.StaffAudit.AsNoTracking().SingleAsync(a => a.Action == "buyer.self_delete" && a.TargetId == id.ToString("D")))).Id);

        var before = Sms.Sent.Count;
        Assert.Equal(HttpStatusCode.Accepted, (await _k.Post("/api/v1/auth/phone/start", new { phone = Number })).StatusCode);   // the same answer as for any number ...
        Assert.Equal(before, Sms.Sent.Count);                                                          // ... but a deleted account is texted no sign-in code
    }

    [Fact]
    public async Task Sign_in_step_up_and_delete_codes_never_stand_in_for_each_other()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();

        var stepUp = (await Json(await _k.Post("/api/v1/auth/reauthenticate/phone/start", null, token))).GetProperty("challengeId").GetGuid();
        Assert.Equal("code_expired", await Code(await Delete(stepUp, LastCode(), token)));            // a step-up code deletes nothing
        var signIn = (await Json(await _k.Post("/api/v1/auth/phone/start", new { phone = Number }))).GetProperty("challengeId").GetGuid();
        Assert.Equal("code_expired", await Code(await Delete(signIn, LastCode(), token)));            // nor does a sign-in code

        var del = await StartDelete(token);
        var code = LastCode();
        Assert.Equal("code_expired", await Code(await _k.Post("/api/v1/auth/reauthenticate/phone", new { challengeId = del, code }, token)));   // a delete code is no step-up
        Assert.Equal("code_expired", await Code(await _k.Post("/api/v1/auth/phone/verify", new { challengeId = del, code })));                  // nor a sign-in
        Assert.Null((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id))).DeletedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(del, code, token)).StatusCode);          // the tries above were on other purposes: this code still works once
    }

    [Fact]
    public async Task Both_the_start_and_the_delete_need_a_recent_sign_in_and_a_phone_step_up_makes_it_recent()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        var del = await StartDelete(token);
        var code = LastCode();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30));                        // past the 10-minute recent sign-in (the code itself would also be near its end)
        var stale = await Delete(del, code, token);
        Assert.Equal((HttpStatusCode.Forbidden, "reauthentication_required"), (stale.StatusCode, await Code(stale)));
        var texts = Sms.Sent.Count;
        var staleStart = await _k.Post("/api/v1/account/delete/phone/start", null, token);
        Assert.Equal((HttpStatusCode.Forbidden, "reauthentication_required"), (staleStart.StatusCode, await Code(staleStart)));
        Assert.Equal(texts, Sms.Sent.Count);                                                           // nothing texted for a stale session
        Assert.Null((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id))).DeletedAt);

        var stepUp = (await Json(await _k.Post("/api/v1/auth/reauthenticate/phone/start", null, token))).GetProperty("challengeId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/auth/reauthenticate/phone", new { challengeId = stepUp, code = LastCode() }, token)).StatusCode);
        var fresh = await StartDelete(token);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(fresh, LastCode(), token)).StatusCode);
    }

    [Fact]
    public async Task A_delete_code_works_only_for_the_account_it_was_sent_to_and_five_wrong_tries_end_it()
    {
        var mine = await PhoneAccount();
        var theirs = await PhoneAccount("+17185550143", "Other Person");
        var myToken = await SignedInByPhone();
        var theirToken = await SignedInByPhone("+17185550143");
        var theirChallenge = await StartDelete(theirToken);
        var r = await Delete(theirChallenge, LastCode("+17185550143"), myToken);                       // their code, my session
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_code"), (r.StatusCode, await Code(r)));
        Assert.Null((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == mine))).DeletedAt);
        Assert.Null((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == theirs))).DeletedAt);

        var del = await StartDelete(myToken);
        var right = LastCode();
        var wrong = right == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++) Assert.Equal("invalid_code", await Code(await Delete(del, wrong, myToken)));
        Assert.Equal("code_expired", await Code(await Delete(del, wrong, myToken)));                   // the fifth wrong try locks it ...
        Assert.Equal("code_expired", await Code(await Delete(del, right, myToken)));                   // ... even for the right digits
        Assert.Null((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == mine))).DeletedAt);
    }

    [Fact]
    public async Task An_account_with_a_password_deletes_with_it_never_by_text_message_and_that_delete_is_audited_too()
    {
        var token = await _k.VerifiedSession("pw@example.com");
        var start = await _k.Post("/api/v1/account/delete/phone/start", null, token);
        Assert.Equal((HttpStatusCode.Conflict, "no_phone"), (start.StatusCode, await Code(start)));
        Assert.Empty(Sms.Sent);
        var r = await Delete(Guid.NewGuid(), "123456", token);
        Assert.Equal((HttpStatusCode.Conflict, "password_required"), (r.StatusCode, await Code(r)));

        Assert.Equal(HttpStatusCode.NoContent, (await _k.Post("/api/v1/account/delete", new { password = AuthKit.Password }, token)).StatusCode);
        var audit = await _k.Db(db => db.StaffAudit.AsNoTracking().SingleAsync(a => a.Action == "buyer.self_delete"));
        Assert.Contains("\"method\":\"password\"", audit.Detail);
        Assert.DoesNotContain("pw@example.com", audit.Detail + audit.Reason);
    }

    [Fact]
    public async Task A_password_set_after_the_code_was_sent_stops_the_delete_by_code()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        var del = await StartDelete(token);
        await _k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, "set-in-between")));
        var r = await Delete(del, LastCode(), token);
        Assert.Equal((HttpStatusCode.Conflict, "password_required"), (r.StatusCode, await Code(r)));
        Assert.Null((await _k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id))).DeletedAt);
        Assert.Equal(0, await _k.Db(db => db.StaffAudit.CountAsync(a => a.Action == "buyer.self_delete")));
    }

    [Fact]
    public async Task Parallel_right_answers_delete_once_and_write_one_audit_row()
    {
        var id = await PhoneAccount();
        var token = await SignedInByPhone();
        var del = await StartDelete(token);
        var code = LastCode();
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Delete(del, code, token)));
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.NoContent);
        Assert.Equal(1, await _k.Db(db => db.StaffAudit.CountAsync(a => a.Action == "buyer.self_delete" && a.TargetId == id.ToString("D"))));
    }
}
