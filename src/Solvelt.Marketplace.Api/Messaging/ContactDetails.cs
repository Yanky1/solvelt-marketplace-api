using System.Text.RegularExpressions;

namespace Solvelt.Marketplace.Api.Messaging;

/// <summary>
/// Off-platform contact and payment details in a message (mirrors the portal's warning, ORDERS.md §6): the first send answers <c>409 contact_details_warning</c> with the kinds found, and the buyer
/// may send anyway with <c>acknowledgeWarning: true</c>. Detection only warns: it is not a security control.
/// </summary>
public static partial class ContactDetails
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"(?<!\d)(?:\+?\d[\s().\-]?){7,}\d(?!\d)")] private static partial Regex Phone();
    [GeneratedRegex(@"(?:https?://|www\.)\S+|\b[a-z0-9\-]+\.(?:com|net|org|io|me|co|ly|app|link)\b/?\S*", RegexOptions.IgnoreCase)] private static partial Regex Link();
    [GeneratedRegex(@"\b(?:paypal|venmo|zelle|cash\s?app|cashapp|western\s?union|wire\s?transfer|whatsapp|telegram|signal app|bitcoin|crypto)\b", RegexOptions.IgnoreCase)] private static partial Regex Payment();

    public static IReadOnlyList<string> Find(string text)
    {
        var found = new List<string>();
        if (Email().IsMatch(text)) found.Add("email");
        if (Phone().IsMatch(text)) found.Add("phone");
        if (Link().IsMatch(text)) found.Add("link");
        if (Payment().IsMatch(text)) found.Add("payment_or_chat_app");
        return found;
    }
}
