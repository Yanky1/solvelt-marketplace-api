using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public class AccountTests
{
    private const string Email = "jane@example.com";
    private const string Pw = AuthKit.Password;
    private static readonly HttpMethod Put = HttpMethod.Put, Delete = HttpMethod.Delete, Get = HttpMethod.Get, Post = HttpMethod.Post;

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static object Us(string name = "Jane Brown", bool? isDefault = null, string line1 = "1 Main St") => new
    {
        fullName = name, line1, line2 = "Apt 2", city = "New York", region = "NY", postalCode = "10001", country = "us", phone = "+1 212 555 0100", isDefault,
    };

    private static async Task<Guid> AddAddress(AuthKit k, string token, object body)
    {
        var r = await k.Send(Post, "/api/v1/account/addresses", body, token);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    private static async Task<List<(Guid Id, bool IsDefault)>> Book(AuthKit k, string token)
    {
        var r = await k.Send(Get, "/api/v1/account/addresses", null, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).EnumerateArray().Select(a => (a.GetProperty("id").GetGuid(), a.GetProperty("isDefault").GetBoolean())).ToList();
    }

    // ---------- profile ----------

    [Fact]
    public async Task The_profile_shows_the_buyer_and_nothing_private_leaks_into_caches()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var r = await k.Send(Get, "/api/v1/account/profile", null, token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cc = r.Headers.CacheControl; Assert.True(cc is { NoStore: true, Private: true }, cc?.ToString());
        var j = await Json(r);
        Assert.Equal(new[] { "displayName", "email", "id", "phone" }, j.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal((Email, "Jane Brown", JsonValueKind.Null), (j.GetProperty("email").GetString(), j.GetProperty("displayName").GetString(), j.GetProperty("phone").ValueKind));
        Assert.Equal((await k.Account(Email)).Id, j.GetProperty("id").GetGuid());
        foreach (var (m, path) in new[] { (Get, "/api/v1/account/profile"), (Put, "/api/v1/account/profile"), (Get, "/api/v1/account/addresses"), (Post, "/api/v1/account/addresses"), (Post, "/api/v1/account/password"), (Post, "/api/v1/account/delete") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await k.Send(m, path, new { }, null)).StatusCode);
    }

    [Fact]
    public async Task Updating_the_profile_trims_clears_and_validates()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var ok = await k.Send(Put, "/api/v1/account/profile", new { displayName = "  Jane Q. Brown ", phone = " +1 (212) 555-0100 " }, token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var j = await Json(ok);
        Assert.Equal(("Jane Q. Brown", "+1 (212) 555-0100"), (j.GetProperty("displayName").GetString(), j.GetProperty("phone").GetString()));
        Assert.Equal(JsonValueKind.Null, (await Json(await k.Send(Put, "/api/v1/account/profile", new { displayName = "Jane", phone = "  " }, token))).GetProperty("phone").ValueKind);
        foreach (var (body, field) in new (object, string)[]
        {
            (new { displayName = "", phone = (string?)null }, "displayName"), (new { displayName = new string('n', 61), phone = (string?)null }, "displayName"),
            (new { displayName = "Jane", phone = "abc" }, "phone"), (new { displayName = "Jane", phone = "12" }, "phone"), (new { displayName = "Jane", phone = new string('1', 31) }, "phone"),
            (new { displayName = "Jane", phone = "555<script>" }, "phone"),
        })
        {
            var r = await k.Send(Put, "/api/v1/account/profile", body, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.True((await Json(r)).GetProperty("errors").TryGetProperty(field, out _), field);
        }
        Assert.Equal("Jane", (await Json(await k.Send(Get, "/api/v1/account/profile", null, token))).GetProperty("displayName").GetString());   // failures changed nothing
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(Put, "/api/v1/account/profile", new { phone = "+1 212 555 0100" }, token)).StatusCode);   // name missing
    }

    // ---------- address book ----------

    [Fact]
    public async Task The_first_address_is_the_default_and_a_later_one_only_when_asked()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var r = await k.Send(Post, "/api/v1/account/addresses", Us(), token);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var a = await Json(r);
        Assert.Equal(new[] { "city", "country", "fullName", "id", "isDefault", "lat", "line1", "line2", "lng", "phone", "postalCode", "region" }, a.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(a.GetProperty("isDefault").GetBoolean());
        Assert.Equal(("US", "Jane Brown", "10001"), (a.GetProperty("country").GetString(), a.GetProperty("fullName").GetString(), a.GetProperty("postalCode").GetString()));   // country upper-cased
        var second = await AddAddress(k, token, Us("Second"));
        var third = await AddAddress(k, token, Us("Third", isDefault: true));
        var book = await Book(k, token);
        Assert.Equal(3, book.Count);
        Assert.Equal(third, Assert.Single(book, b => b.IsDefault).Id);
        Assert.Equal(third, book[0].Id);                                                       // the default is listed first
        Assert.Contains(book, b => b.Id == second && !b.IsDefault);
    }

    [Fact]
    public async Task Making_an_address_the_default_unsets_the_others_and_updating_can_do_the_same()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var a = await AddAddress(k, token, Us("A")); var b = await AddAddress(k, token, Us("B")); var c = await AddAddress(k, token, Us("C"));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, $"/api/v1/account/addresses/{b}/default", null, token)).StatusCode);
        Assert.Equal(b, Assert.Single(await Book(k, token), x => x.IsDefault).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, $"/api/v1/account/addresses/{b}/default", null, token)).StatusCode);   // again: still fine
        var upd = await k.Send(Put, $"/api/v1/account/addresses/{c}", Us("C changed", isDefault: true), token);
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        Assert.True((await Json(upd)).GetProperty("isDefault").GetBoolean());
        Assert.Equal(c, Assert.Single(await Book(k, token), x => x.IsDefault).Id);
        var keep = await k.Send(Put, $"/api/v1/account/addresses/{c}", Us("C again", isDefault: false), token);   // false on the default is ignored: there is always one
        Assert.True((await Json(keep)).GetProperty("isDefault").GetBoolean());
        Assert.Equal(c, Assert.Single(await Book(k, token), x => x.IsDefault).Id);
        var plain = await Json(await k.Send(Put, $"/api/v1/account/addresses/{a}", Us("A edited"), token));      // no isDefault: left as it was
        Assert.False(plain.GetProperty("isDefault").GetBoolean());
        Assert.Equal("A edited", plain.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task Deleting_the_default_promotes_the_oldest_remaining_and_deleting_the_last_leaves_none()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var a = await AddAddress(k, token, Us("A")); k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        var b = await AddAddress(k, token, Us("B")); k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        var c = await AddAddress(k, token, Us("C", isDefault: true));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Delete, $"/api/v1/account/addresses/{b}", null, token)).StatusCode);   // not the default: nothing moves
        Assert.Equal(c, Assert.Single(await Book(k, token), x => x.IsDefault).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Delete, $"/api/v1/account/addresses/{c}", null, token)).StatusCode);   // the default: the oldest remaining takes over
        Assert.Equal(a, Assert.Single(await Book(k, token), x => x.IsDefault).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Delete, $"/api/v1/account/addresses/{a}", null, token)).StatusCode);
        Assert.Empty(await Book(k, token));
        Assert.Equal(HttpStatusCode.NotFound, (await k.Send(Delete, $"/api/v1/account/addresses/{a}", null, token)).StatusCode);
        var again = await AddAddress(k, token, Us("New"));                                         // and the next first address is the default again
        Assert.Equal(again, Assert.Single(await Book(k, token), x => x.IsDefault).Id);
    }

    [Fact]
    public async Task Another_buyers_address_and_unknown_or_malformed_ids_are_all_404_and_nothing_changes()
    {
        using var k = new AuthKit();
        var mine = await k.VerifiedSession(Email);
        var theirs = await k.VerifiedSession("other@example.com");
        var theirId = await AddAddress(k, theirs, Us("Other Person"));
        var myId = await AddAddress(k, mine, Us("Mine"));
        foreach (var id in new[] { theirId.ToString(), Guid.NewGuid().ToString() })
        {
            foreach (var (m, suffix, body) in new (HttpMethod, string, object?)[] { (Get, "", null), (Put, "", Us("Hijacked")), (Delete, "", null), (Post, "/default", null) })
                Assert.Equal(HttpStatusCode.NotFound, (await k.Send(m, $"/api/v1/account/addresses/{id}{suffix}", body, mine)).StatusCode);
        }
        foreach (var bad in new[] { "abc", "..", "%2e%2e", "1", "00000000", "not-a-guid", "'", new string('a', 300), "{" + theirId + "}x" })
            foreach (var m in new[] { Get, Put, Delete, Post })
            {
                var path = m == Post ? $"/api/v1/account/addresses/{bad}/default" : $"/api/v1/account/addresses/{bad}";
                var status = (await k.Send(m, path, m == Put ? Us("x") : null, mine)).StatusCode;
                Assert.True(status == HttpStatusCode.NotFound || status == HttpStatusCode.BadRequest, $"{m} {bad} -> {status}");
            }
        var theirBook = await Book(k, theirs);
        Assert.Equal("Other Person", (await Json(await k.Send(Get, $"/api/v1/account/addresses/{theirId}", null, theirs))).GetProperty("fullName").GetString());
        Assert.Single(theirBook);
        Assert.Single(await Book(k, mine));
        Assert.Equal(myId, (await Book(k, mine)).Single().Id);
    }

    [Theory]
    [InlineData("fullName", "")] [InlineData("fullName", "   ")] [InlineData("fullName", "Name\u0007Bell")]
    [InlineData("line1", "")] [InlineData("line1", "Street\n2")]
    [InlineData("city", "")] [InlineData("country", "")] [InlineData("country", "ZZ")] [InlineData("country", "USA")] [InlineData("country", "u")] [InlineData("country", "12")]
    [InlineData("postalCode", "")] [InlineData("postalCode", "1234")] [InlineData("postalCode", "ABCDE")] [InlineData("postalCode", "10001-12")]
    [InlineData("region", "")] [InlineData("region", "New York")] [InlineData("region", "N1")]
    [InlineData("phone", "abc")] [InlineData("phone", "12")]
    public async Task A_bad_us_address_is_refused_with_the_field_to_fix(string field, string value)
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var body = new Dictionary<string, object?> { ["fullName"] = "Jane", ["line1"] = "1 Main St", ["city"] = "New York", ["region"] = "NY", ["postalCode"] = "10001", ["country"] = "US", ["phone"] = "+1 212 555 0100" };
        body[field] = value;
        var r = await k.Send(Post, "/api/v1/account/addresses", body, token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var errors = (await Json(r)).GetProperty("errors");
        Assert.True(errors.TryGetProperty(field, out _), $"{field}: {errors}");
        Assert.Empty(await Book(k, token));
    }

    [Theory]
    [InlineData("GB", "SW1A 1AA", "", true)]
    [InlineData("gb", "sw1a 1aa", null, true)]
    [InlineData("IL", "9195001", "", true)]
    [InlineData("CA", "K1A 0B1", "ON", true)]
    [InlineData("DE", "10115", "", true)]
    [InlineData("JP", "100-0001", "Tokyo", true)]
    [InlineData("US", "10001-1234", "ny", true)]
    [InlineData("IE", "D02 X285", "", true)]
    [InlineData("SG", "018956", "", true)]
    [InlineData("GB", "-bad", "", false)]
    [InlineData("GB", "bad-", "", false)]
    [InlineData("GB", "A", "", true)]
    [InlineData("GB", "TOOLONGTOOLONGTOOLONG1", "", false)]
    public async Task International_addresses_need_a_sensible_postal_code_and_no_state(string country, string postal, string? region, bool ok)
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var r = await k.Send(Post, "/api/v1/account/addresses", new { fullName = "Yael Cohen", line1 = "5 Some Road", city = "Somewhere", region, postalCode = postal, country }, token);
        Assert.Equal(ok ? HttpStatusCode.Created : HttpStatusCode.UnprocessableEntity, r.StatusCode);
        if (ok) Assert.Equal(country.ToUpperInvariant(), (await Json(r)).GetProperty("country").GetString());
    }

    [Fact]
    public async Task Coordinates_are_optional_come_as_a_pair_and_stay_in_range()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        object Body(double? lat, double? lng) => new { fullName = "Jane", line1 = "1 Main St", city = "New York", region = "NY", postalCode = "10001", country = "US", lat, lng };
        var ok = await k.Send(Post, "/api/v1/account/addresses", Body(40.7128, -74.006), token);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var j = await Json(ok);
        Assert.Equal((40.7128, -74.006), (j.GetProperty("lat").GetDouble(), j.GetProperty("lng").GetDouble()));
        var without = await Json(await k.Send(Post, "/api/v1/account/addresses", Body(null, null), token));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (without.GetProperty("lat").ValueKind, without.GetProperty("lng").ValueKind));
        foreach (var (lat, lng) in new (double?, double?)[] { (40.0, null), (null, 10.0), (91, 0), (-91, 0), (0, 181), (0, -181) })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(Post, "/api/v1/account/addresses", Body(lat, lng), token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, "/api/v1/account/addresses", Body(90, 180), token)).StatusCode);   // the edges are fine
    }

    [Fact]
    public async Task A_buyer_keeps_at_most_twenty_addresses()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        for (var i = 0; i < 20; i++) await AddAddress(k, token, Us($"Address {i}"));
        var r = await k.Send(Post, "/api/v1/account/addresses", Us("One too many"), token);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("too_many_addresses", (await Json(r)).GetProperty("code").GetString());
        var other = await k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, "/api/v1/account/addresses", Us("Someone else"), other)).StatusCode);   // the cap is per buyer
        var first = (await Book(k, token))[0].Id;
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Delete, $"/api/v1/account/addresses/{first}", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await k.Send(Post, "/api/v1/account/addresses", Us("Now it fits"), token)).StatusCode);
    }

    // ---------- exactly one default, under parallel requests ----------

    private static async Task<int> Defaults(AuthKit k, Guid accountId) => await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == accountId && a.IsDefault));

    [Fact]
    public async Task Parallel_make_default_requests_leave_exactly_one_default_and_every_request_succeeds()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var accountId = (await k.Account(Email)).Id;
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await AddAddress(k, token, Us($"A{i}")));
        for (var round = 0; round < 4; round++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => k.Send(Post, $"/api/v1/account/addresses/{ids[i % 5]}/default", null, token))));
            Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
            Assert.Equal(1, await Defaults(k, accountId));
        }
        Assert.Equal(5, await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == accountId)));
    }

    [Fact]
    public async Task Parallel_first_addresses_make_one_default_and_parallel_defaults_on_create_make_one()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var accountId = (await k.Account(Email)).Id;
        var created = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => k.Send(Post, "/api/v1/account/addresses", Us($"First {i}"), token))));
        Assert.All(created, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(1, await Defaults(k, accountId));
        var asked = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => k.Send(Post, "/api/v1/account/addresses", Us($"Wants default {i}", isDefault: true), token))));
        Assert.All(asked, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(1, await Defaults(k, accountId));
        Assert.Equal(18, await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == accountId)));
    }

    [Fact]
    public async Task A_storm_of_mixed_address_writes_always_ends_with_exactly_one_default_when_any_address_is_left()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var accountId = (await k.Account(Email)).Id;
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await AddAddress(k, token, Us($"S{i}")));
        for (var round = 0; round < 3; round++)
        {
            var ops = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
            {
                var id = ids[(i * 7) % ids.Count];
                return (i % 5) switch
                {
                    0 => await k.Send(Post, $"/api/v1/account/addresses/{id}/default", null, token),
                    1 => await k.Send(Delete, $"/api/v1/account/addresses/{id}", null, token),
                    2 => await k.Send(Put, $"/api/v1/account/addresses/{id}", Us($"E{i}", isDefault: true), token),
                    3 => await k.Send(Post, "/api/v1/account/addresses", Us($"N{i}", isDefault: i % 2 == 0), token),
                    _ => await k.Send(Get, "/api/v1/account/addresses", null, token),
                };
            })).ToList();
            var results = await Task.WhenAll(ops);
            Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.Created or HttpStatusCode.NotFound or HttpStatusCode.Conflict, r.StatusCode.ToString()));
            var total = await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == accountId));
            Assert.Equal(total > 0 ? 1 : 0, await Defaults(k, accountId));
            ids = (await Book(k, token)).Select(b => b.Id).ToList();
            while (ids.Count < 6) ids.Add(await AddAddress(k, token, Us("Refill")));
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_default()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        var accountId = (await k.Account(Email)).Id;
        var now = k.F.Clock.GetUtcNow();
        BuyerAddress A(bool isDefault) => new() { Id = Guid.NewGuid(), AccountId = accountId, FullName = "x", Line1 = "x", City = "x", PostalCode = "1", Country = "GB", IsDefault = isDefault, CreatedAt = now, UpdatedAt = now };
        await k.Db(async db => { db.BuyerAddresses.AddRange(A(true), A(false), A(false)); await db.SaveChangesAsync(); return 0; });   // many non-default are fine
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => k.Db(async db => { db.BuyerAddresses.Add(A(true)); await db.SaveChangesAsync(); return 0; }));
        Assert.True(AccountService.IsUniqueViolation(ex));
        // another buyer may have a default of their own
        await k.VerifiedSession("other@example.com");
        var other = (await k.Account("other@example.com")).Id;
        await k.Db(async db => { var a = A(true); a.AccountId = other; db.BuyerAddresses.Add(a); await db.SaveChangesAsync(); return 0; });
    }

    // ---------- password change ----------

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_ends_other_sessions_keeps_this_one_and_mails_the_owner()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        var here = await k.VerifiedSession(Email);
        var elsewhere = (await (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).Content.ReadFromJsonAsync<AuthKit.SignInResponse>())!.Token;
        var r = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = "a brand new password" }, here);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", here)).StatusCode);                  // this device stays signed in
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", elsewhere)).StatusCode);   // every other one is out
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = "a brand new password" })).StatusCode);
        var mail = (await k.Mail(Email)).Last();
        Assert.Equal("Your password was changed", mail.Subject);
        Assert.Contains("/forgot-password", mail.TextBody);
        Assert.DoesNotContain("a brand new password", mail.TextBody);
        var queued = await k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Payload != null && m.Payload.Contains("password-changed")).ToListAsync());
        Assert.DoesNotContain("@", string.Concat(queued.Select(q => q.Payload)));                                // ids only
    }

    [Fact]
    public async Task A_session_created_while_the_password_changes_does_not_survive_it()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        var here = await k.VerifiedSession(Email);
        var account = await k.Account(Email);
        // the row a concurrent sign-in or rotation leaves: not revoked, made under the epoch from before the change
        var raced = Tokens.New();
        var now = k.F.Clock.GetUtcNow();
        await k.Db(async db =>
        {
            db.BuyerSessions.Add(new BuyerSession
            {
                Id = Guid.NewGuid(), AccountId = account.Id, TokenHash = Tokens.Hash(raced), CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now, Epoch = account.SessionEpoch,
                ExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30),
            });
            await db.SaveChangesAsync(); return 0;
        });
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", raced)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = "a brand new password" }, here)).StatusCode);
        await k.Db(db => db.BuyerSessions.Where(s => s.TokenHash == Tokens.Hash(raced)).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)null)));   // as if the change's UPDATE had missed it
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", raced)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", here)).StatusCode);                  // the session that asked lives on
        Assert.Equal(account.SessionEpoch + 1, (await k.Account(Email)).SessionEpoch);
    }

    [Fact]
    public async Task Every_sign_in_and_account_response_is_private_and_not_stored_even_the_refusals()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var cases = new (HttpMethod, string, object?, string?)[]
        {
            (Get, "/api/v1/account/me", null, token),                                   // 200
            (Get, "/api/v1/account/me", null, null),                                    // 401, no session
            (Get, "/api/v1/account/profile", null, token),
            (Get, "/api/v1/account/addresses", null, token),
            (Post, "/api/v1/auth/sign-in", new { email = Email, password = "wrong password!!" }, null),   // 401
            (Post, "/api/v1/auth/sign-up", new { email = "x", password = "y", displayName = "" }, null),  // 422
            (Post, "/api/v1/auth/forgot-password", new { email = "nobody@example.com" }, null),           // 204
            (Post, "/api/v1/auth/verify-email", new { token = "nope", password = "whatever" }, null),     // 400
            (Post, "/api/v1/auth/sign-out", null, token),                               // 204
        };
        foreach (var (m, path, body, t) in cases)
        {
            var r = await k.Send(m, path, body, t);
            var cc = r.Headers.CacheControl;
            Assert.True(cc is { NoStore: true, Private: true }, $"{m} {path} -> {(int)r.StatusCode} Cache-Control: {cc}");
        }
        var broken = new HttpRequestMessage(Post, "/api/v1/auth/sign-in") { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") };
        var rb = await k.Http.SendAsync(broken);
        Assert.True(rb.Headers.CacheControl is { NoStore: true, Private: true }, $"malformed body -> {(int)rb.StatusCode}");
    }

    [Fact]
    public async Task Changing_the_password_needs_a_sign_in_within_ten_minutes_and_reauthenticating_restores_it()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        k.F.Clock.Advance(TimeSpan.FromMinutes(10));
        var edge = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Wrong(), newPassword = "a brand new password" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, edge.StatusCode);                                             // exactly 10 minutes: still recent (the password is what is wrong)
        k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        var stale = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = "a brand new password" }, token);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal("reauthentication_required", (await Json(stale)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/auth/reauthenticate", new { password = Pw }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = "a brand new password" }, token)).StatusCode);
    }

    private static string Wrong() => "wrong password!!";

    [Fact]
    public async Task A_wrong_current_password_a_weak_new_one_and_missing_fields_change_nothing()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        var wrong = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Wrong(), newPassword = "a brand new password" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", (await Json(wrong)).GetProperty("code").GetString());
        foreach (var weak in new[] { "short", Email, "JANE@example.com", new string('x', 129) })
        {
            var r = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = weak }, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.True((await Json(r)).GetProperty("errors").TryGetProperty("newPassword", out _));
        }
        foreach (var body in new object[] { new { }, new { currentPassword = Pw }, new { newPassword = "a brand new password" }, new { currentPassword = "", newPassword = "" } })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(Post, "/api/v1/account/password", body, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Send(Post, "/api/v1/account/password", new { currentPassword = new string('x', 129), newPassword = "a brand new password" }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);   // the old password still works
    }

    [Fact]
    public async Task Guessing_the_current_password_meets_the_same_gate_as_sign_in()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        var token = await k.VerifiedSession(Email);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await k.Send(Post, "/api/v1/account/password", new { currentPassword = Wrong(), newPassword = "a brand new password" }, token)).StatusCode);
        Assert.Equal(6, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        var blocked = await k.Send(Post, "/api/v1/account/password", new { currentPassword = Pw, newPassword = "a brand new password" }, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);                                      // even the right password, while delayed
        Assert.Equal("1", blocked.Headers.GetValues("Retry-After").Single());
    }

    // ---------- account deletion and redaction ----------

    private sealed class RecordingEraser : IAccountEraser
    {
        public List<Guid> Erased { get; } = [];
        public bool Throw { get; set; }
        public Task EraseAsync(Guid accountId, CancellationToken ct) { if (Throw) throw new InvalidOperationException("eraser broke"); Erased.Add(accountId); return Task.CompletedTask; }
    }

    private static async Task<int> DispatchAll(AuthKit k) { var n = 0; int d; while ((d = await k.Dispatch()) > 0) n += d; return n; }

    [Fact]
    public async Task Deleting_the_account_locks_it_out_at_once_and_the_redaction_job_then_erases_the_personal_data()
    {
        var eraser = new RecordingEraser();
        using var k = new AuthKit(null, s => s.AddSingleton<IAccountEraser>(eraser));
        var token = await k.VerifiedSession(Email);
        var other = await k.VerifiedSession("other@example.com");
        await k.Send(Put, "/api/v1/account/profile", new { displayName = "Jane Brown", phone = "+1 212 555 0100" }, token);
        await AddAddress(k, token, Us()); await AddAddress(k, token, Us("Second"));
        await k.Post("/api/v1/auth/forgot-password", new { email = Email });
        var before = await k.Account(Email);
        Assert.Equal(HttpStatusCode.OK, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);   // a second session

        var r = await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        // at once: nothing works any more, and the redaction is queued in the same transaction
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Get("/api/v1/account/me", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        var soft = await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == before.Id));
        Assert.NotNull(soft.DeletedAt);
        Assert.Null(soft.PasswordHash);
        Assert.Null(soft.Email);                                                                               // the address is free at once (L2)
        Assert.Equal("Jane Brown", soft.DisplayName);                                                          // the rest waits for the job
        Assert.Null(soft.RedactedAt);
        var queued = await k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "account.redact"));
        Assert.Equal($"redact:{before.Id:N}", queued.DedupeKey);
        Assert.DoesNotContain("@", queued.Payload);                                                            // the id only

        await DispatchAll(k);
        var gone = await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == before.Id));
        Assert.Equal((null, "Deleted buyer", null, null), (gone.Email, gone.DisplayName, gone.Phone, gone.PasswordHash));
        Assert.NotNull(gone.RedactedAt);
        Assert.NotEqual(before.RelayEmail, gone.RelayEmail);                                                   // the relay address is retired (L1) ...
        Assert.Equal($"deleted-{before.Id:N}@relay.invalid", gone.RelayEmail);                                // ... to a tombstone that is unique and unroutable
        Assert.Equal(before.CreatedAt, gone.CreatedAt);
        Assert.Equal(0, await k.Db(db => db.BuyerAddresses.CountAsync(a => a.AccountId == before.Id)));
        Assert.Equal(0, await k.Db(db => db.EmailTokens.CountAsync(t => t.AccountId == before.Id)));
        Assert.Equal(0, await k.Db(db => db.BuyerSessions.CountAsync(s => s.AccountId == before.Id)));
        Assert.Equal(before.Id, Assert.Single(eraser.Erased));                                                 // other modules were asked, once
        // nobody else was touched
        Assert.Equal(HttpStatusCode.OK, (await k.Get("/api/v1/account/me", other)).StatusCode);
        Assert.Equal("other@example.com", (await k.Account("other@example.com")).Email);
        // the address can be used again, as a brand new account
        Assert.Equal(HttpStatusCode.NoContent, (await k.SignUp(Email, "a different password", "Jane Again")).StatusCode);
        var fresh = await k.Account(Email);
        Assert.NotEqual(before.Id, fresh.Id);
        Assert.NotEqual(before.RelayEmail, fresh.RelayEmail);
    }

    [Fact]
    public async Task Deleting_needs_a_recent_sign_in_and_the_right_password_and_goes_through_the_gate()
    {
        using var k = new AuthKit(new() { ["Auth:SignInIpFailuresPerHour"] = "100000" });
        var token = await k.VerifiedSession(Email);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await k.Send(Post, "/api/v1/account/delete", new { }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Send(Post, "/api/v1/account/delete", new { password = Wrong() }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Send(Post, "/api/v1/account/delete", new { password = new string('x', 129) }, token)).StatusCode);
        Assert.Null((await k.Account(Email)).DeletedAt);
        k.F.Clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal("reauthentication_required", (await Json(stale)).GetProperty("code").GetString());
        Assert.Null((await k.Account(Email)).DeletedAt);
        for (var i = 0; i < 8; i++) await k.Send(Post, "/api/v1/auth/reauthenticate", new { password = Wrong() }, token);
        var blocked = await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);                                            // still not recent
        k.F.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/auth/reauthenticate", new { password = Pw }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token)).StatusCode);   // a second delete: the session is gone
    }

    [Fact]
    public async Task A_failing_eraser_makes_the_redaction_retry_and_the_account_stays_locked_out_meanwhile()
    {
        var eraser = new RecordingEraser { Throw = true };
        using var k = new AuthKit(null, s => s.AddSingleton<IAccountEraser>(eraser));
        var token = await k.VerifiedSession(Email);
        var id = (await k.Account(Email)).Id;
        await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token);
        await k.Dispatch();
        var msg = await k.Db(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "account.redact"));
        Assert.Equal(("pending", "InvalidOperationException"), (msg.Status, msg.LastError));                  // the type only
        var half = await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.NotNull(half.DeletedAt);
        Assert.Null(half.RedactedAt);
        Assert.Equal("Jane Brown", half.DisplayName);                                                          // not erased yet: the eraser must succeed first
        Assert.Equal(HttpStatusCode.Unauthorized, (await k.Post("/api/v1/auth/sign-in", new { email = Email, password = Pw })).StatusCode);
        eraser.Throw = false;
        k.F.Clock.Advance(TimeSpan.FromMinutes(2));
        await DispatchAll(k);
        var done = await k.Db(db => db.BuyerAccounts.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Null(done.Email);
        Assert.NotNull(done.RedactedAt);
    }

    [Fact]
    public async Task A_deleted_account_whose_redaction_message_was_lost_is_queued_again_by_the_sweeper_once_an_hour()
    {
        using var k = new AuthKit();
        await k.VerifiedSession(Email);
        var id = (await k.Account(Email)).Id;
        var now = k.F.Clock.GetUtcNow();
        await k.Db(db => db.BuyerAccounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, (DateTimeOffset?)now.AddHours(-2))));   // deleted two hours ago, no message
        async Task<AuthMaintenance.Result> Purge() { using var s = k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<AuthMaintenance>().PurgeAsync(); }
        Assert.Equal(1, (await Purge()).Redactions);
        Assert.Equal(0, (await Purge()).Redactions);                                                           // same hour: not twice
        await DispatchAll(k);
        Assert.NotNull((await k.Account2(id)).RedactedAt);
        Assert.Equal(0, (await Purge()).Redactions);                                                           // redacted: nothing left to queue
        k.F.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, (await Purge()).Redactions);
    }

    [Fact]
    public async Task A_recently_deleted_account_is_not_swept_before_its_own_message_had_an_hour()
    {
        using var k = new AuthKit();
        var token = await k.VerifiedSession(Email);
        await k.Send(Post, "/api/v1/account/delete", new { password = Pw }, token);
        using var s = k.F.Services.CreateScope();
        Assert.Equal(0, (await s.ServiceProvider.GetRequiredService<AuthMaintenance>().PurgeAsync()).Redactions);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"accountId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("null")]
    public async Task A_malformed_redaction_work_item_is_dead_lettered(string payload)
    {
        using var k = new AuthKit();
        Guid id;
        using (var scope = k.F.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            var m = scope.ServiceProvider.GetRequiredService<OutboxWriter>().Enqueue("account.redact", new { });
            m.Payload = payload; id = m.Id;
            await db.SaveChangesAsync();
        }
        await k.Dispatch();
        var row = await k.Db(db => db.OutboxMessages.AsNoTracking().FirstAsync(m => m.Id == id));
        Assert.Equal(("dead", "invalid_payload"), (row.Status, row.LastError));
    }

    [Fact]
    public async Task Redacting_an_unknown_or_live_or_already_redacted_account_does_nothing()
    {
        var eraser = new RecordingEraser();
        using var k = new AuthKit(null, s => s.AddSingleton<IAccountEraser>(eraser));
        await k.VerifiedSession(Email);
        var id = (await k.Account(Email)).Id;
        using var scope = k.F.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        Assert.False(await accounts.RedactAsync(Guid.NewGuid(), [eraser], default));
        Assert.False(await accounts.RedactAsync(id, [eraser], default));                                       // a live account is never redacted
        Assert.Equal(Email, (await k.Account(Email)).Email);
        Assert.Empty(eraser.Erased);
    }

    [Fact]
    public async Task The_sensitive_account_endpoints_share_the_strict_per_client_limit()
    {
        using var k = new AuthKit(new() { ["RateLimits:AuthPerMinute"] = "40", ["RateLimits:PublicPerMinute"] = "10000" });
        var token = await k.VerifiedSession(Email);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 45; i++) statuses.Add((await k.Send(Post, "/api/v1/account/password", new { currentPassword = "x", newPassword = "y" }, token)).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await k.Send(Post, "/api/v1/account/delete", new { password = "x" }, token)).StatusCode);   // the bucket is shared
        Assert.Equal(HttpStatusCode.OK, (await k.Send(Get, "/api/v1/account/profile", null, token)).StatusCode);                                   // reads use the generous one
    }
}
