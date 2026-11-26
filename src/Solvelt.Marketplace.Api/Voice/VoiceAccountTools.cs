using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Account;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Carts;
using Solvelt.Marketplace.Api.Checkout;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

public enum AccountApply { Saved, Created, Refused, EmailSent, CreatedWithEmail, CreatedEmailNotSaved }

/// <summary>
/// Account setup and editing by phone (slice 6, decision V-09). It owns no rules of its own: names, addresses and the phone proof go through <see cref="AccountService"/> and
/// <see cref="AccountPhoneVerifier"/>. The one thing that is new is creating an account WITHOUT an email or a password: the number the caller proved by SMS code in this call is its only
/// credential, so there is no mailbox for anyone to claim and no password to guess.
/// </summary>
public sealed class PhoneAccountService(MarketplaceDb db, AccountService accounts, AccountPhoneVerifier verifier, AuthService auth, PhonePinService pins, ISmsSender sms, CartService carts, IOptions<MarketplaceOptions> marketplace, TimeProvider time)
{
    public static string? NameProblem(string? raw)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(raw ?? "", @"\s+", " ").Trim();
        return name.Length is < 2 or > 60 || name.Any(char.IsControl) || name.Any(char.IsDigit) ? "That did not sound like a name: ask them to say it again." : null;
    }

    /// <summary>A new account for a number this call has just proven. Null when that number already belongs to an account (it must be opened, not duplicated).</summary>
    public async Task<Guid?> CreateAsync(string name, string e164, CancellationToken ct)
    {
        if (await db.BuyerAccounts.AnyAsync(a => a.PhoneVerifiedE164 == e164, ct)) return null;
        var now = time.GetUtcNow();
        var account = new BuyerAccount
        {
            Id = Guid.NewGuid(), Email = null, PasswordHash = null, DisplayName = AuthValidation.NormaliseText(name), Phone = e164, PhoneVerifiedE164 = e164, PhoneVerifiedAt = now,
            CreatedAt = now, UpdatedAt = now, RelayEmail = RelayIdentity.NewRelayEmail(marketplace.Value.RelayDomain ?? DevDefaults.RelayDomain),
        };
        db.BuyerAccounts.Add(account);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return null; }   // the same number was claimed in parallel (unique index)
        return account.Id;
    }

    /// <summary>Applies a change the caller heard read back and confirmed. Every path re-checks the call's own state: nothing here trusts the model.</summary>
    public async Task<AccountApply> ApplyAsync(VoiceCallState s, AccountChangeRequest c, CancellationToken ct)
    {
        if (c.Kind == "create")
        {
            if (s.VerifiedPhone is not { } phone || s.AccountId is not null || !s.VerifiedInThisCall || c.Name is null) return AccountApply.Refused;
            if (await CreateAsync(c.Name, phone, ct) is not { } id) return AccountApply.Refused;
            if (s.CartToken is { } guestToken) await carts.MergeGuestAsync(id, guestToken, ct);   // the guest cart carries over into the new account
            s.SetCartToken(null);
            s.Identify(CallerLevel.Verified, id, verifiedNow: true);
            s.SetFirstName(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            s.SetCart("cart", (await carts.GetAsync(new CartOwner(id, null), ct)).ItemCount);
            s.SetTask("shopping");
            if (c.Email is null) return AccountApply.Created;
            // the spoken address is saved as PENDING and a link is mailed to it (V-13b); the account itself is already created either way
            return await auth.RequestPhoneEmailAsync(id, c.Email, ct) == PhoneEmailResult.Sent ? AccountApply.CreatedWithEmail : AccountApply.CreatedEmailNotSaved;
        }
        if (!OrderAccess.IsAccount(s) || s.AccountId is not { } account) return AccountApply.Refused;
        var profile = await accounts.GetProfileAsync(account, ct);
        if (profile is null) return AccountApply.Refused;
        switch (c.Kind)
        {
            case "name":
                if (c.Name is null || await accounts.UpdateProfileAsync(account, c.Name, profile.Phone, ct) is null) return AccountApply.Refused;
                s.AccountDetailsChanged(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
                return AccountApply.Saved;
            case "phone":
                // the new number was proven by a code typed in THIS call, signed in as this account: the precondition of AccountPhoneVerifier (V-05)
                if (!c.PhoneProved || c.Phone is null) return AccountApply.Refused;
                if (await accounts.UpdateProfileAsync(account, profile.DisplayName, c.Phone, ct) is null || !await verifier.MarkVerifiedAsync(account, c.Phone, ct)) return AccountApply.Refused;
                s.SetVerifiedPhone(c.Phone);
                s.AccountDetailsChanged(null);
                return AccountApply.Saved;
            case "email":
                return c.Email is not null && await auth.RequestPhoneEmailAsync(account, c.Email, ct) == PhoneEmailResult.Sent ? AccountApply.EmailSent : AccountApply.Refused;
            case "pin_remove":
                if (s.Pin is not { Action: "remove" }) return AccountApply.Refused;
                await pins.ClearAsync(account, ct);
                s.SetPin(null);
                if (s.VerifiedPhone is { } removedFor) await sms.SendAsync(removedFor, "Solvelt: the phone PIN on your account was just removed. If this was not you, contact us.", ct);
                return AccountApply.Saved;
            case "card_remove":
                return c.CardId is { } cid && await db.SavedCards.Where(x => x.Id == cid && x.AccountId == account).ExecuteDeleteAsync(ct) == 1 ? AccountApply.Saved : AccountApply.Refused;
            case "address_add":
                return c.Body is not null && (await accounts.CreateAddressAsync(account, c.Body, ct)).Outcome == AccountOutcome.Ok ? AccountApply.Saved : AccountApply.Refused;
            case "address_edit":
                return c.AddressId is { } e && c.Body is not null && await accounts.UpdateAddressAsync(account, e, c.Body, ct) is not null ? AccountApply.Saved : AccountApply.Refused;
            case "address_remove":
                return c.AddressId is { } r && await accounts.DeleteAddressAsync(account, r, ct) ? AccountApply.Saved : AccountApply.Refused;
            case "address_default":
                return c.AddressId is { } d && await accounts.SetDefaultAsync(account, d, ct) ? AccountApply.Saved : AccountApply.Refused;
        }
        return AccountApply.Refused;
    }
}

internal static class AccountTalk
{
    public const string Ask = " To save it, say yes. To leave it as it is, say no.";

    public static VoiceToolResult ReadingBack(VoiceCallState s, AccountChangeRequest change)
    {
        s.SetAccountChange(change);
        s.SetTask("account");
        s.BeginKeypadStep("confirm_account");
        return new(new JsonObject { ["status"] = "reading_back", ["instruction"] = "Tell the caller in one short sentence that you will read the change back now. Do not say it yourself. Then stop talking." }.ToJsonString(), EndCall: true);
    }

    public static VoiceToolResult? Gate(VoiceCallState s, bool guestOk = false)
    {
        if (OrderAccess.IsAccount(s)) return s.CountAccountChangeStart() > 6 ? ToolKit.Error("too_many_attempts", "Too many account changes in this call: offer the web site or a person.") : null;
        if (guestOk && OrderAccess.IsGuest(s)) return null;
        return ToolKit.Error("need_verification", s.Level == CallerLevel.Guest
            ? "Changing an account needs an account verified by text message in this call. This caller has no account yet: offer to create one (create_account)."
            : "Changing an account needs the text-message code first: offer identify_caller.");
    }
}

/// <summary>Turns a guest (a caller whose number was verified by SMS code in this call) into an account. Name and number only; no email and no password are collected by phone.</summary>
public sealed class CreateAccountTool(MarketplaceDb db) : IVoiceTool
{
    public string Name => "create_account";
    public JsonObject Definition => ToolKit.Def(Name, "Create an account for a caller who ordered as a guest. Needs the caller's name and a phone number verified by text message in this call. The server reads the account back and asks for a yes on the keypad; say one short sentence that you will read it back, then stop talking. The cart carries over.",
        new JsonObject { ["name"] = ToolKit.Prop("string", "The caller's full name as they said it."), ["email"] = ToolKit.Prop("string", "The caller's email address as they spelled it, if they want it saved (optional; ask once). The server reads it back.") }, "name");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (OrderAccess.IsAccount(s)) return ToolKit.Error("already_account", "The caller already has an account and is signed in.");
        if (!OrderAccess.IsGuest(s)) return ToolKit.Error("need_verification", "Creating an account needs the caller's number verified by text message first: call identify_caller.");
        if (s.CountAccountChangeStart() > 4) return ToolKit.Error("too_many_attempts", "Too many attempts in this call: offer the web site or a person.");
        var name = System.Text.RegularExpressions.Regex.Replace(ToolKit.Str(args, "name") ?? "", @"\s+", " ").Trim();
        if (PhoneAccountService.NameProblem(name) is { } problem) return ToolKit.Error("bad_name", problem);
        if (await db.BuyerAccounts.AsNoTracking().AnyAsync(a => a.PhoneVerifiedE164 == s.VerifiedPhone, ct))
            return ToolKit.Error("number_in_use", "Say that this number already belongs to an account that cannot be opened by phone yet; offer the web site or a person. Do not create another.");
        var last4 = VoiceSpeech.Digits(new string(s.VerifiedPhone!.Where(char.IsDigit).TakeLast(4).ToArray()));
        var email = ToolKit.Str(args, "email") is { Length: > 0 } raw ? AuthValidation.NormaliseEmail(raw) : null;
        if (email is not null && AuthValidation.EmailProblem(email) is not null) return ToolKit.Error("bad_email", "That does not sound like an email address: ask them to spell it again, slowly, or to leave the email for now.");
        var emailPart = email is null ? " It has no password and no email for now: you can add those on our web site." : $" I will also save the email address {VoiceSpeech.Email(email)}. It only starts to count after you open the link I email to that address; until then it is not used for anything. It has no password for now: you can add one on our web site.";
        var text = $"I will create a Solvelt account for {VoiceSpeech.Plain(name, 60)}, signed in with the phone number ending {last4}.{emailPart} Your cart stays with you." + AccountTalk.Ask;
        return AccountTalk.ReadingBack(s, new AccountChangeRequest("create", text, Name: name, Email: email));
    }
}

