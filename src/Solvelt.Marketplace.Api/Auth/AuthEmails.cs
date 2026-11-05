using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Auth;

/// <summary>Plain-text account emails. Links open the web pages, which show a confirm button (the POST consumes the token, so mail scanners cannot burn it).</summary>
public static class AuthEmails
{
    public static EmailMessage VerifyEmail(string to, string name, string baseUrl, string token, int hours) => new(to, "Confirm your email address",
        $"Hello {name},\n\nConfirm your email address to finish creating your Solvelt account:\n\n{baseUrl}/verify-email?token={token}\n\nTo confirm, open the link and enter the password you chose when you signed up. If you do not remember it, use \"Forgot password\" on the sign-in page instead: that also confirms your address.\n\nThe link works once and expires in {hours} hours. If you did not sign up, ignore this message.\n");

    public static EmailMessage PasswordReset(string to, string baseUrl, string token, int minutes) => new(to, "Reset your password",
        $"We received a request to reset your Solvelt password:\n\n{baseUrl}/reset-password?token={token}\n\nThe link works once and expires in {minutes} minutes. If you did not ask for this, ignore this message: your password has not changed.\n");

    public static EmailMessage PasswordChanged(string to, string baseUrl) => new(to, "Your password was changed",
        $"The password of your Solvelt account was just changed, and every other device was signed out.\n\nIf this was you, there is nothing to do. If it was not, reset your password now: {baseUrl}/forgot-password\n");

    public static EmailMessage PasskeyAdded(string to, string baseUrl) => new(to, "A passkey was added to your account",
        $"A passkey was just added to your Solvelt account. It can be used to sign in.\n\nIf this was you, there is nothing to do. If it was not, sign in, remove the passkey in your account settings and change your password: {baseUrl}/forgot-password");

    public static EmailMessage PasskeyRemoved(string to, string baseUrl) => new(to, "A passkey was removed from your account",
        $"A passkey was just removed from your Solvelt account.\n\nIf this was you, there is nothing to do. If it was not, change your password now: {baseUrl}/forgot-password");

    public static EmailMessage AlreadyRegistered(string to, string baseUrl) => new(to, "You already have a Solvelt account",
        $"Someone (possibly you) tried to sign up with this email address, but it already has an account.\n\nSign in: {baseUrl}/sign-in\nForgot your password: {baseUrl}/forgot-password\n\nIf this was not you, you can ignore this message.\n");
}
