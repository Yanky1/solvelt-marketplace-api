using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal.Webhooks;
using Solvelt.Marketplace.Api.Reviews;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Reviews and ratings (M6, decisions M6-04..): eligibility, one per line, aggregates under parallel writes, votes, reports, moderation, replies, erasure.</summary>
public sealed class ReviewsTests : IDisposable
{
    private readonly AuthKit _k = new();
    private bool _synced;
    private int _n;
    public void Dispose() => _k.Dispose();

    private static Guid Listing(string slug = "challah-classic") => CatalogKit.ListingId(slug);
    private async Task Ready() { if (_synced) return; await CatalogKit.Sync(_k.F); _synced = true; }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(HttpMethod m, string path, string? token, object? body = null)
    {
        await Ready();
        var r = await _k.Send(m, path, body, token);
        var s = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, s.Length == 0 ? default : JsonDocument.Parse(s).RootElement.Clone());
    }

    private async Task<(string Token, Guid Account)> Buyer(string email, string name = "Jane Brown")
    {
        var t = await _k.VerifiedSession(email, null, name);
        return (t, (await _k.Account(email)).Id);
    }

    /// <summary>A delivered order line of <paramref name="account"/> for the listing (seeded directly: the order flow has its own tests).</summary>
    private async Task<Guid> Delivered(Guid account, string slug = "challah-classic", string status = FulfilmentStatus.Delivered, bool demo = false)
    {
        await Ready();
        var listing = await _k.Db(db => db.CatalogListings.AsNoTracking().FirstAsync(l => l.Id == Listing(slug)));
        var order = new MarketplaceOrder { Id = Guid.NewGuid(), Number = "MP-RV-" + Guid.NewGuid().ToString("N")[..10], AccountId = account, IsDemo = demo, Status = "paid", CreatedAt = _k.F.Clock.GetUtcNow() };
        var group = new OrderGroup { Id = Guid.NewGuid(), OrderId = order.Id, SellerId = listing.SellerId, BrandId = listing.BrandId, SellerName = "S", BrandName = "B" };
        var line = new OrderLine { Id = Guid.NewGuid(), OrderId = order.Id, GroupId = group.Id, MarketplaceLineId = order.Number + "-1", ListingId = listing.Id, VariantId = Guid.NewGuid(), SellerId = listing.SellerId, BrandId = listing.BrandId, Title = listing.Title, VariantName = "Small", Quantity = 1 };
        var state = new SellerOrderState { GroupId = group.Id, OrderId = order.Id, Status = status, DeliveredAt = _k.F.Clock.GetUtcNow(), UpdatedAt = _k.F.Clock.GetUtcNow() };
        await _k.Db(async db => { db.Orders.Add(order); db.OrderGroups.Add(group); db.OrderLines.Add(line); db.SellerOrderStates.Add(state); await db.SaveChangesAsync(); return 0; });
        return line.Id;
    }

    private async Task<string> Login(string email)
    {
        var r = await _k.Post("/api/v1/auth/sign-in", new { email, password = AuthKit.Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<AuthKit.SignInResponse>(r.Content))!.Token;
    }

    private async Task<Guid> Booking(Guid account, string status = BookingStatus.Completed, string slug = "challah-classic")
    {
        var b = new Solvelt.Marketplace.Api.Data.Booking { Id = Guid.NewGuid(), AccountId = account, ListingId = Listing(slug), Status = status, MarketplaceBookingId = "BK-" + Guid.NewGuid().ToString("N")[..10], Number = "B-" + ++_n, IdempotencyKey = Guid.NewGuid().ToString("N"), StartAt = _k.F.Clock.GetUtcNow(), EndAt = _k.F.Clock.GetUtcNow() };
        await _k.Db(async db => { db.Bookings.Add(b); await db.SaveChangesAsync(); return 0; });
        return b.Id;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Write(string t, Guid line, int rating = 5, string text = "Wonderful, fresh and still warm.", string kind = "order_line")
        => Call(HttpMethod.Post, "/api/v1/reviews", t, new { kind, targetId = line, rating, text });

    private async Task<Guid> Reviewed(string t, Guid line, int rating = 5)
    {
        var (st, b) = await Write(t, line, rating);
        Assert.Equal(HttpStatusCode.Created, st);
        return b.GetProperty("id").GetGuid();
    }

    private async Task<RatingAggregate?> Agg(string scope, Guid id) => await _k.Db(db => db.RatingAggregates.AsNoTracking().FirstOrDefaultAsync(a => a.Scope == scope && a.ScopeId == id));

    // ---------------------------------------------------------------- eligibility, create, text rules
    [Fact]
    public async Task Only_a_delivered_line_or_a_completed_booking_of_the_buyer_can_be_reviewed_once_and_never_in_demo_data()
    {
        var (a, ida) = await Buyer("r1a@example.com", "Dana Levi");
        var (b, idb) = await Buyer("r1b@example.com");
        var mine = await Delivered(ida);
        Assert.Equal(HttpStatusCode.NotFound, (await Write(b, mine)).Status);                                         // another buyer's line is a 404
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Write(a, await Delivered(ida, status: FulfilmentStatus.Shipped))).Status);   // not delivered yet
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Write(a, await Delivered(ida, demo: true))).Status);   // demo data is never reviewed
        var (st, body) = await Write(a, mine, 4);
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.Equal("Dana L.", body.GetProperty("displayName").GetString());                                          // first name and last initial only
        Assert.Equal(HttpStatusCode.Conflict, (await Write(a, mine, 5)).Status);                                       // one per line

        var done = await Booking(ida);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Write(a, await Booking(ida, BookingStatus.Confirmed), kind: "booking")).Status);
        Assert.Equal(HttpStatusCode.Created, (await Write(a, done, kind: "booking")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Write(b, done, kind: "booking")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Write(a, mine, kind: "nonsense")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Write("", mine)).Status);
    }

    [Fact]
    public async Task Parallel_reviews_of_the_same_line_are_one_review_and_one_set_of_figures()
    {
        var (a, ida) = await Buyer("r2@example.com");
        var line = await Delivered(ida);
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Write(a, line, 5)));
        Assert.Equal(1, rs.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(rs.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        Assert.Equal(1, await _k.Db(db => db.Reviews.CountAsync()));
        Assert.Equal(1, (await Agg("listing", Listing()))!.Count);
    }

    [Theory]
    [InlineData(0, "Fine.")]
    [InlineData(6, "Fine.")]
    [InlineData(5, "   ")]
    [InlineData(5, "Call me on 718-555-0199 for a discount")]
    [InlineData(5, "see https://example.com/deal")]
    [InlineData(5, "write to me at someone@example.com")]
    [InlineData(5, "this was shit")]
    public async Task The_rating_and_the_text_are_checked(int rating, string text)
    {
        var (a, ida) = await Buyer($"r3-{++_n}@example.com");
        var (st, _) = await Write(a, await Delivered(ida), rating, text);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, st);
        Assert.Equal(0, await _k.Db(db => db.Reviews.CountAsync()));
    }

    // ---------------------------------------------------------------- aggregates
    [Fact]
    public async Task The_figures_of_the_listing_the_seller_and_the_brand_follow_creation_edits_and_parallel_writers()
    {
        var buyers = new List<(string T, Guid Id)>();
        for (var i = 0; i < 6; i++) buyers.Add(await Buyer($"r4-{i}@example.com"));
        var lines = new List<Guid>();
        foreach (var b in buyers) lines.Add(await Delivered(b.Id));
        var ratings = new[] { 5, 4, 4, 3, 1, 5 };
        var created = await Task.WhenAll(buyers.Select((b, i) => Write(b.T, lines[i], ratings[i])));
        Assert.All(created, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        var l = await _k.Db(db => db.CatalogListings.AsNoTracking().FirstAsync(x => x.Id == Listing()));
        foreach (var (scope, id) in new[] { ("listing", l.Id), ("seller", l.SellerId), ("brand", l.BrandId) })
        {
            var a = (await Agg(scope, id))!;
            Assert.Equal((6, 22L, "1,0,1,2,2"), (a.Count, a.Sum, string.Join(",", a.Stars1, a.Stars2, a.Stars3, a.Stars4, a.Stars5)));
        }
        // edits in parallel: 5 -> 2 (buyer 0) and 1 -> 4 (buyer 4)
        var edits = await Task.WhenAll(Call(HttpMethod.Patch, $"/api/v1/reviews/{created[0].Body.GetProperty("id").GetGuid()}", buyers[0].T, new { rating = 2, text = "On reflection, only fine." }),
                                       Call(HttpMethod.Patch, $"/api/v1/reviews/{created[4].Body.GetProperty("id").GetGuid()}", buyers[4].T, new { rating = 4, text = "It got better." }));
        Assert.All(edits, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        var after = (await Agg("listing", l.Id))!;
        Assert.Equal((6, 22L, "0,1,1,3,1"), (after.Count, after.Sum, string.Join(",", after.Stars1, after.Stars2, after.Stars3, after.Stars4, after.Stars5)));
        var (_, listJson) = await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={l.Id}", null);
        Assert.Equal(6, listJson.GetProperty("summary").GetProperty("count").GetInt32());
        Assert.Equal(3.7, listJson.GetProperty("summary").GetProperty("average").GetDouble());
        // the figures equal what the rows say
        var rows = await _k.Db(db => db.Reviews.AsNoTracking().ToListAsync());
        Assert.Equal((rows.Count, rows.Sum(r => (long)r.Rating)), (after.Count, after.Sum));
    }

    [Fact]
    public async Task A_review_can_be_edited_for_thirty_days_by_its_author_only_and_a_change_is_sent_to_the_portal()
    {
        var (a, ida) = await Buyer("r5a@example.com");
        var (b, _) = await Buyer("r5b@example.com");
        var id = await Reviewed(a, await Delivered(ida), 3);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Patch, $"/api/v1/reviews/{id}", b, new { rating = 1, text = "Hijacked." })).Status);
        _k.F.Clock.Advance(TimeSpan.FromDays(10));
        a = await Login("r5a@example.com");   // the session of ten days ago has expired
        var (st, body) = await Call(HttpMethod.Patch, $"/api/v1/reviews/{id}", a, new { rating = 5, text = "Even better the second time." });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("editedAt").ValueKind);
        while (await _k.Dispatch() > 0) { }
        var sent = _k.F.Portal.VerifiedCalls.Where(c => c.PathAndQuery == "/api/internal/v1/reviews").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).ToList();
        Assert.NotEmpty(sent);
        var last = sent[^1];
        Assert.Equal((id.ToString(), 5, "Even better the second time."), (last.GetProperty("reviewId").GetString(), last.GetProperty("rating").GetInt32(), last.GetProperty("text").GetString()));
        Assert.False(last.TryGetProperty("accountId", out _) || last.TryGetProperty("email", out _));
        _k.F.Clock.Advance(TimeSpan.FromDays(25));   // 35 days after writing
        a = await Login("r5a@example.com");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/reviews/{id}", a, new { rating = 1, text = "Too late." })).Status);
        Assert.Equal(5, (await Agg("listing", Listing()))!.Sum);
    }

    // ---------------------------------------------------------------- votes
    [Fact]
    public async Task A_helpful_vote_is_one_per_buyer_even_in_parallel_never_on_ones_own_review_and_can_be_taken_back()
    {
        var (a, ida) = await Buyer("r6a@example.com");
        var (b, _) = await Buyer("r6b@example.com");
        var id = await Reviewed(a, await Delivered(ida));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", a)).Status);
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Call(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", b)));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.NoContent, r.Status));
        Assert.Equal((1, 1), (await _k.Db(db => db.Reviews.AsNoTracking().Select(r => r.HelpfulCount).SingleAsync()), await _k.Db(db => db.ReviewVotes.CountAsync())));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/reviews/{id}/helpful", b)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/reviews/{id}/helpful", b)).Status);   // again: nothing to take back
        Assert.Equal(0, await _k.Db(db => db.Reviews.AsNoTracking().Select(r => r.HelpfulCount).SingleAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/reviews/{Guid.NewGuid()}/helpful", b)).Status);
    }

    // ---------------------------------------------------------------- reports and moderation
    private async Task<string> Decided(string kind, string reportId, string target, string decision, string? shown = null)
    {
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"rd-{++_n:D3}", "content.report_decided", _k.F.Clock.GetUtcNow().AddSeconds(_n), new { reportId, kind, targetId = target, decision, reasonShownToUser = shown })));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    [Fact]
    public async Task A_report_goes_to_the_portal_once_with_an_opaque_reference_and_a_decision_hides_and_restores_the_review_and_its_figures()
    {
        var (a, ida) = await Buyer("r7a@example.com");
        var (b, idb) = await Buyer("r7b@example.com");
        var (c, idc) = await Buyer("r7c@example.com");
        var id = await Reviewed(a, await Delivered(ida), 2);
        await Reviewed(c, await Delivered(idc), 4);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", a, new { reason = "spam" })).Status);   // not one's own
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", b, new { reason = "nonsense" })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", b, new { reason = "offensive", note = "rude" })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", b, new { reason = "offensive" })).Status);   // again: still one
        Assert.Equal(1, await _k.Db(db => db.ReviewReports.CountAsync()));
        while (await _k.Dispatch() > 0) { }
        var report = _k.F.Portal.VerifiedCalls.Where(x => x.PathAndQuery == "/api/internal/v1/content/reports").Select(x => JsonDocument.Parse(x.BodyText).RootElement.Clone()).Single();
        Assert.Equal(("review", id.ToString()), (report.GetProperty("kind").GetString(), report.GetProperty("targetId").GetString()));
        Assert.DoesNotContain("r7b", report.GetProperty("reporterRef").GetString());
        var reportId = report.GetProperty("reportId").GetString()!;

        Assert.Equal("skipped", await Decided("review", "RV-forged", id.ToString(), "hidden"));                          // not a report we made
        Assert.Equal("skipped", await Decided("review", reportId, Guid.NewGuid().ToString(), "hidden"));               // our report, someone else's target
        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "hidden", "Contains an insult"));
        var hidden = await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.Equal((ReviewStatus.Hidden, "Contains an insult"), (hidden.Status, hidden.ModerationNote));
        Assert.Equal((1, 4L), ((await Agg("listing", Listing()))!.Count, (await Agg("listing", Listing()))!.Sum));      // its 2 stars are out of the figures
        var (_, listed) = await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null);
        Assert.Equal(1, listed.GetProperty("total").GetInt32());                                                         // and out of the public list
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Patch, $"/api/v1/reviews/{id}", a, new { rating = 5, text = "Changed my mind." })).Status);   // a hidden review is not editable

        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "dismissed"));                           // staff changed their mind: restored
        Assert.Equal(ReviewStatus.Published, (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == id))).Status);
        Assert.Equal((2, 6L), ((await Agg("listing", Listing()))!.Count, (await Agg("listing", Listing()))!.Sum));
        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "removed"));
        var removed = await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == id));
        Assert.Equal((ReviewStatus.Removed, ""), (removed.Status, removed.Text));                                         // removed: the text is gone
        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "dismissed"));
        Assert.Equal(ReviewStatus.Removed, (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == id))).Status);   // and it stays removed
        Assert.Equal(1, (await Agg("listing", Listing()))!.Count);
    }

    // ---------------------------------------------------------------- seller reply
    private async Task<string> Reply(string type, Guid reviewId, string replyId, string? text, string? at = null, string reason = "seller_deleted")
    {
        var data = new Dictionary<string, object?> { ["reviewId"] = reviewId.ToString(), ["replyId"] = replyId, ["text"] = text, ["repliedAt"] = _k.F.Clock.GetUtcNow(), ["editedAt"] = at, ["removedAt"] = _k.F.Clock.GetUtcNow(), ["reason"] = reason };
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"rr-{++_n:D3}", type, _k.F.Clock.GetUtcNow().AddSeconds(_n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    [Fact]
    public async Task The_sellers_reply_is_kept_by_the_portals_order_and_removed_with_the_review()
    {
        var (a, ida) = await Buyer("r8@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        Assert.Equal("skipped", await Reply("review.reply_posted", Guid.NewGuid(), "RP-1", "Thank you"));                // a review we do not have
        Assert.Equal("applied", await Reply("review.reply_posted", id, "RP-1", "Thank you!"));
        var (_, listed) = await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null);
        Assert.Equal("Thank you!", listed.GetProperty("reviews")[0].GetProperty("reply").GetProperty("text").GetString());
        Assert.Equal("applied", await Reply("review.reply_posted", id, "RP-1", "Thank you so much!", _k.F.Clock.GetUtcNow().AddMinutes(5).ToString("O")));   // an edit: same reply id
        Assert.Equal("Thank you so much!", (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).ReplyText);
        Assert.Equal("applied", await Reply("review.reply_removed", id, "RP-1", null));
        Assert.Null((await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).ReplyText);
        // a reply on a review that is hidden is not shown
        var (b, idb) = await Buyer("r8b@example.com");
        var id2 = await Reviewed(b, await Delivered(idb), 1);
        await _k.Db(async db => { await db.Reviews.Where(r => r.Id == id2).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Hidden)); return 0; });
        Assert.Equal("applied", await Reply("review.reply_posted", id2, "RP-2", "We are sorry"));      // kept (M6 review L4: the portal will not send it again), but not shown: the list holds published reviews only
        Assert.Equal(1, (await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null)).Body.GetProperty("total").GetInt32());   // the first review only
        await _k.Db(async db => { await db.Reviews.Where(r => r.Id == id2).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Removed)); return 0; });
        Assert.Equal("skipped", await Reply("review.reply_posted", id2, "RP-3", "A removed review keeps nothing"));
    }

    // ---------------------------------------------------------------- erasure and export
    [Fact]
    public async Task Deleting_the_account_deletes_reviews_votes_and_reports_takes_back_the_figures_and_tells_the_portal_and_the_export_holds_them()
    {
        var (a, ida) = await Buyer("r9a@example.com");
        var (b, idb) = await Buyer("r9b@example.com");
        var mine = await Reviewed(a, await Delivered(ida), 5);
        var theirs = await Reviewed(b, await Delivered(idb), 3);
        await Call(HttpMethod.Put, $"/api/v1/reviews/{theirs}/helpful", a);
        await Call(HttpMethod.Put, $"/api/v1/reviews/{mine}/helpful", b);
        await Call(HttpMethod.Post, $"/api/v1/reviews/{theirs}/report", a, new { reason = "spam" });
        using (var scope = _k.F.Services.CreateScope())
        {
            var json = JsonSerializer.Serialize(await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "reviews").ExportAsync(ida, default));
            Assert.Contains("Wonderful", json);
            await scope.ServiceProvider.GetServices<IAccountEraser>().OfType<ReviewsAccountEraser>().Single().EraseAsync(ida, default);
            await scope.ServiceProvider.GetServices<IAccountEraser>().OfType<ReviewsAccountEraser>().Single().EraseAsync(ida, default);   // again: no error
        }
        Assert.Equal(1, await _k.Db(db => db.Reviews.CountAsync()));
        Assert.Equal(0, await _k.Db(db => db.ReviewVotes.CountAsync()));
        var kept = Assert.Single(await _k.Db(db => db.ReviewReports.AsNoTracking().ToListAsync()));   // M6 review MED-2: the report is anonymised, not deleted (staff may still decide on it)
        Assert.Equal((theirs, (string?)null), (kept.ReviewId, kept.Note));
        Assert.NotEqual(ida, kept.AccountId);
        var agg = (await Agg("listing", Listing()))!;
        Assert.Equal((1, 3L), (agg.Count, agg.Sum));
        Assert.Equal(0, await _k.Db(db => db.Reviews.AsNoTracking().Where(r => r.Id == theirs).Select(r => r.HelpfulCount).SingleAsync()));
        while (await _k.Dispatch() > 0) { }
        var state = _k.F.Portal.VerifiedCalls.Where(c => c.PathAndQuery == $"/api/internal/v1/reviews/{mine}/state").Select(c => JsonDocument.Parse(c.BodyText).RootElement.Clone()).ToList();
        Assert.Equal("removed", Assert.Single(state).GetProperty("state").GetString());
    }

    // ---------------------------------------------------------------- photos
    private static byte[] Png(int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        bmp.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Upload(string? token, Guid review, byte[] bytes, string name = "photo.png", string type = "image/png")
    {
        await Ready();
        var content = new MultipartFormDataContent { { new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type) } }, "file", name } };
        var r = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/reviews/{review}/photos") { Content = content };
        if (token is not null) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var res = await _k.Http.SendAsync(r);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task A_photo_is_decided_by_its_bytes_re_encoded_cut_to_size_served_only_while_published_and_capped_at_five_even_in_parallel()
    {
        var (a, ida) = await Buyer("p1a@example.com");
        var (b, _) = await Buyer("p1b@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var big = Png(3200, 200);
        var (st, ph) = await Upload(a, id, big, "evil.pdf", "application/pdf");                            // the name and the declared type are ignored
        Assert.Equal(HttpStatusCode.Created, st);
        Assert.Equal((1600, 100), (ph.GetProperty("width").GetInt32(), ph.GetProperty("height").GetInt32()));   // cut to 1600 on the long side
        var photoId = ph.GetProperty("id").GetGuid();
        var stored = await _k.Db(db => db.ReviewPhotoData.AsNoTracking().Select(d => d.Bytes).SingleAsync());
        Assert.NotEqual(big, stored);                                                                      // a fresh image, not the upload
        var served = await _k.Http.GetAsync($"/api/v1/reviews/photos/{photoId}");                          // public
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        var (_, listed) = await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null);
        Assert.Equal(photoId, listed.GetProperty("reviews")[0].GetProperty("photos")[0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await Upload(b, id, Png(10, 10))).Status);                  // not the author's review
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(null, id, Png(10, 10))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Upload(a, id, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })).Status);   // not a picture
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Upload(a, id, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF"), "x.png", "image/png")).Status);   // a PDF is no picture
        var more = await Task.WhenAll(Enumerable.Range(0, 7).Select(_ => Upload(a, id, Png(20, 20))));    // 1 stored + 7 racing for 4 places
        Assert.Equal(ReviewLimits.MaxPhotos, await _k.Db(db => db.ReviewPhotos.CountAsync()));                  // exactly the free places are filled, never more, never fewer
        Assert.Equal(ReviewLimits.MaxPhotos - 1, more.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(more.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.Status));
        // only the author can delete; a hidden review's photos are not served
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, $"/api/v1/reviews/{id}/photos/{photoId}", b)).Status);
        await _k.Db(async db => { await db.Reviews.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Hidden)); return 0; });
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.GetAsync($"/api/v1/reviews/photos/{photoId}")).StatusCode);
        await _k.Db(async db => { await db.Reviews.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Published)); return 0; });
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/reviews/{id}/photos/{photoId}", a)).Status);
        Assert.Equal(0, await _k.Db(db => db.ReviewPhotoData.CountAsync(d => d.PhotoId == photoId)));
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.GetAsync($"/api/v1/reviews/photos/{photoId}")).StatusCode);
    }

    [Fact]
    public async Task Photos_are_refused_after_the_edit_window_deleted_with_a_removed_review_and_with_the_account()
    {
        var (a, ida) = await Buyer("p2a@example.com");
        var (c, idc) = await Buyer("p2c@example.com");
        var id = await Reviewed(a, await Delivered(ida));
        var id2 = await Reviewed(c, await Delivered(idc));
        Assert.Equal(HttpStatusCode.Created, (await Upload(a, id, Png(30, 30))).Status);
        Assert.Equal(HttpStatusCode.Created, (await Upload(c, id2, Png(30, 30))).Status);
        // a removal decision deletes the pictures with the text
        var (b, _) = await Buyer("p2b@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", b, new { reason = "fake" })).Status);
        while (await _k.Dispatch() > 0) { }
        var reportId = JsonDocument.Parse(_k.F.Portal.VerifiedCalls.Single(x => x.PathAndQuery == "/api/internal/v1/content/reports").BodyText).RootElement.GetProperty("reportId").GetString()!;
        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "removed"));
        Assert.Equal(0, await _k.Db(db => db.ReviewPhotos.CountAsync(p => p.ReviewId == id)));
        Assert.Equal(0, await _k.Db(db => db.ReviewPhotoData.CountAsync()) - await _k.Db(db => db.ReviewPhotos.CountAsync()));   // no orphan bytes
        // the account's erasure takes the photos of its reviews
        using (var scope = _k.F.Services.CreateScope())
        {
            var json = JsonSerializer.Serialize(await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "reviews").ExportAsync(idc, default));
            Assert.Contains("image/png", json);
            await scope.ServiceProvider.GetServices<IAccountEraser>().OfType<ReviewsAccountEraser>().Single().EraseAsync(idc, default);
        }
        Assert.Equal((0, 0), (await _k.Db(db => db.ReviewPhotos.CountAsync()), await _k.Db(db => db.ReviewPhotoData.CountAsync())));
        // after the edit window nothing can be added
        var late = await Reviewed(a, await Delivered(ida));
        _k.F.Clock.Advance(TimeSpan.FromDays(31));
        a = await Login("p2a@example.com");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Upload(a, late, Png(10, 10))).Status);
    }

    // ---------------------------------------------------------------- C17 as the portal really answers (portal docs/REVIEWS.md, merged in dccead1)
    private async Task<OutboxMessage> TheOutbox(string type) => (await _k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == type).ToListAsync())).Single();
    private void Script(int status, string body) { for (var i = 0; i < 10; i++) _k.F.Portal.Respond((HttpStatusCode)status, body); }   // a retry inside the client takes the next answer too

    [Theory]
    [InlineData(201, "{\"reviewId\":\"x\",\"id\":\"6f0c5d2e-0000-0000-0000-000000000001\",\"duplicate\":false,\"updated\":false,\"state\":\"visible\"}", "delivered", null)]
    [InlineData(200, "{\"reviewId\":\"x\",\"id\":\"6f0c5d2e-0000-0000-0000-000000000001\",\"duplicate\":true,\"updated\":false,\"state\":\"visible\"}", "delivered", null)]
    [InlineData(200, "{\"reviewId\":\"x\",\"duplicate\":false,\"updated\":true,\"state\":\"visible\"}", "delivered", null)]
    [InlineData(422, "{\"code\":\"invalid_review\",\"message\":\"The review is not valid.\",\"errors\":{\"listingId\":\"not a product of that seller\"}}", "dead", "portal_422_invalid_review")]
    [InlineData(429, "{\"code\":\"rate_limited\",\"message\":\"slow down\"}", "pending", "portal_429")]
    [InlineData(503, "{}", "pending", "portal_503")]
    [InlineData(401, "{\"code\":\"bad_signature\"}", "dead", "portal_401")]
    public async Task The_review_send_follows_the_portals_real_answers(int status, string body, string expected, string? error)
    {
        var (a, ida) = await Buyer("c17a@example.com", "Jane Brown");
        var line = await Delivered(ida);
        while (await _k.Dispatch() > 0) { }
        var id = await Reviewed(a, line, 4);
        Script(status, body);
        await _k.Dispatch();
        var m = await TheOutbox(ReviewService.UpsertType);
        Assert.Equal(expected, m.Status);
        if (error is not null) Assert.Equal(error, m.LastError);
        var sent = JsonDocument.Parse(_k.F.Portal.VerifiedCalls.First(c => c.PathAndQuery == "/api/internal/v1/reviews").BodyText).RootElement;   // a 503 is tried again inside the client, the body is the same each time
        // exactly the contract's fields: of the buyer only the short display name crosses (no account id, e-mail, order or photos)
        var allowed = new[] { "reviewId", "listingId", "sellerId", "rating", "text", "buyerDisplayName", "createdAt", "editedAt" };
        var keys = sent.EnumerateObject().Select(x => x.Name).ToList();
        Assert.Empty(keys.Except(allowed));
        Assert.All(allowed.Where(k => k != "editedAt"), k => Assert.Contains(k, keys));
        Assert.Equal((id.ToString(), "Jane B.", 4), (sent.GetProperty("reviewId").GetString(), sent.GetProperty("buyerDisplayName").GetString(), sent.GetProperty("rating").GetInt32()));
        Assert.InRange(sent.GetProperty("reviewId").GetString()!.Length, 1, 50);
        Assert.Equal(Listing(), sent.GetProperty("listingId").GetGuid());
    }

    [Theory]
    [InlineData(200, "{\"reviewId\":\"x\",\"state\":\"hidden\",\"changed\":true}", "delivered", null)]
    [InlineData(200, "{\"reviewId\":\"x\",\"state\":\"hidden\",\"changed\":false}", "delivered", null)]
    [InlineData(404, "{\"code\":\"not_found\"}", "delivered", null)]
    [InlineData(422, "{\"code\":\"invalid_state\",\"message\":\"bad state\"}", "dead", "portal_422_invalid_state")]
    [InlineData(409, "{\"code\":\"review_removed\",\"message\":\"final\"}", "dead", "portal_409_review_removed")]
    [InlineData(429, "{\"code\":\"rate_limited\"}", "pending", "portal_429")]
    [InlineData(500, "{}", "pending", "portal_500")]
    public async Task The_state_send_follows_the_portals_real_answers(int status, string body, string expected, string? error)
    {
        var id = Guid.NewGuid();
        using (var scope = _k.F.Services.CreateScope())
            await new OutboxWriter(scope.ServiceProvider.GetRequiredService<MarketplaceDb>(), _k.F.Clock).TryEnqueueAsync(ReviewService.StateType, new ReviewService.StatePayload(id, "hidden", "Under review"), $"review-state:{id:N}:hidden");
        Script(status, body);
        await _k.Dispatch();
        var m = await TheOutbox(ReviewService.StateType);
        Assert.Equal(expected, m.Status);
        if (error is not null) Assert.Equal(error, m.LastError);
        var call = _k.F.Portal.VerifiedCalls.First(c => c.PathAndQuery == $"/api/internal/v1/reviews/{id}/state");
        var sent = JsonDocument.Parse(call.BodyText).RootElement;
        Assert.Equal(("hidden", "Under review"), (sent.GetProperty("state").GetString(), sent.GetProperty("reason").GetString()));
    }

    [Theory]
    [InlineData(200, "{\"reportId\":\"x\",\"status\":\"recorded\"}", "delivered")]
    [InlineData(404, "{\"code\":\"not_found\",\"message\":\"No active reply with that id.\"}", "delivered")]   // the reply was removed a moment ago: nothing left to moderate
    [InlineData(422, "{\"code\":\"invalid_target\",\"message\":\"targetId must be a reply id.\"}", "dead")]
    public async Task A_report_of_a_reply_names_the_reply_id_and_a_reply_that_is_already_gone_is_not_dead_lettered(int status, string body, string expected)
    {
        var (a, ida) = await Buyer("c17b@example.com");
        var (b, _) = await Buyer("c17c@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        while (await _k.Dispatch() > 0) { }
        var replyId = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Reply("review.reply_posted", id, replyId, "Thank you!"));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/reply/report", b, new { reason = "offensive" })).Status);
        Script(status, body);
        await _k.Dispatch();
        Assert.Equal(expected, (await TheOutbox(ReviewService.ReportType)).Status);
        var sent = JsonDocument.Parse(_k.F.Portal.VerifiedCalls.First(c => c.PathAndQuery == "/api/internal/v1/content/reports").BodyText).RootElement;
        Assert.Equal(("review_reply", replyId), (sent.GetProperty("kind").GetString(), sent.GetProperty("targetId").GetString()));
    }

    [Theory]
    [InlineData("seller_deleted")]
    [InlineData("review_removed")]
    [InlineData("staff_removed")]
    public async Task Every_removal_reason_of_a_reply_takes_it_down(string reason)
    {
        var (a, ida) = await Buyer("c17d@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var replyId = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Reply("review.reply_posted", id, replyId, "Thank you!"));
        Assert.Equal(replyId, (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).ReplyId);
        Assert.Equal("applied", await Reply("review.reply_removed", id, replyId, null, reason: reason));
        Assert.Null((await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).ReplyId);
    }

    [Fact]
    public async Task A_reply_event_flagged_predecessorDead_is_applied_without_the_seller_order_alarm_or_a_reconcile()
    {
        var (a, ida) = await Buyer("c17e@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var data = new Dictionary<string, object?> { ["reviewId"] = id.ToString(), ["replyId"] = Guid.NewGuid().ToString(), ["text"] = "Thank you!", ["repliedAt"] = _k.F.Clock.GetUtcNow(), ["editedAt"] = null };
        var body = Body("rd-dead", "review.reply_posted", _k.F.Clock.GetUtcNow().AddSeconds(1), data).Insert(1, "\"predecessorDead\":true,");
        var (status, json) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", StatusOf(json));
        Assert.Equal("Thank you!", (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).ReplyText);   // a reply event is a full snapshot: the later one says it all
        var row = await _k.Db(db => db.PortalInboundEvents.AsNoTracking().SingleAsync(e => e.Id == "rd-dead"));
        Assert.DoesNotContain("predecessor_dead", row.Note ?? "");
        Assert.Empty(await _k.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == OrderReconcileHandler.Type).ToListAsync()));
    }

    // ---------------------------------------------------------------- C17.1: the portal's sequence on the event
    private async Task<string> Numbered(string eventId, string type, Guid reviewId, string replyId, string? text, DateTimeOffset at, string? raw)
    {
        var data = new Dictionary<string, object?> { ["reviewId"] = reviewId.ToString(), ["replyId"] = replyId, ["text"] = text, ["repliedAt"] = at, ["editedAt"] = null, ["removedAt"] = at, ["reason"] = "seller_deleted" };
        var body = Body(eventId, type, at, data);
        if (raw is not null) body = body.Insert(1, raw + ",");
        var (status, json) = await Post(_k.Http, Request(_k.F, body));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }
    private static string Seq(Guid reviewId, long sequence, string? scope = null) => $"\"sequenceScope\":\"{scope ?? $"review:{reviewId}"}\",\"sequence\":{sequence}";
    private async Task<string?> ReplyNow() => (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).ReplyText;

    [Fact]
    public async Task Events_of_one_review_are_ordered_by_the_sequence_whatever_the_arrival_the_time_or_the_event_ids()
    {
        var (a, ida) = await Buyer("c171a@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var at = _k.F.Clock.GetUtcNow();                                  // three edits in the SAME instant
        var rid = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Numbered("a-3", "review.reply_posted", id, rid, "third", at, Seq(id, 3)));        // arrives first, though it is the newest
        Assert.Equal("skipped", await Numbered("z-2", "review.reply_posted", id, rid, "second", at, Seq(id, 2)));       // a late older one (its id would sort after "a-3": the id is no order)
        Assert.Equal("skipped", await Numbered("m-1", "review.reply_posted", id, rid, "first", at, Seq(id, 1)));
        Assert.Equal("third", await ReplyNow());
        Assert.Equal("skipped", await Numbered("b-3", "review.reply_posted", id, rid, "third again", at, Seq(id, 3)));  // the same sequence under another event id: not newer
        Assert.Equal("third", await ReplyNow());
        Assert.Equal("applied", await Numbered("c-4", "review.reply_posted", id, rid, "fourth", at, Seq(id, 4)));
        Assert.Equal("fourth", await ReplyNow());
        // a removal is ordered the same way: an older one does not take down a newer reply, a newer one does
        Assert.Equal("skipped", await Numbered("d-2", "review.reply_removed", id, rid, null, at, Seq(id, 2)));
        Assert.Equal("fourth", await ReplyNow());
        Assert.Equal("applied", await Numbered("e-5", "review.reply_removed", id, rid, null, at, Seq(id, 5)));
        Assert.Null(await ReplyNow());
        Assert.Equal("skipped", await Numbered("f-4b", "review.reply_posted", id, rid, "a dead event re-queued late", at.AddMinutes(1), Seq(id, 4)));   // even with a later time
        Assert.Null(await ReplyNow());
    }

    [Fact]
    public async Task The_same_event_id_is_applied_once_with_or_without_a_sequence()
    {
        var (a, ida) = await Buyer("c171b@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var at = _k.F.Clock.GetUtcNow();
        var rid = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Numbered("dup-1", "review.reply_posted", id, rid, "Thanks", at, Seq(id, 1)));
        Assert.Equal("duplicate", await Numbered("dup-1", "review.reply_posted", id, rid, "Thanks", at, Seq(id, 1)));
        Assert.Equal("applied", await Numbered("dup-2", "review.reply_posted", id, rid, "Thanks a lot", at.AddSeconds(1), null));
        Assert.Equal("duplicate", await Numbered("dup-2", "review.reply_posted", id, rid, "Thanks a lot", at.AddSeconds(1), null));
        Assert.Equal("Thanks a lot", await ReplyNow());
    }

    [Fact]
    public async Task Without_a_sequence_the_arrival_order_decides_for_the_same_instant_and_a_strictly_older_event_is_still_refused()
    {
        var (a, ida) = await Buyer("c171c@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var at = _k.F.Clock.GetUtcNow();
        var rid = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Numbered("z-first", "review.reply_posted", id, rid, "first edit", at, null));
        Assert.Equal("applied", await Numbered("a-second", "review.reply_posted", id, rid, "second edit", at, null));   // the same instant: the later ARRIVAL wins (its id sorts before "z-first", which used to lose)
        Assert.Equal("second edit", await ReplyNow());
        Assert.Equal("skipped", await Numbered("old-1", "review.reply_posted", id, rid, "a re-queued old one", at.AddSeconds(-30), null));   // strictly older than what was applied
        Assert.Equal("second edit", await ReplyNow());
        Assert.Equal("applied", await Numbered("new-1", "review.reply_posted", id, rid, "third edit", at.AddSeconds(5), null));
        Assert.Equal("third edit", await ReplyNow());
    }

    [Theory]
    [InlineData("\"sequenceScope\":\"review:{id}\"")]                                  // a scope without a sequence
    [InlineData("\"sequence\":9")]                                                       // a sequence without a scope
    [InlineData("\"sequenceScope\":\"review:{id}\",\"sequence\":\"9\"")]               // a sequence that is a string
    [InlineData("\"sequenceScope\":\"review:{id}\",\"sequence\":1.5")]                 // not an integer
    [InlineData("\"sequenceScope\":\"review:{id}\",\"sequence\":-1")]                  // negative
    [InlineData("\"sequenceScope\":7,\"sequence\":9")]                                  // a scope that is not a string
    [InlineData("\"sequenceScope\":\"review:someone-else\",\"sequence\":1")]            // another scope than this review's: not comparable, so not used
    public async Task A_missing_or_malformed_or_foreign_sequence_is_ignored_and_the_arrival_order_applies(string raw)
    {
        var (a, ida) = await Buyer("c171d@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var at = _k.F.Clock.GetUtcNow();
        var rid = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Numbered("seq-ok", "review.reply_posted", id, rid, "numbered", at, Seq(id, 10)));
        Assert.Equal("applied", await Numbered("seq-odd", "review.reply_posted", id, rid, "by arrival", at.AddSeconds(1), raw.Replace("{id}", id.ToString())));   // not compared with the stored 10
        Assert.Equal("by arrival", await ReplyNow());
    }

    [Fact]
    public void The_envelope_carries_the_sequence_for_every_event_type_and_only_when_both_fields_are_valid()
    {
        PortalEvent? Parse(string extra)
        {
            Assert.True(PortalEvent.TryParse(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"e1\",\"type\":\"order.shipped\",\"createdAt\":\"2026-11-05T10:00:00Z\",\"data\":{}" + extra + "}"), out var e, out _));
            return e;
        }
        var ok = Parse(",\"sequenceScope\":\"so:abc\",\"sequence\":7");
        Assert.Equal(("so:abc", 7L), (ok!.SequenceScope, ok.Sequence));
        Assert.Equal((null, null), (Parse("")!.SequenceScope, Parse("")!.Sequence));                                                       // legacy and catalog events have none
        Assert.Null(Parse(",\"sequenceScope\":\"so:abc\"")!.Sequence);
        Assert.Null(Parse(",\"sequence\":3")!.SequenceScope);
        Assert.Null(Parse(",\"sequenceScope\":\"" + new string('x', PortalEvent.MaxScopeLength + 1) + "\",\"sequence\":3")!.Sequence);   // an absurd scope is dropped with its sequence
        Assert.Equal(9_000_000_000L, Parse(",\"sequenceScope\":\"s\",\"sequence\":9000000000")!.Sequence);                              // beyond 32 bits
        Assert.Null(Parse(",\"sequenceScope\":\"s\",\"sequence\":\"7\"")!.Sequence);
    }

    // ---------------------------------------------------------------- review of the merged stack (HIGH-1 and following)
    [Fact]
    public async Task A_staff_decision_on_a_reported_seller_reply_takes_down_that_reply_only_and_only_for_our_own_report_and_target()
    {
        var (a, ida) = await Buyer("h1a@example.com");
        var (b, _) = await Buyer("h1b@example.com");
        var id = await Reviewed(a, await Delivered(ida), 4);
        while (await _k.Dispatch() > 0) { }
        var rid = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Reply("review.reply_posted", id, rid, "We are not sorry at all"));
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/reply/report", b, new { reason = "offensive" })).Status);
        while (await _k.Dispatch() > 0) { }
        var sent = JsonDocument.Parse(_k.F.Portal.VerifiedCalls.Single(x => x.PathAndQuery == "/api/internal/v1/content/reports").BodyText).RootElement;
        Assert.Equal("review_reply", sent.GetProperty("kind").GetString());
        var reportId = sent.GetProperty("reportId").GetString()!;

        Assert.Equal("skipped", await Decided("review_reply", "RV-forged", rid, "removed"));                            // not a report we made
        Assert.Equal("skipped", await Decided("review_reply", reportId, Guid.NewGuid().ToString(), "removed"));        // our report, another reply
        Assert.Equal("skipped", await Decided("review_reply", reportId, id.ToString(), "removed"));                    // our report, the review's id is not the reply's
        Assert.Equal("We are not sorry at all", await ReplyNow());
        Assert.Equal("applied", await Decided("review_reply", reportId, rid, "removed", "Not allowed in a reply"));
        Assert.Null(await ReplyNow());                                                                                   // gone from the page
        var review = await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync());
        Assert.Equal(ReviewStatus.Published, review.Status);                                                             // the REVIEW is untouched, and so are its figures
        Assert.Equal(1, (await Agg("listing", Listing()))!.Count);
        Assert.Equal("removed", (await _k.Db(db => db.ReviewReports.AsNoTracking().SingleAsync())).Decision);
        // a decision that comes after the seller already posted another reply does not take that one down
        var rid2 = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Reply("review.reply_posted", id, rid2, "A better answer"));
        Assert.Equal("applied", await Decided("review_reply", reportId, rid, "hidden"));
        Assert.Equal("A better answer", await ReplyNow());
    }

    /// <summary>The figures of every listing, seller and brand are exactly what the published reviews add up to (a lost or doubled update, or a hidden review left in, shows here).</summary>
    private async Task AssertFiguresAreWhatThePublishedReviewsAddUp()
    {
        var published = await _k.Db(db => db.Reviews.AsNoTracking().Where(r => r.Status == ReviewStatus.Published).ToListAsync());
        var rows = await _k.Db(db => db.RatingAggregates.AsNoTracking().ToListAsync());
        var expected = published.SelectMany(r => new[] { ("listing", r.ListingId, r.Rating), ("seller", r.SellerId, r.Rating), ("brand", r.BrandId, r.Rating) })
            .GroupBy(x => (x.Item1, x.Item2)).ToDictionary(g => g.Key, g => (Count: g.Count(), Sum: g.Sum(x => (long)x.Item3), Stars: Enumerable.Range(1, 5).Select(n => g.Count(x => x.Item3 == n)).ToArray()));
        foreach (var a in rows)
        {
            var e = expected.GetValueOrDefault((a.Scope, a.ScopeId), (Count: 0, Sum: 0L, Stars: new int[5]));
            Assert.Equal((e.Count, e.Sum), (a.Count, a.Sum));
            Assert.Equal(e.Stars, new[] { a.Stars1, a.Stars2, a.Stars3, a.Stars4, a.Stars5 });
        }
        Assert.All(expected.Keys, k => Assert.Contains(rows, a => (a.Scope, a.ScopeId) == k));
    }

    [Fact]
    public async Task Parallel_edits_of_the_same_review_leave_the_figures_equal_to_the_rating_that_won()
    {
        var (a, ida) = await Buyer("m1a@example.com");
        var (c, idc) = await Buyer("m1c@example.com");
        var id = await Reviewed(a, await Delivered(ida), 3);
        await Reviewed(c, await Delivered(idc), 4);
        for (var round = 0; round < 3; round++)
        {
            var edits = await Task.WhenAll(new[] { 5, 1, 2, 4, 1, 5, 2, 4 }.Select((r, i) => Call(HttpMethod.Patch, $"/api/v1/reviews/{id}", a, new { rating = r, text = $"Edit {round}-{i} of the same review." })));
            Assert.All(edits, e => Assert.Equal(HttpStatusCode.OK, e.Status));
            await AssertFiguresAreWhatThePublishedReviewsAddUp();
        }
        Assert.Equal(2, (await Agg("listing", Listing()))!.Count);
    }

    [Fact]
    public async Task An_edit_racing_a_moderation_decision_never_puts_a_hidden_review_back_into_the_figures_or_loses_its_rating()
    {
        var (reporter, _) = await Buyer("m1r@example.com");
        var authors = new List<(string Token, Guid Review)>();
        for (var i = 0; i < 5; i++)
        {
            var (t, acc) = await Buyer($"m1x{i}@example.com");
            authors.Add((t, await Reviewed(t, await Delivered(acc), 3)));
        }
        foreach (var (_, rv) in authors) Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{rv}/report", reporter, new { reason = "spam" })).Status);
        while (await _k.Dispatch() > 0) { }
        var reportIds = _k.F.Portal.VerifiedCalls.Where(x => x.PathAndQuery == "/api/internal/v1/content/reports").Select(x => JsonDocument.Parse(x.BodyText).RootElement.Clone())
            .ToDictionary(j => j.GetProperty("targetId").GetString()!, j => j.GetProperty("reportId").GetString()!);
        var work = authors.SelectMany((x, i) => new Func<Task>[]
        {
            async () => await Call(HttpMethod.Patch, $"/api/v1/reviews/{x.Review}", x.Token, new { rating = 5, text = "Edited while it is being decided." }),   // may be refused (Locked) when the decision came first
            async () => Assert.Equal("applied", await Decided("review", reportIds[x.Review.ToString()], x.Review.ToString(), "hidden")),
        }).Select(f => f());
        await Task.WhenAll(work);
        await AssertFiguresAreWhatThePublishedReviewsAddUp();
        Assert.All(await _k.Db(db => db.Reviews.AsNoTracking().ToListAsync()), r => Assert.Equal(ReviewStatus.Hidden, r.Status));
        Assert.Equal(0, (await Agg("listing", Listing()))!.Count);
    }

    [Fact]
    public async Task A_decision_that_arrives_after_the_reporters_erased_their_accounts_still_applies_and_the_reports_hold_nothing_of_them()
    {
        var (a, ida) = await Buyer("m2a@example.com");
        var (b, idb) = await Buyer("m2b@example.com");
        var (c, idc) = await Buyer("m2c@example.com");
        var id = await Reviewed(a, await Delivered(ida), 1);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", b, new { reason = "offensive", note = "this person insulted me" })).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", c, new { reason = "offensive" })).Status);
        while (await _k.Dispatch() > 0) { }
        var reportIds = _k.F.Portal.VerifiedCalls.Where(x => x.PathAndQuery == "/api/internal/v1/content/reports").Select(x => JsonDocument.Parse(x.BodyText).RootElement.Clone().GetProperty("reportId").GetString()!).ToList();
        Assert.Equal(2, reportIds.Count);
        foreach (var reporter in new[] { idb, idc })                    // both reporters delete their accounts; two erased reporters of one review must not collide
            using (var scope = _k.F.Services.CreateScope())
            {
                var eraser = scope.ServiceProvider.GetServices<IAccountEraser>().OfType<ReviewsAccountEraser>().Single();
                await eraser.EraseAsync(reporter, default);
                await eraser.EraseAsync(reporter, default);              // again: nothing to do
                var export = JsonSerializer.Serialize(await scope.ServiceProvider.GetServices<IAccountDataExporter>().Single(e => e.Section == "reviews").ExportAsync(reporter, default));
                Assert.DoesNotContain("offensive", export);              // nothing of the reports is left under their name
            }
        var rows = await _k.Db(db => db.ReviewReports.AsNoTracking().ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((id, "review", id.ToString(), (string?)null), (r.ReviewId, r.Kind, r.TargetId, r.Note)));
        Assert.DoesNotContain(rows, r => r.AccountId == idb || r.AccountId == idc);
        Assert.Equal(2, rows.Select(r => r.AccountId).Distinct().Count());
        Assert.Equal("applied", await Decided("review", reportIds[0], id.ToString(), "hidden", "Insulting"));      // staff decide after the reporters are gone
        var review = await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync());
        Assert.Equal(ReviewStatus.Hidden, review.Status);
        Assert.Equal(0, (await Agg("listing", Listing()))!.Count);
    }

    [Fact]
    public async Task A_review_photo_is_cached_for_minutes_not_a_day_revalidated_cheaply_and_a_hidden_reviews_photo_is_never_answered_with_a_304()
    {
        var (a, ida) = await Buyer("m3a@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var (st, ph) = await Upload(a, id, Png(30, 30));
        Assert.Equal(HttpStatusCode.Created, st);
        var url = $"/api/v1/reviews/photos/{ph.GetProperty("id").GetGuid()}";
        var first = await _k.Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var cc = first.Headers.CacheControl!;
        Assert.True(cc.Public && cc.MustRevalidate);
        Assert.Equal(TimeSpan.FromMinutes(5), cc.MaxAge);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.Equal("nosniff", first.Headers.GetValues("X-Content-Type-Options").Single());
        var again = new HttpRequestMessage(HttpMethod.Get, url); again.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotModified, (await _k.Http.SendAsync(again)).StatusCode);                 // asking again is cheap while it is shown
        await _k.Db(async db => { await db.Reviews.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Hidden)); return 0; });
        var stale = new HttpRequestMessage(HttpMethod.Get, url); stale.Headers.IfNoneMatch.Add(etag!);
        Assert.Equal(HttpStatusCode.NotFound, (await _k.Http.SendAsync(stale)).StatusCode);                    // a cache that holds it is told it is gone, not "unchanged"
    }

    [Fact]
    public async Task A_fully_refunded_line_cannot_be_reviewed_a_partly_refunded_one_can_and_a_refund_after_the_review_leaves_the_review_as_it_is()
    {
        var (a, ida) = await Buyer("l1a@example.com");
        var line = await Delivered(ida);
        async Task Refunded(int quantity) => await _k.Db(async db => { await db.OrderLines.Where(l => l.Id == line).ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, 2).SetProperty(l => l.RefundedQuantity, quantity)); return 0; });
        async Task<int> Offered() => (await Call(HttpMethod.Get, $"/api/v1/reviews/eligible?listingId={Listing()}", a)).Body.GetProperty("items").GetArrayLength();
        await Refunded(2);                                                                  // all of it refunded: nothing was kept to review
        Assert.Equal(0, await Offered());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Write(a, line, 5)).Status);
        await Refunded(1);                                                                  // part of it kept: it can be reviewed
        Assert.Equal(1, await Offered());
        var id = await Reviewed(a, line, 5);
        await Refunded(2);                                                                  // refunded AFTER the review: policy M6-09, the review stays (it is the buyer's honest account of what they received) and stays editable
        var (_, listed) = await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null);
        Assert.Equal(1, listed.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Patch, $"/api/v1/reviews/{id}", a, new { rating = 4, text = "Changed my mind after the refund." })).Status);
        Assert.Equal(ReviewStatus.Published, (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).Status);
    }

    [Fact]
    public async Task Review_writes_are_limited_per_signed_in_account_not_per_address_and_requests_without_a_session_do_not_count()
    {
        var (a, ida) = await Buyer("l2a@example.com");
        var (b, _) = await Buyer("l2b@example.com");
        var (c, _) = await Buyer("l2c@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        for (var i = 0; i < 25; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", null)).Status);   // no session: not counted
        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 36; i++) results.Add((await Call(i % 2 == 0 ? HttpMethod.Put : HttpMethod.Delete, $"/api/v1/reviews/{id}/helpful", b)).Status);   // vote, un-vote, vote ...
        Assert.All(results.Take(30), s => Assert.Equal(HttpStatusCode.NoContent, s));
        Assert.All(results.Skip(30), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
        var refused = await _k.Send(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", null, b);
        Assert.Equal("60", refused.Headers.RetryAfter!.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", c)).Status);   // another buyer, the same address: its own bucket
    }

    [Fact]
    public async Task A_review_hidden_before_its_first_send_reaches_the_portal_when_it_is_restored_and_a_reply_that_came_meanwhile_is_there()
    {
        var (a, ida) = await Buyer("l4a@example.com");
        var (b, _) = await Buyer("l4b@example.com");
        var id = await Reviewed(a, await Delivered(ida), 2);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Post, $"/api/v1/reviews/{id}/report", b, new { reason = "spam" })).Status);
        var reportId = (await _k.Db(db => db.ReviewReports.AsNoTracking().SingleAsync())).ReportId;
        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "hidden"));                      // hidden BEFORE the first send
        while (await _k.Dispatch() > 0) { }
        Assert.DoesNotContain(_k.F.Portal.VerifiedCalls, c => c.PathAndQuery == "/api/internal/v1/reviews");      // staff know it; the seller was not told
        var rid = Guid.NewGuid().ToString();
        Assert.Equal("applied", await Reply("review.reply_posted", id, rid, "We would like to make it right"));     // the portal's reply arrives while it is hidden
        Assert.Equal(0, (await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null)).Body.GetProperty("total").GetInt32());
        Assert.Equal("applied", await Decided("review", reportId, id.ToString(), "dismissed"));                   // staff change their mind: restored
        while (await _k.Dispatch() > 0) { }
        var sent = JsonDocument.Parse(Assert.Single(_k.F.Portal.VerifiedCalls, c => c.PathAndQuery == "/api/internal/v1/reviews").BodyText).RootElement;   // now the seller is told, once
        Assert.Equal((id.ToString(), 2), (sent.GetProperty("reviewId").GetString(), sent.GetProperty("rating").GetInt32()));
        var listed = (await Call(HttpMethod.Get, $"/api/v1/reviews?listingId={Listing()}", null)).Body.GetProperty("reviews")[0];
        Assert.Equal("We would like to make it right", listed.GetProperty("reply").GetProperty("text").GetString());   // and the reply that came while it was hidden is shown
    }

    [Fact]
    public async Task A_helpful_vote_can_be_taken_back_from_a_review_that_has_since_been_hidden()
    {
        var (a, ida) = await Buyer("l5a@example.com");
        var (b, _) = await Buyer("l5b@example.com");
        var id = await Reviewed(a, await Delivered(ida), 4);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", b)).Status);
        Assert.Equal(1, (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).HelpfulCount);
        await _k.Db(async db => { await db.Reviews.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Hidden)); return 0; });
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Put, $"/api/v1/reviews/{id}/helpful", b)).Status);       // no new votes on it
        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, $"/api/v1/reviews/{id}/helpful", b)).Status);   // but the one cast can be taken back
        Assert.Equal(0, await _k.Db(db => db.ReviewVotes.CountAsync()));
        Assert.Equal(0, (await _k.Db(db => db.Reviews.AsNoTracking().SingleAsync())).HelpfulCount);
    }

    [Fact]
    public async Task A_sequence_in_another_scope_than_the_reviews_own_is_ignored_and_logged_as_a_warning()
    {
        var (a, ida) = await Buyer("c171w@example.com");
        var id = await Reviewed(a, await Delivered(ida), 5);
        var at = _k.F.Clock.GetUtcNow();
        Assert.Equal("applied", await Numbered("warn-1", "review.reply_posted", id, Guid.NewGuid().ToString(), "Thanks", at, "\"sequenceScope\":\"review:not-this-one\",\"sequence\":4"));
        Assert.Contains(_k.F.Logs.Lines, l => l.Contains("ReviewReplyHandler") && l.Contains("instead of the review's own") && l.Contains("warn-1") && l.Contains("review:not-this-one"));
        Assert.Equal("applied", await Numbered("warn-2", "review.reply_posted", id, Guid.NewGuid().ToString(), "Thanks again", at, Seq(id, 5)));   // the review's own scope: no warning for it
        Assert.DoesNotContain(_k.F.Logs.Lines, l => l.Contains("instead of the review's own") && l.Contains("warn-2"));
    }
}
