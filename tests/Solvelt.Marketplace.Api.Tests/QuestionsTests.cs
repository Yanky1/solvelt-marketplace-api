using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Reviews;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Product questions (M6, portal C1; decision M6-06).</summary>
public sealed class QuestionsTests : IDisposable
{
    private readonly AuthKit _k = new();
    private bool _synced;
    public void Dispose() => _k.Dispose();

    private static Guid Listing(string slug = "challah-classic") => CatalogKit.ListingId(slug);

    private async Task<(HttpStatusCode Status, JsonElement Body)> Ask(string? token, object body)
    {
        if (!_synced) { await CatalogKit.Sync(_k.F); _synced = true; }
        var r = await _k.Send(HttpMethod.Post, "/api/v1/questions", body, token);
        var s = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, s.Length == 0 ? default : JsonDocument.Parse(s).RootElement.Clone());
    }

    private IEnumerable<JsonElement> Sent() => _k.F.Portal.VerifiedCalls.Where(c => c.PathAndQuery == $"/api/internal/v1/products/{Listing()}/questions").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone());

    [Fact]
    public async Task A_question_is_stored_sent_once_with_our_id_and_only_a_first_name_and_initial_and_the_buyer_sees_its_state()
    {
        var t = await _k.VerifiedSession("q1@example.com", null, "Dana Levi");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Ask(null, new { listingId = Listing(), text = "Is it kosher?" })).Status);
        var (st, b) = await Ask(t, new { listingId = Listing(), text = "  Is it microwave safe?  " });
        Assert.Equal(HttpStatusCode.Accepted, st);
        var id = b.GetProperty("id").GetString()!;
        Assert.Equal("sending", b.GetProperty("status").GetString());
        while (await _k.Dispatch() > 0) { }
        var sent = Assert.Single(Sent());
        Assert.Equal((id, "Is it microwave safe?", "Dana L."), (sent.GetProperty("questionId").GetString(), sent.GetProperty("text").GetString(), sent.GetProperty("buyerDisplayName").GetString()));
        Assert.False(sent.TryGetProperty("email", out _) || sent.TryGetProperty("accountId", out _));
        var q = await _k.Db(db => db.ProductQuestions.AsNoTracking().SingleAsync());
        Assert.Equal((QuestionStatus.Sent, true), (q.Status, q.SentAt is not null));
        while (await _k.Dispatch() > 0) { }
        Assert.Single(Sent());                                                                       // nothing is sent twice
        var mine = JsonDocument.Parse(await (await _k.Get("/api/v1/account/questions", t)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("sent", mine.GetProperty("questions")[0].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("call me 718-555-0199")]
    [InlineData("see https://example.com")]
    [InlineData("mail me at someone@example.com")]
    public async Task The_text_is_checked_before_anything_is_stored(string text)
    {
        var t = await _k.VerifiedSession($"q2-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Ask(t, new { listingId = Listing(), text })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Ask(t, new { listingId = Listing(), text = new string('x', 1001) })).Status);
        Assert.Equal(0, await _k.Db(db => db.ProductQuestions.CountAsync()));
    }

    [Fact]
    public async Task The_question_throttle_holds_under_parallel_requests_of_one_buyer()
    {
        var t = await _k.VerifiedSession("q3p@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Ask(t, new { listingId = Guid.NewGuid(), text = "Warm-up: syncs the catalog before the parallel requests (the helper does it lazily)" })).Status);
        for (var round = 0; round < 2; round++)
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Ask(t, new { listingId = Listing(), text = $"Parallel question {round}-{i}" })));
            var statuses = string.Join(",", answers.Select(a => (int)a.Status));
            Assert.True((round == 0 ? QuestionEndpoints.PerListingPerDay : 0) == answers.Count(a => a.Status == HttpStatusCode.Accepted), $"the first wave fills the three places, the second finds none; statuses: {statuses}");
            Assert.True(answers.Where(a => a.Status != HttpStatusCode.Accepted).All(a => a.Status == HttpStatusCode.TooManyRequests), $"the others are refused with 429; statuses: {statuses}");
        }
        Assert.Equal(QuestionEndpoints.PerListingPerDay, await _k.Db(db => db.ProductQuestions.CountAsync()));
    }

    [Fact]
    public async Task An_unknown_or_demo_listing_is_404_and_a_buyer_is_throttled_per_listing_and_per_day()
    {
        var t = await _k.VerifiedSession("q3@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Ask(t, new { listingId = Guid.NewGuid(), text = "Hello?" })).Status);
        for (var i = 0; i < QuestionEndpoints.PerListingPerDay; i++) Assert.Equal(HttpStatusCode.Accepted, (await Ask(t, new { listingId = Listing(), text = "Question " + i })).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Ask(t, new { listingId = Listing(), text = "One more" })).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await Ask(t, new { listingId = Listing("babka-chocolate"), text = "A different item" })).Status);   // another listing is a separate budget
        _k.F.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        t = (await (await _k.Post("/api/v1/auth/sign-in", new { email = "q3@example.com", password = AuthKit.Password })).Content.ReadFromJsonAsyncToken());
        Assert.Equal(HttpStatusCode.Accepted, (await Ask(t, new { listingId = Listing(), text = "Tomorrow" })).Status);
    }

    [Fact]
    public async Task A_listing_the_portal_no_longer_shows_ends_the_question_as_rejected_and_is_not_retried()
    {
        var t = await _k.VerifiedSession("q4@example.com");
        _k.F.Portal.RespondAlways(c => c.PathAndQuery.EndsWith("/questions") ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"code\":\"not_found\"}", System.Text.Encoding.UTF8, "application/json") } : new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.Accepted, (await Ask(t, new { listingId = Listing(), text = "Still there?" })).Status);
        while (await _k.Dispatch() > 0) { }
        var q = await _k.Db(db => db.ProductQuestions.AsNoTracking().SingleAsync());
        Assert.Equal((QuestionStatus.Rejected, "listing_unavailable"), (q.Status, q.RejectedReason));
        Assert.Single(Sent());
    }

    [Fact]
    public async Task Deleting_the_account_deletes_its_questions_and_the_export_holds_them()
    {
        var t = await _k.VerifiedSession("q5@example.com");
        var other = await _k.VerifiedSession("q5b@example.com");
        await Ask(t, new { listingId = Listing(), text = "My own words" });
        await Ask(other, new { listingId = Listing(), text = "Someone else's words" });
        var id = (await _k.Account("q5@example.com")).Id;
        using (var scope = _k.F.Services.CreateScope())
        {
            var json = JsonSerializer.Serialize(await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "questions").ExportAsync(id, default));
            Assert.Contains("My own words", json);
            Assert.DoesNotContain("Someone else", json);
            await scope.ServiceProvider.GetServices<IAccountEraser>().OfType<QuestionsAccountEraser>().Single().EraseAsync(id, default);
        }
        Assert.Equal(1, await _k.Db(db => db.ProductQuestions.CountAsync()));
        while (await _k.Dispatch() > 0) { }
        Assert.Single(Sent());                                                                       // the deleted question was never sent
    }
}

internal static class TokenReader
{
    public static async Task<string> ReadFromJsonAsyncToken(this HttpContent c) => (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<AuthKit.SignInResponse>(c))!.Token;
}