public sealed class ChangeNameTool : IVoiceTool
{
    public string Name => "change_my_name";
    public JsonObject Definition => ToolKit.Def(Name, "Change the name on the caller's account. Only for a caller verified by text message in this call. The server reads it back and asks for a yes on the keypad; say one short sentence that you will read it back, then stop talking.",
        new JsonObject { ["name"] = ToolKit.Prop("string", "The new full name as they said it.") }, "name");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (AccountTalk.Gate(s) is { } refused) return Task.FromResult(refused);
        var name = System.Text.RegularExpressions.Regex.Replace(ToolKit.Str(args, "name") ?? "", @"\s+", " ").Trim();
        if (PhoneAccountService.NameProblem(name) is { } problem) return Task.FromResult(ToolKit.Error("bad_name", problem));
        return Task.FromResult(AccountTalk.ReadingBack(s, new AccountChangeRequest("name", $"I will change the name on your account to {VoiceSpeech.Plain(name, 60)}." + AccountTalk.Ask, Name: name)));
    }
}

/// <summary>Add, edit, remove or set the default of a saved address. The saved list must have been read out first (the model names a position; the server holds the ids, and only the account's own ids).</summary>
public sealed class ManageSavedAddressTool(AccountService accounts, ZipResolver zips, IPostcodeLookup postcode, ILogger<ManageSavedAddressTool> log) : IVoiceTool
{
    public string Name => "manage_saved_address";
    public JsonObject Definition => ToolKit.Def(Name, "Add, edit, remove or make default one of the caller's saved addresses. Only for a caller verified by text message in this call. For edit, remove and set_default read the list first with list_saved_addresses and give the position. The server reads the change back and asks for a yes on the keypad; say one short sentence that you will read it back, then stop talking.",
        new JsonObject
        {
            ["action"] = ToolKit.Prop("string", "One of: add, edit, remove, set_default."), ["position"] = ToolKit.Prop("integer", "Position in the list just read out (edit, remove, set_default)."),
            ["street"] = ToolKit.Prop("string", "House number and street (add, edit)."), ["apartment"] = ToolKit.Prop("string", "Apartment, suite or floor."), ["city"] = ToolKit.Prop("string", "City."),
            ["state"] = ToolKit.Prop("string", "Two-letter state code."), ["postal_code"] = ToolKit.Prop("string", "ZIP or postal code."), ["country"] = ToolKit.Prop("string", "Two-letter country code; US if not said."),
        }, "action");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (AccountTalk.Gate(s) is { } refused) return refused;
        var id = s.AccountId!.Value;
        var action = ToolKit.Str(args, "action");
        if (action is not ("add" or "edit" or "remove" or "set_default")) return ToolKit.Error("bad_action", "Ask whether they want to add, change, remove or make default an address.");
        AddressView? current = null;
        if (action != "add")
        {
            var pos = ToolKit.Int(args, "position", 0);
            if (pos < 1 || pos > s.SavedAddressIds.Count) return ToolKit.Error("no_such_position", "Read the saved addresses first with list_saved_addresses, then ask which one.");
            current = await accounts.GetAddressAsync(id, s.SavedAddressIds[pos - 1], ct);   // by account id: another buyer's address is simply not found
            if (current is null) return ToolKit.Error("not_available", "That address is not there any more: read the list again.");
        }
        var spoken = current is null ? "" : VoiceSpeech.Street(VoiceSpeech.Plain(current.Line1, 80)) + ", " + VoiceSpeech.Plain(current.City, 40);
        if (action == "remove") return AccountTalk.ReadingBack(s, new AccountChangeRequest("address_remove", $"I will remove the saved address {spoken}." + AccountTalk.Ask, AddressId: current!.Id));
        if (action == "set_default") return AccountTalk.ReadingBack(s, new AccountChangeRequest("address_default", $"I will make the saved address {spoken} your default." + AccountTalk.Ask, AddressId: current!.Id));

