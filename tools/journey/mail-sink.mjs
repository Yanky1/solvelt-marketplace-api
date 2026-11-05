// A tiny SMTP sandbox for the journey (never relays anything): accepts mail on SMTP_PORT, keeps it in memory, lists it as JSON on HTTP_PORT (GET /messages, DELETE /messages).
// No dependencies. The API sends mail here (Email__SmtpHost=127.0.0.1, Email__SmtpUseTls=false); no real address is ever contacted.
import net from "node:net";
import http from "node:http";

const SMTP_PORT = Number(process.env.SMTP_PORT ?? 2525);
const HTTP_PORT = Number(process.env.HTTP_PORT ?? 8025);
const messages = [];

net.createServer((sock) => {
  let buf = "", inData = false, from = "", to = [];
  const send = (s) => sock.write(s + "\r\n");
  send("220 journey-mail-sink ESMTP");
  sock.on("error", () => {});
  sock.on("data", (chunk) => {
    buf += chunk.toString("utf8");
    for (;;) {
      if (inData) {
        const end = buf.indexOf("\r\n.\r\n");
        if (end < 0) return;
        const raw = buf.slice(0, end); buf = buf.slice(end + 5); inData = false;
        const headers = raw.split("\r\n\r\n")[0] ?? "";
        const subject = /^Subject:\s*(.*)$/im.exec(headers)?.[1] ?? "";
        messages.push({ from, to: [...to], subject, raw, receivedAt: new Date().toISOString() });
        to = []; send("250 OK queued");
        continue;
      }
      const nl = buf.indexOf("\r\n");
      if (nl < 0) return;
      const line = buf.slice(0, nl); buf = buf.slice(nl + 2);
      const up = line.toUpperCase();
      if (up.startsWith("EHLO")) { sock.write("250-journey-mail-sink\r\n250 8BITMIME\r\n"); }
      else if (up.startsWith("HELO")) send("250 journey-mail-sink");
      else if (up.startsWith("MAIL FROM")) { from = line.slice(10).replace(/[<>\s]/g, ""); send("250 OK"); }
      else if (up.startsWith("RCPT TO")) { to.push(line.slice(8).replace(/[<>\s]/g, "")); send("250 OK"); }
      else if (up === "DATA") { inData = true; send("354 End data with <CR><LF>.<CR><LF>"); }
      else if (up === "QUIT") { send("221 Bye"); sock.end(); }
      else send("250 OK");
    }
  });
}).listen(SMTP_PORT, "127.0.0.1", () => console.log(`mail sink: smtp ${SMTP_PORT}, http ${HTTP_PORT}`));

// A stand-in for Stripe.js (the checkout page loads it from STRIPE_JS_URL; the web accepts a non-Stripe origin only with ALLOW_STRIPE_STUB=1 on localhost). A "Payment Element" with one input;
// confirmPayment first lets the journey runner (window.journeyPay, exposed by Playwright) deliver the signed payment_intent.succeeded webhook, then goes to the return_url as Stripe does.
const STRIPE_STUB = `window.Stripe = function (pk) { window.__stripePk = pk; return { elements: function (o) { window.__stripeSecret = o.clientSecret; return { create: function () { return { mount: function (sel) { var el = document.querySelector(sel); var l = document.createElement("label"); l.textContent = "Card number "; var i = document.createElement("input"); i.setAttribute("autocomplete", "cc-number"); l.appendChild(i); el.appendChild(l); } }; } }; }, confirmPayment: function (a) { var go = function () { location.assign(a.confirmParams.return_url + "?redirect_status=succeeded"); }; return new Promise(function () { if (window.journeyPay) { window.journeyPay(window.__stripeSecret, a.confirmParams.return_url).then(go, go); } else go(); }); } }; };`;

http.createServer((req, res) => {
  if (req.url === "/__stripe/v3.js") { res.writeHead(200, { "content-type": "application/javascript" }).end(STRIPE_STUB); return; }
  if (req.method === "DELETE") { messages.length = 0; res.writeHead(204).end(); return; }
  res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(messages));
}).listen(HTTP_PORT, "127.0.0.1");
