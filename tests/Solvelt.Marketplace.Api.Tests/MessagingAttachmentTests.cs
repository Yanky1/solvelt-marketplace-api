using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Fulfilment;
using Solvelt.Marketplace.Api.Messaging;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using SkiaSharp;
using static Solvelt.Marketplace.Api.Tests.Infrastructure.WebhookTestKit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M5 slice 4b: seller attachments (sniffed, capped, stripped, served as attachment), message retention and staff moderation of a reported message.</summary>
public sealed class MessagingAttachmentTests : OrderFlowBase
{
    private int _n;
    private readonly Dictionary<Guid, Func<HttpResponseMessage>> _files = new();

    public MessagingAttachmentTests()
    {
        _onOther = c =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(c.PathAndQuery, "/attachments/([0-9a-f-]{36})$");
            if (m.Success && _files.TryGetValue(Guid.Parse(m.Groups[1].Value), out var f)) return f();
            if (c.PathAndQuery.Contains("/receipts")) return FakePortal.Json(HttpStatusCode.OK, "{}");
            if (c.PathAndQuery == "/api/internal/v1/content/reports") return FakePortal.Json(HttpStatusCode.Created, "{}");
            return null;
        };
    }

    // ---- sample files: REAL images (the sanitizer decodes and re-encodes them), with metadata spliced in where a test wants some
    private static byte[] Encode(SKEncodedImageFormat f, int w = 8, int h = 6, SKColor? left = null)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(SKColors.CornflowerBlue);
        if (left is { } c) for (var y = 0; y < h; y++) for (var x = 0; x < Math.Max(1, w / 2); x++) bmp.SetPixel(x, y, c);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(f, 90).ToArray();
    }

    private static byte[] Seg(byte marker, byte[] data) => [0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2), .. data];

    /// <summary>A JPEG with every kind of identifying data the review found: EXIF with GPS, a comment, XMP, an MPF (APP2) segment, and an EXIF segment after the image data and after the end of the image.</summary>
    private static byte[] Jpeg(bool exif = true, int w = 8, int h = 6)
    {
        var j = Encode(SKEncodedImageFormat.Jpeg, w, h);
        if (!exif) return j;
        byte[] A(string t) => Encoding.ASCII.GetBytes(t);
        var head = new List<byte> { 0xFF, 0xD8 };
        head.AddRange(Seg(0xE1, A("Exif\0\0GPSLatitude=40.69 Make=Phone")));
        head.AddRange(Seg(0xFE, A("secret comment")));
        head.AddRange(Seg(0xE1, A("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>Jane</x:xmpmeta>")));
        head.AddRange(Seg(0xE2, A("MPF\0GPSSECRET")));
        // the real JPEG without its SOI, its last two bytes (EOI) replaced by an EXIF segment after the scan, then a real EOI, then a whole second image with its own EXIF
        var body = j[2..^2];
        return [.. head, .. body, .. Seg(0xE1, A("Exif\0\0GPSAFTERSCAN")), 0xFF, 0xD9, .. Seg(0xE1, A("Exif\0\0GPSTRAILER")), 0xFF, 0xD8, 0xFF, 0xD9];
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n => { var c = (uint)n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    private static uint Crc(byte[] b) { var c = 0xFFFFFFFFu; foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
    private static byte[] Chunk(string type, byte[] data)
    {
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var crc = Crc(td);
        return [.. new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }, .. td, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    /// <summary>A real PNG with text and EXIF chunks (a PNG's first chunk is IHDR, 25 bytes after the 8-byte signature).</summary>
    private static byte[] Png(bool text = true, int w = 8, int h = 6)
    {
        var p = Encode(SKEncodedImageFormat.Png, w, h);
        if (!text) return p;
        return [.. p[..33], .. Chunk("tEXt", Encoding.ASCII.GetBytes("Author\0Jane Brown")), .. Chunk("eXIf", Encoding.ASCII.GetBytes("GPS")), .. p[33..]];
    }

    /// <summary>A real WebP with EXIF and XMP chunks appended (the RIFF size is fixed up).</summary>
    private static byte[] Webp()
    {
        var w = Encode(SKEncodedImageFormat.Webp);
        byte[] C(string cc, string data) { var d = Encoding.ASCII.GetBytes(data); return [.. Encoding.ASCII.GetBytes(cc), (byte)d.Length, (byte)(d.Length >> 8), 0, 0, .. d, .. (d.Length % 2 == 1 ? new byte[] { 0 } : [])]; }
        var all = w.Concat(C("EXIF", "GPS here")).Concat(C("XMP ", "jane")).ToArray();
        var size = all.Length - 8;
        all[4] = (byte)size; all[5] = (byte)(size >> 8); all[6] = (byte)(size >> 16); all[7] = (byte)(size >> 24);
        return all;
    }

    /// <summary>A JPEG with an EXIF orientation tag (1-8) in a big-endian TIFF block.</summary>
    private static byte[] JpegWithOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff = [.. "Exif\0\0MM"u8.ToArray(), 0, 0x2A, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0, 0, 0, 0];
        return [0xFF, 0xD8, .. Seg(0xE1, tiff), .. jpeg[2..]];
    }

    private static byte[] Pdf(string extra = "") => Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog" + extra + ">>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");

    private async Task<(MarketplaceOrder O, OrderGroup G)> Placed()
    {
        await Login();
        await AddToCart("challah-classic", 2);
        var (number, intent, total) = await Checkout();
        await Pay(number, intent, total);
        await Drain();
        var o = await Order(number);
        return (o, o.Groups.Single());
    }

    private async Task<string> SellerMessage(MarketplaceOrder o, OrderGroup g, string text, params (Guid Id, string Name, string Type, long Size)[] files)
    {
        var data = new Dictionary<string, object?> { ["messageId"] = $"pm-{++_n}", ["kind"] = "order", ["text"] = text, ["sentAt"] = _k.F.Clock.GetUtcNow().AddSeconds(30),
            ["marketplaceOrderId"] = o.Number, ["sellerOrderNumber"] = g.SellerOrderNumber, ["attachments"] = files.Select(f => new { id = f.Id, fileName = f.Name, contentType = f.Type, size = f.Size, path = "/x" }).ToArray() };
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"ma-{++_n:D3}", "message.sent", _k.F.Clock.GetUtcNow().AddSeconds(-500 + _n), data)));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    private static HttpResponseMessage File(byte[] bytes, string declared = "image/jpeg") => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(declared) } } };

    private async Task<HttpResponseMessage> Get(string path, string? session = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session ?? _session);
        return await _k.Http.SendAsync(r);
    }

    private Task<List<MessageAttachment>> Attachments() => _k.Db(db => db.MessageAttachments.AsNoTracking().OrderBy(a => a.FileName).ToListAsync());

    // ------------------------------------------------------------------ pipeline

    [Fact]
    public async Task Seller_files_are_fetched_with_a_signed_get_checked_by_their_bytes_stripped_and_stored()
    {
        var (o, g) = await Placed();
        Guid jpg = Guid.NewGuid(), png = Guid.NewGuid(), webp = Guid.NewGuid(), pdf = Guid.NewGuid();
        _files[jpg] = () => File(Jpeg(), "text/html");                 // declared type is a lie: only the bytes count
        _files[png] = () => File(Png(), "image/png");
        _files[webp] = () => File(Webp(), "application/octet-stream");
        _files[pdf] = () => File(Pdf(), "application/pdf");
        Assert.Equal("applied", await SellerMessage(o, g, "Photos", (jpg, "a.jpg", "image/jpeg", 10), (png, "b.png", "image/png", 10), (webp, "c.webp", "image/webp", 10), (pdf, "d.pdf", "application/pdf", 10)));
        await Drain();
        var atts = await Attachments();
        Assert.All(atts, a => Assert.Equal("stored", a.Status));
        Assert.Equal(new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" }, atts.OrderBy(a => a.FileName).Select(a => a.SniffedType).ToArray());
        Assert.All(Portal("attachments/" + jpg).Concat(Portal("attachments/" + png)), c => { Assert.True(c.Verified); Assert.Equal("GET", c.Method); Assert.Empty(c.Body); });   // signed GET, empty body
        var data = await _k.Db(db => db.MessageAttachmentData.AsNoTracking().ToListAsync());
        string Text(string name) => Encoding.Latin1.GetString(data.Single(d => d.AttachmentId == atts.Single(a => a.FileName == name).Id).Bytes);
        foreach (var name in new[] { "a.jpg", "b.png", "c.webp" })
            foreach (var secret in new[] { "GPS", "Exif", "secret comment", "Jane", "jane", "xmpmeta", "Make=Phone" })
                Assert.DoesNotContain(secret, Text(name));                                           // all metadata is gone
        foreach (var secret in new[] { "GPSSECRET", "MPF", "AFTERSCAN", "TRAILER", "here" }) Assert.DoesNotContain(secret, Text("a.jpg") + Text("c.webp"));   // MPF / EXIF after the scan / data after the end of the image
        // what is stored is a fresh, valid image of the same size
        foreach (var name in new[] { "a.jpg", "b.png", "c.webp" })
        {
            using var bmp = SKBitmap.Decode(data.Single(d => d.AttachmentId == atts.Single(a => a.FileName == name).Id).Bytes);
            Assert.Equal((8, 6), (bmp.Width, bmp.Height));
        }
    }

    [Fact]
    public async Task G6_A_file_the_scanner_refuses_is_never_stored_and_the_default_scanner_allows()
    {
        var (o, g) = await Placed();
        Guid ok = Guid.NewGuid(), bad = Guid.NewGuid();
        _files[ok] = () => File(Pdf(), "application/pdf");
        _files[bad] = () => File(Pdf("/Title(EICAR)"), "application/pdf");
        _k.F.ScannerVerdict = (bytes, type) => !Encoding.Latin1.GetString(bytes).Contains("EICAR") && type == "application/pdf";
        await SellerMessage(o, g, "two files", (ok, "ok.pdf", "application/pdf", 1), (bad, "bad.pdf", "application/pdf", 1));
        await Drain();
        var atts = await Attachments();
        Assert.Equal(("stored", "rejected"), (atts.Single(a => a.FileName == "ok.pdf").Status, atts.Single(a => a.FileName == "bad.pdf").Status));
        Assert.Equal(1, await _k.Db(db => db.MessageAttachmentData.CountAsync()));                    // the refused file's bytes were never kept
    }

    [Fact]
    public async Task Files_that_are_not_what_they_say_too_big_or_active_are_refused_and_never_stored()
    {
        var (o, g) = await Placed();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        _files[ids[0]] = () => File(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "image/png");   // html with an image header
        _files[ids[1]] = () => File(Pdf("/OpenAction<</S/JavaScript/JS(app.alert(1))>>"), "application/pdf");
        _files[ids[2]] = () => File(Pdf("/Java#53cript/JS(x)"), "application/pdf");                                // hidden by a name escape
        _files[ids[3]] = () => { var j = Jpeg(false); return File([.. j[..^2], .. new byte[5 * 1024 * 1024], .. j[^2..]], "image/jpeg"); };   // a VALID jpeg, over 5 MB: only the cap refuses it
        _files[ids[4]] = () => File(Jpeg(false).Take(12).ToArray(), "image/jpeg");                                  // truncated
        await SellerMessage(o, g, "files", ids.Select((id, i) => (id, $"f{i}.bin", "image/png", 1L)).ToArray());
        await Drain();
        Assert.All(await Attachments(), a => Assert.Equal("rejected", a.Status));
        Assert.Equal(0, await _k.Db(db => db.MessageAttachmentData.CountAsync()));
        // none can be downloaded
        var thread = await _k.Db(db => db.MessageThreads.Select(t => t.Id).SingleAsync());
        var view = JsonDocument.Parse(await (await Get($"/api/v1/messages/threads/{thread}")).Content.ReadAsStringAsync()).RootElement;
        Assert.All(view.GetProperty("messages")[0].GetProperty("attachments").EnumerateArray(), a => Assert.False(a.GetProperty("available").GetBoolean()));
        foreach (var a in await Attachments()) Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/messages/attachments/{a.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_busy_portal_is_retried_and_a_file_the_portal_no_longer_has_is_closed()
    {
        var (o, g) = await Placed();
        Guid busy = Guid.NewGuid(), gone = Guid.NewGuid();
        var calls = 0;
        _files[busy] = () => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : File(Png(false), "image/png");
        _files[gone] = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        await SellerMessage(o, g, "x", (busy, "busy.png", "image/png", 1), (gone, "gone.png", "image/png", 1));
        await Drain();
        _k.F.Clock.Advance(TimeSpan.FromMinutes(10));
        await Drain();
        var atts = await Attachments();
        Assert.Equal(("stored", "rejected"), (atts.Single(a => a.FileName == "busy.png").Status, atts.Single(a => a.FileName == "gone.png").Status));
    }

    [Fact]
    public async Task A_stored_file_is_served_only_as_an_attachment_with_the_sniffed_type_nosniff_and_a_csp_and_only_to_its_buyer()
    {
        var (o, g) = await Placed();
        var id = Guid.NewGuid();
        _files[id] = () => File(Png(), "text/html");
        await SellerMessage(o, g, "pic", (id, "../../etc/pass\"wd<script>.html", "text/html", 5));
        await Drain();
        var a = (await Attachments()).Single();
        Assert.DoesNotContain("/", a.FileName);                                                       // the shown name is a plain name
        var res = await Get($"/api/v1/messages/attachments/{a.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith(".png", res.Content.Headers.ContentDisposition.FileName ?? res.Content.Headers.ContentDisposition.FileNameStar);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var other = await _k.VerifiedSession("other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/messages/attachments/{a.Id}", other)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _k.Http.GetAsync($"/api/v1/messages/attachments/{a.Id}")).StatusCode);
    }

    // ------------------------------------------------------------------ moderation

    private async Task<(Guid Thread, Guid Msg, Guid Att, string Pid)> Reported(MarketplaceOrder o, OrderGroup g)
    {
        var id = Guid.NewGuid();
        _files[id] = () => File(Png(false), "image/png");
        await SellerMessage(o, g, "pay me on venmo", (id, "x.png", "image/png", 5));
        await Drain();
        var m = await _k.Db(db => db.ThreadMessages.AsNoTracking().SingleAsync(x => x.Direction == "seller"));
        var r = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{m.Id}/report") { Content = JsonContent.Create(new { reason = "off platform" }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        Assert.Equal(HttpStatusCode.Accepted, (await _k.Http.SendAsync(r)).StatusCode);
        return (m.ThreadId, m.Id, (await Attachments()).Single().Id, m.PortalMessageId!);
    }

    private async Task<string> Decided(string kind, string reportId, string target, string decision, string? shown = null)
    {
        var (status, json) = await Post(_k.Http, Request(_k.F, Body($"cd-{++_n:D3}", "content.report_decided", _k.F.Clock.GetUtcNow().AddSeconds(-100 + _n), new { reportId, kind, targetId = target, decision, reasonShownToUser = shown })));
        Assert.Equal(HttpStatusCode.OK, status);
        return StatusOf(json);
    }

    [Fact]
    public async Task A_hidden_or_removed_message_loses_its_text_and_files_a_dismissed_one_stays_and_only_our_own_reports_count()
    {
        var (o, g) = await Placed();
        var (thread, msg, att, pid) = await Reported(o, g);
        Assert.Equal("skipped", await Decided("message", "MR-someone-else", pid, "hidden"));            // not the report id we made
        Assert.Equal("skipped", await Decided("message", "MR-pm-ghost", "pm-ghost", "hidden"));          // a message we do not have
        Assert.Equal("applied", await SellerMessage(o, g, "an ordinary message"));
        var plain = await _k.Db(db => db.ThreadMessages.AsNoTracking().Where(m => m.Direction == "seller" && m.ReportedAt == null).Select(m => m.PortalMessageId!).SingleAsync());
        Assert.Equal("skipped", await Decided("message", "MR-" + plain, plain, "hidden"));              // a real message that nobody reported on our side: never hidden by an event alone
        Assert.Null(await _k.Db(db => db.ThreadMessages.Where(m => m.PortalMessageId == plain).Select(m => m.HiddenAt).SingleAsync()));
        Assert.Equal("skipped", await Decided("review", "R-1", "review-1", "hidden"));                   // reviews are the reviews workstream's
        Assert.Equal("skipped", await Decided("listing", "L-1", "x", "hidden"));
        Assert.Equal("applied", await Decided("message", "MR-" + pid, pid, "dismissed"));
        Assert.Null(await _k.Db(db => db.ThreadMessages.Where(m => m.Id == msg).Select(m => m.HiddenAt).SingleAsync()));   // dismissed: not hidden
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/messages/attachments/{att}")).StatusCode);   // still there
        Assert.Equal("applied", await Decided("message", "MR-" + pid, pid, "removed", "Asked for payment off the platform"));
        Assert.Equal("applied", await Decided("message", "MR-" + pid, pid, "removed"));                  // again: nothing changes
        var view = JsonDocument.Parse(await (await Get($"/api/v1/messages/threads/{thread}")).Content.ReadAsStringAsync()).RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == msg);
        Assert.True(view.GetProperty("hidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("text").ValueKind);
        Assert.Equal(0, view.GetProperty("attachments").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/messages/attachments/{att}")).StatusCode);
        Assert.Equal(0, await _k.Db(db => db.MessageAttachmentData.CountAsync()));                       // the bytes are deleted
        Assert.Equal("", await _k.Db(db => db.ThreadMessages.Where(m => m.Id == msg).Select(m => m.Text).SingleAsync()));   // and so is the text: hidden is gone, not only not shown
        Assert.Equal("Asked for payment off the platform", await _k.Db(db => db.ThreadMessages.Where(m => m.Id == msg).Select(m => m.ModerationNote).SingleAsync()));
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public async Task Message_texts_and_files_are_redacted_90_days_after_the_order_closed_and_product_questions_90_days_after_the_last_message()
    {
        var (o, g) = await Placed();
        var id = Guid.NewGuid();
        _files[id] = () => File(Png(false), "image/png");
        await SellerMessage(o, g, "about your order", (id, "x.png", "image/png", 5));
        await Drain();
        var listing = await _k.Db(db => db.CatalogListings.AsNoTracking().Where(l => l.Visible).OrderBy(l => l.Slug).FirstAsync());
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages") { Content = JsonContent.Create(new { listingId = listing.Id, text = "Is it fresh?" }) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        await _k.Http.SendAsync(r);
        async Task<FulfilmentMaintenance.Result> Run() { using var s = _k.F.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<FulfilmentMaintenance>().RunAsync(); }
        _k.F.Clock.Advance(TimeSpan.FromDays(89));
        Assert.Equal(0, (await Run()).ThreadsRedacted);
        _k.F.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await Run()).ThreadsRedacted);                       // the product question (91 days since its message); the order is still open
        Assert.Equal("", await _k.Db(db => db.ThreadMessages.Where(m => m.Direction == "buyer").Select(m => m.Text).SingleAsync()));
        Assert.Equal(1, await _k.Db(db => db.MessageAttachmentData.CountAsync()));                          // the order thread is untouched
        // the seller order closes now; 90 days later its thread goes, with the files
        await Post(_k.Http, Request(_k.F, Body("ma-del", "order.delivered", _k.F.Clock.GetUtcNow().AddMinutes(-1), new { order = new { sellerOrderId = g.SellerOrderId, sellerOrderNumber = g.SellerOrderNumber, marketplaceOrderId = o.Number }, by = "seller" })));
        _k.F.Clock.Advance(TimeSpan.FromDays(89));
        Assert.Equal(0, (await Run()).ThreadsRedacted);
        _k.F.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await Run()).ThreadsRedacted);
        Assert.Equal(0, await _k.Db(db => db.MessageAttachmentData.CountAsync()));
        Assert.Equal(0, await _k.Db(db => db.MessageAttachments.CountAsync()));
        Assert.All(await _k.Db(db => db.ThreadMessages.AsNoTracking().ToListAsync()), m => { Assert.Equal("", m.Text); Assert.NotNull(m.RedactedAt); });
        Assert.All(await _k.Db(db => db.MessageThreads.AsNoTracking().ToListAsync()), t => Assert.NotNull(t.RedactedAt));      // both threads are closed (the session itself has long expired by now)
    }

    // ------------------------------------------------------------------ the sanitizer on its own

    [Fact]
    public void G2_Images_are_decoded_and_encoded_afresh_so_no_metadata_survives_in_any_container()
    {
        var jpg = AttachmentSanitizer.Process(Jpeg());
        var png = AttachmentSanitizer.Process(Png());
        var webp = AttachmentSanitizer.Process(Webp());
        Assert.Equal(("image/jpeg", "image/png", "image/webp"), (jpg.ContentType, png.ContentType, webp.ContentType));
        foreach (var r in new[] { jpg, png, webp })
        {
            var text = Encoding.Latin1.GetString(r.Bytes!);
            foreach (var secret in new[] { "GPS", "Exif", "MPF", "secret comment", "Jane", "jane", "xmpmeta", "Make=Phone", "AFTERSCAN", "TRAILER", "EXIF", "Author" }) Assert.DoesNotContain(secret, text);
        }
        Assert.Equal(1, Encoding.Latin1.GetString(jpg.Bytes!).Split("\u00ff\u00d8").Length - 1);   // exactly one image: no second picture hidden behind the first
        Assert.Equal("malformed_png", AttachmentSanitizer.Process(Png().Take(40).ToArray()).Rejected);
        Assert.Equal("malformed_jpeg", AttachmentSanitizer.Process(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xD8 }).Rejected);
        Assert.Equal("malformed_png", AttachmentSanitizer.Process([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", new byte[13]), .. Chunk("IDAT", [1]), .. Chunk("IEND", [])]).Rejected);
        Assert.Equal("unsupported_type", AttachmentSanitizer.Process(Encoding.ASCII.GetBytes("GIF89a....")).Rejected);
        Assert.Equal("empty", AttachmentSanitizer.Process([]).Rejected);
        Assert.NotNull(AttachmentSanitizer.Process(Pdf("/Subtype/JSON")).ContentType);                         // /JSON is not /JS
        Assert.Equal("pdf_active_content", AttachmentSanitizer.Process(Pdf("/AA<<>>")).Rejected);
    }

    [Theory]
    [InlineData(1, 8, 6)] [InlineData(2, 8, 6)] [InlineData(3, 8, 6)] [InlineData(4, 8, 6)]
    [InlineData(5, 6, 8)] [InlineData(6, 6, 8)] [InlineData(7, 6, 8)] [InlineData(8, 6, 8)]
    public void G2_The_exif_orientation_is_applied_before_the_metadata_is_dropped(int orientation, int expectedW, int expectedH)
    {
        // the stored picture is 8x6 with its left half red; after orientation 5-8 it is shown 6x8
        var src = JpegWithOrientation(Encode(SKEncodedImageFormat.Jpeg, 8, 6, SKColors.Red), orientation);
        var r = AttachmentSanitizer.Process(src);
        using var bmp = SKBitmap.Decode(r.Bytes!);
        Assert.Equal((expectedW, expectedH), (bmp.Width, bmp.Height));
        // where the red half ended up: orientation 1 left, 2 right, 3 right, 4 left, 5 top, 6 top, 7 bottom, 8 bottom
        bool Red(int x, int y) { var c = bmp.GetPixel(x, y); return c.Red > 200 && c.Blue < 100; }
        var (wx, wy) = (bmp.Width, bmp.Height);
        switch (orientation)
        {
            case 1 or 4: Assert.True(Red(1, wy / 2)); Assert.False(Red(wx - 2, wy / 2)); break;
            case 2 or 3: Assert.True(Red(wx - 2, wy / 2)); Assert.False(Red(1, wy / 2)); break;
            case 5 or 6: Assert.True(Red(wx / 2, 1)); Assert.False(Red(wx / 2, wy - 2)); break;
            default: Assert.True(Red(wx / 2, wy - 2)); Assert.False(Red(wx / 2, 1)); break;
        }
        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(r.Bytes!));                                      // and the orientation tag itself is gone: the pixels are already upright
    }

    [Fact]
    public void G2_The_start_up_self_test_decodes_an_embedded_image_with_the_native_library_that_ships_with_the_app()
        => Assert.True(AttachmentSanitizer.SelfTest());

    [Fact]
    public void G2_A_huge_picture_is_refused_from_its_header_and_a_big_one_is_scaled_down()
    {
        // a PNG that says it is 20000 x 20000 (400 MP): refused before any pixel is decoded
        byte[] ihdr = [0, 0, 0x4E, 0x20, 0, 0, 0x4E, 0x20, 8, 6, 0, 0, 0];
        Assert.Equal("image_too_large", AttachmentSanitizer.Process([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", [1]), .. Chunk("IEND", [])]).Rejected);
        // 5000 x 10 is under the pixel cap but over the side cap: scaled so the long side is 4096
        using var big = SKBitmap.Decode(AttachmentSanitizer.Process(Png(false, 5000, 10)).Bytes!);
        Assert.Equal(4096, big.Width);
        Assert.InRange(big.Height, 1, 10);
    }
}