        var profile = await accounts.GetProfileAsync(id, ct);
        var addr = new CheckoutAddress(current?.FullName ?? profile?.DisplayName, ToolKit.Str(args, "street") ?? current?.Line1, ToolKit.Str(args, "apartment") ?? current?.Line2, ToolKit.Str(args, "city") ?? current?.City,
            (ToolKit.Str(args, "state") ?? current?.Region)?.Trim().ToUpperInvariant(), ToolKit.Str(args, "postal_code") ?? current?.PostalCode, (ToolKit.Str(args, "country") ?? current?.Country ?? "US").Trim().ToUpperInvariant(), current?.Phone ?? s.VerifiedPhone);
        var errors = CheckoutService.Validate(addr, null);
        if (errors.Count == 0 && addr.Country == "US" && !UsStates.Codes.Contains(addr.Region ?? "")) errors = new Dictionary<string, string> { ["address.region"] = "state" };
        if (errors.Count > 0) return ToolKit.Json(new JsonObject { ["error"] = "invalid_address", ["problems"] = new JsonArray(CheckoutAccess.Problems(errors).Select(p => (JsonNode)p).ToArray()), ["instruction"] = "Ask the caller again for the parts that are wrong, then call this tool again." });
        var changed = ToolKit.Str(args, "street") is not null || ToolKit.Str(args, "city") is not null || ToolKit.Str(args, "postal_code") is not null || ToolKit.Str(args, "state") is not null;
        if (changed)
        {
            // V-63: the same check as for a delivery address, only when the address itself changes (an apartment alone is not re-checked); Google's city and state are what is read back
            var gate = await AddressGate.RunAsync(s, addr, zips, postcode, log, ct);
            if (gate.Refusal is not null) return gate.Refusal;
            addr = gate.Address!;
        }
        var body = new AddressBody(addr.Name, addr.Line1, addr.Line2, addr.City, addr.Region, addr.PostalCode, addr.Country, addr.Phone, changed ? null : current?.Lat, changed ? null : current?.Lng);
        var say = VoiceSpeech.Address(addr);
        return action == "add"
            ? AccountTalk.ReadingBack(s, new AccountChangeRequest("address_add", $"I will add this address to your account: {say}." + AccountTalk.Ask, Body: body))
            : AccountTalk.ReadingBack(s, new AccountChangeRequest("address_edit", $"I will change the saved address {spoken} to: {say}." + AccountTalk.Ask, AddressId: current!.Id, Body: body));
    }
}

