using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Outbox;
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
        Assert.Equal("skipped", await Reply("review.reply_posted", id2, "RP-2", "We are sorry"));
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
        Assert.Equal((0, 0), (await _k.Db(db => db.ReviewVotes.CountAsync()), await _k.Db(db => db.ReviewReports.CountAsync())));
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
}
