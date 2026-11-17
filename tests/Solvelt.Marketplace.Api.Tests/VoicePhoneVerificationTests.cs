using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Only a phone number an account has PROVEN lets the voice line recognise a caller as that account (decision V-05, orchestrator review of slice 2).</summary>
public class VoicePhoneVerificationTests
{
    private const string Email = "jane@example.com", Number = "+15551234567";

    private static async Task<(VoiceScenario S, string Token)> Account(bool verified)
    {
        var s = new VoiceScenario(Number);
        var token = await s.K.VerifiedSession(Email);
        await s.SetPhone(Email, "(555) 123-4567", verified);
        return (s, token);
    }

    private static async Task<CallerLevel> CallerAfterCode(VoiceScenario s)
    {
        await s.Dial();
        await s.Tool("identify_caller");
        await s.EndStreamAfterGoodbye();
        await s.AfterStream();
        var (_, token) = await s.Type(s.LastCode());
        await s.Attach(token);
        return s.State().Level;
    }

    [Fact]
    public async Task A_profile_phone_nobody_proved_is_never_recognised_the_caller_is_a_guest()
    {
        var (s, _) = await Account(verified: false);
        using (s)
        {
            Assert.Equal(CallerLevel.Guest, await CallerAfterCode(s));         // they hold the number and typed the right code, but the account never proved it
            Assert.Null(s.State().AccountId);
        }
    }

    [Fact]
    public async Task A_proven_number_is_recognised()
    {
        var (s, _) = await Account(verified: true);
        using (s)
        {
            Assert.Equal(CallerLevel.Verified, await CallerAfterCode(s));
            Assert.NotNull(s.State().AccountId);
        }
    }

    [Fact]
    public async Task Changing_the_profile_phone_clears_the_proof_but_writing_the_same_number_another_way_keeps_it()
    {
        var (s, token) = await Account(verified: true);
        using (s)
        {
            var same = await s.K.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = "Jane Brown", phone = "555.123.4567" }, token);
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal(Number, (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).PhoneVerifiedE164);
            var other = await s.K.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = "Jane Brown", phone = "555-000-9999" }, token);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            var row = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email));
            Assert.Equal((null, null), (row.PhoneVerifiedE164, row.PhoneVerifiedAt));
            // and the old number now reaches nobody's account
            Assert.Equal(CallerLevel.Guest, await CallerAfterCode(s));
        }
    }

    [Fact]
    public async Task Clearing_the_profile_phone_clears_the_proof()
    {
        var (s, token) = await Account(verified: true);
        using (s)
        {
            Assert.Equal(HttpStatusCode.OK, (await s.K.Send(HttpMethod.Put, "/api/v1/account/profile", new { displayName = "Jane Brown", phone = "" }, token)).StatusCode);
            Assert.Null((await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).PhoneVerifiedE164);
        }
    }

    [Fact]
    public async Task Marking_a_number_verified_needs_it_on_the_profile_takes_it_over_from_another_account_and_one_account_holds_a_number()
    {
        var (s, _) = await Account(verified: true);
        using (s)
        {
            await s.K.VerifiedSession("new@example.com", name: "New Holder");
            await s.SetPhone("new@example.com", "(555) 123-4567");                      // the number was reassigned: the new holder types it too
            using var scope = s.F.Services.CreateScope();
            var verifier = scope.ServiceProvider.GetRequiredService<AccountPhoneVerifier>();
            var newId = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == "new@example.com"))).Id;
            Assert.False(await verifier.MarkVerifiedAsync(newId, "+15550000000", CancellationToken.None));   // a number the profile does not carry
            Assert.True(await verifier.MarkVerifiedAsync(newId, Number, CancellationToken.None));           // proven by the new holder's code
            var rows = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().Where(a => a.PhoneVerifiedE164 != null).ToListAsync());
            Assert.Equal(newId, Assert.Single(rows).Id);                                                    // the old account lost it
            // the database itself refuses two accounts holding one number
            await Assert.ThrowsAnyAsync<Exception>(() => s.K.Db(async db =>
            {
                await db.BuyerAccounts.Where(a => a.Email == Email).ExecuteUpdateAsync(x => x.SetProperty(a => a.PhoneVerifiedE164, Number));
                return 0;
            }));
        }
    }

    [Fact]
    public async Task A_deleted_or_redacted_account_keeps_no_proof()
    {
        var (s, token) = await Account(verified: true);
        using (s)
        {
            var id = (await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Email == Email))).Id;
            var now = s.F.Clock.GetUtcNow();
            await s.K.Db(async db => await db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(x => x.SetProperty(a => a.DeletedAt, now)));   // the account was deleted: the redaction job follows
            using (var scope = s.F.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<Solvelt.Marketplace.Api.Account.AccountService>().RedactAsync(id, scope.ServiceProvider.GetServices<Solvelt.Marketplace.Api.Account.IAccountEraser>(), CancellationToken.None));
            var row = await s.K.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
            Assert.Equal((null, null, null), (row.Phone, row.PhoneVerifiedE164, row.PhoneVerifiedAt));
            Assert.NotNull(token);
        }
    }
}