/// <summary>Saving or changing the account's email by phone (decision V-13b). The address the caller spelled is read back by the SERVER and applied only on yes. It is saved as PENDING: a link is mailed to it, and it only becomes the account's email, verified, when that link is confirmed. Until then it cannot sign in or reset a password, and an address another account already holds is never attached (the caller hears the same thing either way).</summary>
public sealed class ChangeEmailTool : IVoiceTool
{
    public string Name => "change_my_email";
    public JsonObject Definition => ToolKit.Def(Name, "Save or change the email address on the caller's account. Only for a caller verified by text message in this call. Ask the caller to spell it slowly. The server reads it back and asks for a yes on the keypad; say one short sentence that you will read it back, then stop. The address only counts after the caller opens the link that is emailed to it.",
        new JsonObject { ["email"] = ToolKit.Prop("string", "The email address as the caller spelled it.") }, "email");

    public Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (AccountTalk.Gate(s) is { } refused) return Task.FromResult(refused);
        var email = ToolKit.Str(args, "email") is { } raw ? AuthValidation.NormaliseEmail(raw) : null;
        if (AuthValidation.EmailProblem(email) is not null) return Task.FromResult(ToolKit.Error("bad_email", "That does not sound like an email address: ask them to spell it again, slowly."));
        var text = $"I will save the email address {VoiceSpeech.Email(email!)} on your account, and send a link to it. It only starts to count after you open that link; until then nothing else changes." + AccountTalk.Ask;
        return Task.FromResult(AccountTalk.ReadingBack(s, new AccountChangeRequest("email", text, Email: email)));
    }
}

