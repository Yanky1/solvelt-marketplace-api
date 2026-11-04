using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Follow-ups of the orchestrator's review of the outbox (af8d339): portal answers, admin hardening, producer pattern, email provider.</summary>
public class Review6Tests
{
    private static HttpRequestMessage Req(HttpMethod m, string url, string? key = DevDefaults.AdminApiKey, string? host = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (key is not null) r.Headers.Add(OutboxAdmin.KeyHeader, key);
        if (host is not null) r.Headers.Host = host;
        return r;
    }

    private static async Task<Guid> Enqueue(TestFactory f, string? key = null, string type = "test.msg")
    {
        using var scope = f.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<OutboxWriter>().Enqueue(type, new { n = 1 }, key);
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        return m.Id;
    }

    private static async Task<int> Dispatch(TestFactory f)
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchDueAsync();
    }

    private static async Task<JsonElement> Health(TestFactory f)
        => JsonDocument.Parse(await f.CreateClient().GetStringAsync("/health")).RootElement;

    // ---------- portal answers ----------

    [Theory]
    [InlineData(401)]
    [InlineData(413)]
    public async Task A_payment_event_the_portal_refuses_with_401_or_413_is_dead_lettered_at_once_and_counted(int status)
    {
        using var f = new TestFactory();
        for (var i = 0; i < 5; i++) f.Portal.Respond((HttpStatusCode)status, "{\"code\":\"nope\"}");
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            id = scope.ServiceProvider.GetRequiredService<OutboxWriter>().EnqueuePaymentEvent("evt-x", PaymentEventTypes.Captured, new PaymentCapturedData(PaymentKinds.Order, "MP-1", null, "SO-1", "pi_1", "usd", 100)).Id;
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        }
        Assert.Equal(0, (await Health(f)).GetProperty("outbox").GetProperty("deadLettered").GetInt64());
        await Dispatch(f);
        using var check = f.Services.CreateScope();
        var m = await check.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(("dead", $"portal_{status}", 1), (m.Status, m.LastError, m.Attempts));
        Assert.Single(f.Portal.VerifiedCalls);                                               // never re-sent
        Assert.Equal(1, (await Health(f)).GetProperty("outbox").GetProperty("deadLettered").GetInt64());
        Assert.Contains(f.Logs.Lines, l => l.Contains("is dead after") && l.Contains($"portal_{status}"));
    }

    [Fact]
    public async Task A_429_is_not_tried_again_sooner_than_the_portals_one_minute_window()
    {
        var r = PortalOutcome.From(new PortalResult<object>(429, null, new PortalError("rate_limited", "x"), 1));
        Assert.Equal((OutboxOutcome.Retry, TimeSpan.FromSeconds(60)), (r.Outcome, r.RetryAfter));
        using var f = new TestFactory();
        for (var i = 0; i < 30; i++) f.Portal.Respond((HttpStatusCode)429, "{\"code\":\"rate_limited\"}");
        Guid id;
        using (var scope = f.Services.CreateScope())
        {
            id = scope.ServiceProvider.GetRequiredService<OutboxWriter>().EnqueuePaymentEvent("evt-429", PaymentEventTypes.Captured, new PaymentCapturedData(PaymentKinds.Order, "MP-1", null, "SO-1", "pi_1", "usd", 100)).Id;
            await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().SaveChangesAsync();
        }
        var t0 = f.Clock.GetUtcNow();
        await Dispatch(f);
        using var check = f.Services.CreateScope();
        var m = await check.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("portal_429", m.LastError);
        Assert.Equal(t0.AddSeconds(60), m.NextAttemptAt);                                    // the first backoff alone would be 30 s
    }

    // ---------- admin hardening ----------

    [Fact]
    public async Task Admin_routes_exist_only_on_the_admin_host()
    {
        using var f = new TestFactory(new() { ["Admin:Host"] = "admin.internal:8443" });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox"))).StatusCode);                              // right key, public host
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", host: "shop.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", host: "admin.internal"))).StatusCode);        // port matters
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", host: "evil.admin.internal:8443"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", host: "admin.internal:8443"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", host: "ADMIN.internal:8443"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", "wrong", host: "admin.internal:8443"))).StatusCode);   // right host, wrong key
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Post, $"/api/admin/outbox/{Guid.NewGuid()}/retry", host: "shop.example"))).StatusCode);
    }

    [Fact]
    public async Task Refused_admin_requests_are_counted_on_health_and_a_key_is_never_logged()
    {
        using var f = new TestFactory();
        var c = f.CreateClient();
        Assert.Equal(0, (await Health(f)).GetProperty("admin").GetProperty("refused").GetInt64());
        await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", "guess-number-one-secret"));
        await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", null));
        await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox"));   // authorised: not counted
        Assert.Equal(2, (await Health(f)).GetProperty("admin").GetProperty("refused").GetInt64());
        var logs = string.Join("\n", f.Logs.Lines);
        Assert.DoesNotContain("guess-number-one-secret", logs);
        Assert.DoesNotContain(DevDefaults.AdminApiKey, logs);
    }

    [Fact]
    public async Task A_blank_admin_key_disables_the_endpoints()
    {
        foreach (var blank in new[] { "", "   ", "\t" })
        {
            using var f = new TestFactory(new() { ["Admin:ApiKey"] = blank });
            Assert.Equal(HttpStatusCode.NotFound, (await f.CreateClient().SendAsync(Req(HttpMethod.Get, "/api/admin/outbox", blank))).StatusCode);
        }
    }

    [Fact]
    public async Task Admin_has_its_own_rate_limit_bucket_apart_from_buyer_auth()
    {
        using var f = new TestFactory(new() { ["RateLimits:AdminPerMinute"] = "2", ["RateLimits:AuthPerMinute"] = "2" });
        var c = f.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) statuses.Add((await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox"))).StatusCode);
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, statuses);
        // the admin bucket is spent, the buyer auth bucket is untouched
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "a@example.com" })).StatusCode);
        // and spending the auth bucket does not touch admin
        using var g = new TestFactory(new() { ["RateLimits:AdminPerMinute"] = "2", ["RateLimits:AuthPerMinute"] = "2" });
        var d = g.CreateClient();
        for (var i = 0; i < 3; i++) await d.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "a@example.com" });
        Assert.Equal(HttpStatusCode.OK, (await d.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox"))).StatusCode);
    }

    [Fact]
    public async Task The_admin_listing_pages_through_every_message_once_even_when_timestamps_tie()
    {
        using var f = new TestFactory();
        // 7 messages created at the very same instant (the fake clock does not move), then 2 later ones
        for (var i = 0; i < 7; i++) await Enqueue(f);
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 2; i++) await Enqueue(f);
        var c = f.CreateClient();
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/api/admin/outbox?limit=3" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var res = await c.SendAsync(Req(HttpMethod.Get, url));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var msgs = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
            Assert.InRange(msgs.Count, 1, 3);
            seen.AddRange(msgs.Select(m => m.GetProperty("id").GetString()!));
            cursor = doc.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : doc.RootElement.GetProperty("nextCursor").GetString();
            pages++;
        } while (cursor is not null && pages < 10);
        Assert.Equal(3, pages);
        Assert.Equal(9, seen.Count);
        Assert.Equal(9, seen.Distinct().Count());                                          // none twice, none missed
        using var scope = f.Services.CreateScope();
        var newest = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().OutboxMessages.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(2).Select(m => m.Id.ToString()).ToListAsync();
        Assert.Equal(newest.Order(), seen.Take(2).Order(), StringComparer.OrdinalIgnoreCase);   // newest first
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("!!!!")]
    [InlineData("")]
    public async Task A_bad_cursor_is_a_400(string cursor)
    {
        using var f = new TestFactory();
        await Enqueue(f);
        var res = await f.CreateClient().SendAsync(Req(HttpMethod.Get, "/api/admin/outbox?cursor=" + Uri.EscapeDataString(cursor)));
        // an empty cursor means "first page"; anything else that does not decode is refused
        Assert.Equal(cursor == "" ? HttpStatusCode.OK : HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_page_size_is_clamped()
    {
        using var f = new TestFactory();
        for (var i = 0; i < 3; i++) await Enqueue(f);
        var c = f.CreateClient();
        async Task<int> Count(string q)
        {
            using var doc = JsonDocument.Parse(await (await c.SendAsync(Req(HttpMethod.Get, "/api/admin/outbox?" + q))).Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("messages").GetArrayLength();
        }
        Assert.Equal(1, await Count("limit=0"));
        Assert.Equal(1, await Count("limit=-5"));
        Assert.Equal(3, await Count("limit=100000"));
    }

    // ---------- producer pattern ----------

    [Fact]
    public async Task A_duplicate_dedupe_key_is_an_already_done_and_never_rolls_back_the_callers_change()
    {
        using var f = new TestFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var writer = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.BuyerAccounts.Add(new BuyerAccount { Id = Guid.NewGuid(), Email = "biz@example.com", DisplayName = "Biz", RelayEmail = "relay-biz@relay.example", CreatedAt = f.Clock.GetUtcNow(), UpdatedAt = f.Clock.GetUtcNow() });   // the business change
        await db.SaveChangesAsync();
        Assert.True(await writer.TryEnqueueAsync("test.msg", new { n = 1 }, "once"));
        Assert.False(await writer.TryEnqueueAsync("test.msg", new { n = 2 }, "once"));       // taken: no exception, no rollback
        Assert.False(await writer.TryEnqueueAsync("test.msg", new { n = 3 }, "once", TimeSpan.FromHours(1)));
        await tx.CommitAsync();
        using var check = f.Services.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await cdb.BuyerAccounts.CountAsync(a => a.Email == "biz@example.com"));          // the business change committed
        var m = await cdb.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(("once", "{\"n\":1}", "pending", 0, 0), (m.DedupeKey, m.Payload, m.Status, m.Attempts, m.Version));                  // the first one won
        Assert.Equal(f.Clock.GetUtcNow(), m.NextAttemptAt);
        Assert.Equal(f.Clock.GetUtcNow(), m.CreatedAt);
        Assert.NotEqual(Guid.Empty, m.Id);
    }

    [Fact]
    public async Task A_try_enqueued_message_is_delivered_like_any_other_and_honours_its_delay()
    {
        var h = new ScriptedOutboxHandler().Then(OutboxResult.Delivered());
        using var f = new TestFactory(services: s => s.AddScoped<IOutboxHandler>(_ => h));
        using (var scope = f.Services.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<OutboxWriter>().TryEnqueueAsync("test.msg", new { n = 1 }, "later", TimeSpan.FromSeconds(60)));
        }
        f.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, await Dispatch(f));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Dispatch(f));
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Parallel_producers_of_one_dedupe_key_queue_one_message_and_all_keep_their_own_changes()
    {
        using var f = new TestFactory();
        _ = f.Services;                                          // build the host once, before the parallel part
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            await using var tx = await db.Database.BeginTransactionAsync();
            db.BuyerAccounts.Add(new BuyerAccount { Id = Guid.NewGuid(), Email = $"p{i}@example.com", DisplayName = "P", RelayEmail = $"relay-p{i}@relay.example", CreatedAt = f.Clock.GetUtcNow(), UpdatedAt = f.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
            var queued = await scope.ServiceProvider.GetRequiredService<OutboxWriter>().TryEnqueueAsync("test.msg", new { i }, "same-key");
            await tx.CommitAsync();
            return queued;
        })));
        Assert.Equal(1, results.Count(r => r));
        using var check = f.Services.CreateScope();
        var cdb = check.ServiceProvider.GetRequiredService<MarketplaceDb>();
        Assert.Equal(1, await cdb.OutboxMessages.CountAsync());
        Assert.Equal(10, await cdb.BuyerAccounts.CountAsync());                                   // no caller lost its change
    }

    // ---------- configuration ----------

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> GoodProd() => new()
    {
        ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40), ["Auth:GuestLinkKey"] = new string('G', 40),
        ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
        ["Portal:BaseUrl"] = "https://portal.example", ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
        ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp",
    };

    private static IReadOnlyList<string> Validate(Dictionary<string, string?> d, string env = "Production")
        => StartupValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(d).Build(), new Env(env));

    [Fact]
    public void Production_refuses_to_start_without_an_email_provider()
    {
        Assert.Empty(Validate(GoodProd()));
        var none = GoodProd(); none.Remove("Email:Provider");
        Assert.Contains(Validate(none), p => p.Contains("Email:Provider") && p.Contains("required"));
        var blank = GoodProd(); blank["Email:Provider"] = "  ";
        Assert.Contains(Validate(blank), p => p.Contains("Email:Provider") && p.Contains("required"));
        var other = GoodProd(); other["Email:Provider"] = "Carrier pigeon";
        Assert.Contains(Validate(other), p => p.Contains("not valid"));
        var noHost = GoodProd(); noHost.Remove("Email:SmtpHost");
        Assert.Contains(Validate(noHost), p => p.Contains("Email:SmtpHost"));
        var noFrom = GoodProd(); noFrom.Remove("Email:From");
        Assert.Contains(Validate(noFrom), p => p.Contains("Email:From"));
        var badFrom = GoodProd(); badFrom["Email:From"] = "not an address";
        Assert.Contains(Validate(badFrom), p => p.Contains("Email:From"));
        Assert.Empty(Validate(none, "Development"));                                          // the sink is for Development and Testing
        Assert.Empty(Validate(none, "Testing"));
    }

    [Fact]
    public void The_admin_key_needs_its_own_host_that_is_not_the_public_host()
    {
        var ok = GoodProd(); ok["Admin:ApiKey"] = new string('d', 40);
        Assert.Contains(Validate(ok), p => p.Contains("Admin:Host") && p.Contains("required"));
        var blankHost = new Dictionary<string, string?>(ok) { ["Admin:Host"] = "  " };
        Assert.Contains(Validate(blankHost), p => p.Contains("Admin:Host"));
        var publicHost = new Dictionary<string, string?>(ok) { ["Admin:Host"] = "SHOP.example" };
        Assert.Contains(Validate(publicHost), p => p.Contains("must not be the public host"));
        var good = new Dictionary<string, string?>(ok) { ["Admin:Host"] = "admin.internal:8443" };
        Assert.Empty(Validate(good));
        var blankKeyNoHost = GoodProd(); blankKeyNoHost["Admin:ApiKey"] = "   ";
        Assert.Empty(Validate(blankKeyNoHost));                                               // a blank key is "not set", no host needed
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void The_lease_must_outlast_the_longest_handler_in_every_environment(string env)
    {
        var d = GoodProd();
        Assert.Empty(Validate(d, env));
        d["Outbox:LeaseSeconds"] = "59";                                                      // default portal deadline 30 + margin 30 = 60
        Assert.Contains(Validate(d, env), p => p.Contains("Outbox:LeaseSeconds") && p.Contains("Portal:TotalDeadlineSeconds"));
        d["Outbox:LeaseSeconds"] = "60";
        Assert.Empty(Validate(d, env));
        d["Portal:TotalDeadlineSeconds"] = "45";                                              // 45 + 30 = 75
        Assert.Contains(Validate(d, env), p => p.Contains("Portal:TotalDeadlineSeconds (45)"));
        d["Portal:TotalDeadlineSeconds"] = "600";                                             // the client clamps it to 60, so 90 is the need
        Assert.Contains(Validate(d, env), p => p.Contains("Portal:TotalDeadlineSeconds (60)"));
        d["Portal:TotalDeadlineSeconds"] = "30"; d["Outbox:LeaseSeconds"] = "120"; d["Email:SendTimeoutSeconds"] = "91";
        Assert.Contains(Validate(d, env), p => p.Contains("Email:SendTimeoutSeconds (91)"));
        d["Email:SendTimeoutSeconds"] = "90";
        Assert.Empty(Validate(d, env));
        d["Email:SendTimeoutSeconds"] = "0";
        Assert.Contains(Validate(d, env), p => p.Contains("at least 1"));
    }

    // ---------- SMTP ----------

    /// <summary>A tiny SMTP server: records the message and answers every command with success.</summary>
    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string? Transcript;
        private readonly Task _loop;
        private readonly bool _silent;

        public FakeSmtp(bool silent = false)
        {
            _silent = silent;
            _listener.Start();
            _loop = Task.Run(Serve);
        }

        private async Task Serve()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                if (_silent) { await Task.Delay(Timeout.Infinite); return; }
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                var log = new StringBuilder();
                await writer.WriteLineAsync("220 fake ESMTP");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    log.AppendLine(line);
                    var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                    if (cmd == "EHLO" || cmd == "HELO") await writer.WriteLineAsync("250 fake");
                    else if (cmd == "DATA")
                    {
                        await writer.WriteLineAsync("354 go");
                        while ((line = await reader.ReadLineAsync()) is not null && line != ".") log.AppendLine(line);
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                    else await writer.WriteLineAsync("250 ok");
                }
                Transcript = log.ToString();
            }
            catch (Exception) { /* the test is over */ }
        }

        public void Dispose() { _listener.Stop(); try { _loop.Wait(1000); } catch { } }
    }

    [Fact]
    public async Task The_smtp_sender_delivers_the_message_to_the_configured_server()
    {
        using var smtp = new FakeSmtp();
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions { SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpUseTls = false, From = "no-reply@shop.example", SendTimeoutSeconds = 10 }));
        await sender.SendAsync(new EmailMessage("jane@example.com", "Hello there", "Body line one"), CancellationToken.None);
        for (var i = 0; i < 50 && smtp.Transcript is null; i++) await Task.Delay(50);
        Assert.NotNull(smtp.Transcript);
        Assert.Contains("MAIL FROM:<no-reply@shop.example>", smtp.Transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RCPT TO:<jane@example.com>", smtp.Transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Subject: Hello there", smtp.Transcript);
        Assert.Contains("Body line one", smtp.Transcript);
    }

    [Fact]
    public async Task A_smtp_server_that_never_answers_is_cut_off_by_the_send_timeout()
    {
        using var smtp = new FakeSmtp(silent: true);
        var options = new EmailOptions { SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpUseTls = false, From = "no-reply@shop.example", SendTimeoutSeconds = 1 };
        var sender = new SmtpEmailSender(Options.Create(options));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => EmailSending.SendAsync(sender, new EmailMessage("jane@example.com", "Hello", "Body"), options, CancellationToken.None));
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"the hung send took {sw.Elapsed.TotalSeconds:F1} s");   // the 1 s timeout (the client's own or ours) ends it
    }

    [Fact]
    public async Task The_send_timeout_never_swallows_a_real_cancellation()
    {
        var options = new EmailOptions { SendTimeoutSeconds = 30 };
        using var cts = new CancellationTokenSource();
        var task = EmailSending.SendAsync(new HangingEmailSender(), new EmailMessage("a@example.com", "s", "b"), options, cts.Token);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);                  // cancelled by the caller: not turned into a timeout
    }
}
