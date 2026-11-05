// The stand-in Stripe.js asks the runner to "pay" (window.journeyPay): the runner delivers Stripe's signed payment_intent.succeeded for what the return URL names.
// The amount always comes from the API's own record of the order or booking (read with the buyer's token), never from the page.
import * as L from "./lib.mjs";

export async function exposePay(page, getToken) {
  await page.exposeFunction("journeyPay", async (clientSecret, returnUrl) => {
    const intentId = String(clientSecret).split("_secret_")[0];
    const path = new URL(returnUrl).pathname;
    const token = getToken();
    let amount, metadata;
    if (path.startsWith("/checkout/confirmation/")) {
      const number = decodeURIComponent(path.split("/").pop());
      const order = (await L.must(L.api("GET", `/api/v1/checkout/orders/${number}`, { token }), 200, "read the order to pay")).body;
      amount = order.totalCents; metadata = { marketplace_order_id: number };
    } else if (path.startsWith("/bookings/")) {
      const id = decodeURIComponent(path.split("/").pop());
      const booking = (await L.must(L.api("GET", `/api/v1/bookings/${id}`, { token }), 200, "read the booking to pay")).body;
      const mid = L.sql(`select "MarketplaceBookingId" from bookings where "Id" = '${id.replace(/[^0-9a-f-]/gi, "")}'`);
      L.check(mid, "the booking has a marketplace booking id");
      amount = booking.totalCents; metadata = { marketplace_booking_id: mid, kind: "booking" };
    } else throw new Error(`journeyPay: do not know what ${path} pays for`);
    const r = await L.stripeEvent("payment_intent.succeeded", { id: intentId, amount, amount_received: amount, currency: "usd", metadata });
    L.check(r.status === 200, `the Stripe webhook answered ${r.status}: ${JSON.stringify(r.body)}`);
  });
}