/// <summary>Changing the account's number: the NEW number must be proven by a code typed in this call before it is even read back, and only then does the caller's yes save it.</summary>
public sealed class ChangePhoneTool(PhoneCodeService codes) : IVoiceTool
{
    public string Name => "change_my_phone";
    public JsonObject Definition => ToolKit.Def(Name, "Change the phone number of the caller's account. Only for a caller verified by text message in this call. Texts a code to the NEW number and moves the call to the keypad; the new number only counts once its code is typed. Never ask for the code aloud.",
        new JsonObject { ["new_number"] = ToolKit.Prop("string", "The new phone number with area code.") }, "new_number");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (AccountTalk.Gate(s) is { } refused) return refused;
        var number = PhoneNumbers.ToE164(ToolKit.Str(args, "new_number"));
        if (number is null) return ToolKit.Error("no_number", "That is not a phone number: ask the caller to say it again with the area code.");
        if (number == s.VerifiedPhone) return ToolKit.Error("same_number", "That is the number the account already has.");
        var sent = await codes.SendAsync(s, number, "phone_change", s.AccountId, ct);
        if (sent == SendResult.TooMany) return ToolKit.Error("too_many_codes", "No more codes can be sent right now. Offer the web site or a person.");
        if (sent == SendResult.Failed) return ToolKit.Error("sms_failed", "The text could not be sent. Offer to try once more, or a person.");
        var text = $"I will change the phone number on your account to {VoiceSpeech.Digits(new string(number.Where(char.IsDigit).ToArray()))}. From then on you are recognised by that number." + AccountTalk.Ask;
        s.SetAccountChange(new AccountChangeRequest("phone", text, Phone: number));
        s.SetTask("account");
        s.BeginKeypadStep("sms_code");
        return new(new JsonObject
        {
            ["status"] = "code_sent",
            ["instruction"] = "Tell the caller, in one or two short sentences: you are texting a six digit code to the NEW number now, and after you stop talking the automated line will ask them to SAY the code. Do not ask for the code yourself and never repeat it. Then stop talking.",
        }.ToJsonString(), EndCall: true);
    }
}

/// <summary>Password, payment methods and deleting the account are not done by phone: the caller is texted a link to the web site (to their verified number only).</summary>
public sealed class TextAccountLinkTool(ISmsSender sms, IOptions<MarketplaceOptions> marketplace) : IVoiceTool
{
    public static readonly string[] Topics = ["password", "payment_methods", "delete_account"];
    public string Name => "text_me_account_link";
    public JsonObject Definition => ToolKit.Def(Name, "Text the caller a link to the web site for something that is not done by phone: their password, payment methods or deleting the account. Say it is done on the web site for their safety.",
        new JsonObject { ["topic"] = ToolKit.Prop("string", "One of: " + string.Join(", ", Topics) + ".") }, "topic");

    public async Task<VoiceToolResult> RunAsync(VoiceToolContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.State;
        if (!OrderAccess.IsAccount(s) && !OrderAccess.IsGuest(s)) return ToolKit.Error("need_verification", "A link is texted only to a number verified in this call: offer identify_caller first.");
        var topic = ToolKit.Str(args, "topic");
        if (topic is null || !Topics.Contains(topic)) return ToolKit.Error("bad_topic", "Ask which: " + string.Join(", ", Topics) + ".");
        if (s.CountAccountLink() > 5) return ToolKit.Error("too_many_texts", "Enough texts in this call: say so.");
        var url = (marketplace.Value.PublicBaseUrl ?? DevDefaults.PublicBaseUrl).TrimEnd('/') + "/account";
        var what = topic switch { "password" => "set a password", "payment_methods" => "manage payment methods", _ => "delete your account" };
        if (!await sms.SendAsync(s.VerifiedPhone!, $"Solvelt: to {what}, sign in on our web site: {url}", ct)) return ToolKit.Error("sms_failed", "The text could not be sent: say so.");
        return ToolKit.Json(new JsonObject { ["status"] = "sent", ["instruction"] = "Say the link is on its way by text, and that this is done on the web site for their safety." });
    }
}
