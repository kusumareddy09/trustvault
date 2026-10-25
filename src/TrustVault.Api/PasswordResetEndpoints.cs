
using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;

namespace TrustVault.Api;

public static class PasswordResetEndpoints
{
    private sealed class OtpEntry
    {
        public required string Hash { get; init; }
        public required DateTime ExpiresAtUtc { get; init; }
        public DateTime LastSentAtUtc { get; set; }
        public int Attempts { get; set; }
    }

    private static readonly ConcurrentDictionary<string, OtpEntry>
        OtpStore = new();

    public static void MapPasswordResetEndpoints(
        this WebApplication app)
    {
        app.MapPost("/api/auth/forgot-password",
            async (ForgotPasswordRequest request,
                   AppDbContext db,
                   IConfiguration config,
                   HttpContext context) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant() ?? "";

            // Use the same response for existing and unknown accounts.
            var genericResponse = Results.Ok(new
            {
                message = "If the account exists and email delivery is configured, an OTP will be sent."
            });

            if (email.Length > 254 || !IsValidEmail(email))
                return genericResponse;

            var user = await db.Users.SingleOrDefaultAsync(
                u => u.Email == email);

            if (user is null)
                return genericResponse;

            var now = DateTime.UtcNow;

            if (OtpStore.TryGetValue(email, out var previous) &&
                now - previous.LastSentAtUtc < TimeSpan.FromSeconds(60))
            {
                return genericResponse;
            }

            var otp = RandomNumberGenerator.GetInt32(100000, 1000000)
                .ToString();

            var entry = new OtpEntry
            {
                Hash = HashOtp(otp),
                ExpiresAtUtc = now.AddMinutes(10),
                LastSentAtUtc = now,
                Attempts = 0
            };

            OtpStore[email] = entry;

            try
            {
                var host = config["Email:SmtpHost"];
                var username = config["Email:Username"];
                var password = config["Email:Password"];
                var from = config["Email:From"];
                var portText = config["Email:SmtpPort"];

                if (string.IsNullOrWhiteSpace(host) ||
                    string.IsNullOrWhiteSpace(username) ||
                    string.IsNullOrWhiteSpace(password) ||
                    string.IsNullOrWhiteSpace(from) ||
                    !int.TryParse(portText, out var port))
                {
                    OtpStore.TryRemove(email, out _);
                    return Results.Problem(
                        "Email delivery is not configured.",
                        statusCode: 500);
                }

                using var message = new MailMessage(from, email)
                {
                    Subject = "TrustVault password reset OTP",
                    Body =
                        $"Your TrustVault password reset OTP is {otp}.\n\n" +
                        "It expires in 10 minutes. If you did not request " +
                        "this, you can ignore this email.",
                    IsBodyHtml = false
                };

                using var smtp = new SmtpClient(host, port)
                {
                    EnableSsl = true,
                    Credentials = new NetworkCredential(username, password)
                };

                await smtp.SendMailAsync(message);
            }
            catch
            {
                OtpStore.TryRemove(email, out _);

                return Results.Problem(
                    "Unable to send the OTP email. Check the SMTP configuration.",
                    statusCode: 500);
            }

            return genericResponse;
        }).AllowAnonymous();

        app.MapPost("/api/auth/reset-password",
            async (ResetPasswordRequest request,
                   AppDbContext db,
                   IPasswordHasher<UserAccount> hasher,
                   HttpContext context) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant() ?? "";
            var otp = request.Otp?.Trim() ?? "";
            var newPassword = request.NewPassword ?? "";

            if (!IsValidEmail(email) ||
                otp.Length != 6 ||
                otp.Any(c => c < '0' || c > '9') ||
                newPassword.Length < 8 ||
                newPassword.Length > 128)
            {
                return Results.BadRequest(new
                {
                    message = "Enter a valid email, six-digit OTP, and password of 8–128 characters."
                });
            }

            if (!OtpStore.TryGetValue(email, out var entry))
            {
                return Results.BadRequest(new
                {
                    message = "OTP is invalid or expired. Request a new OTP."
                });
            }

            if (DateTime.UtcNow > entry.ExpiresAtUtc)
            {
                OtpStore.TryRemove(email, out _);

                return Results.BadRequest(new
                {
                    message = "OTP expired. Request a new OTP."
                });
            }

            if (entry.Attempts >= 5)
            {
                OtpStore.TryRemove(email, out _);

                return Results.BadRequest(new
                {
                    message = "Too many attempts. Request a new OTP."
                });
            }

            entry.Attempts++;

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(entry.Hash),
                    Convert.FromHexString(HashOtp(otp))))
            {
                return Results.BadRequest(new
                {
                    message = "Invalid OTP."
                });
            }

            var user = await db.Users.SingleOrDefaultAsync(
                u => u.Email == email);

            if (user is null)
            {
                OtpStore.TryRemove(email, out _);

                return Results.BadRequest(new
                {
                    message = "Unable to reset the password. Request a new OTP."
                });
            }

            user.PasswordHash = hasher.HashPassword(user, newPassword);

            db.AuditEvents.Add(new AuditEvent
            {
                ActorId = user.Id,
                Action = "PasswordReset",
                Outcome = "Allowed",
                RequestId = context.TraceIdentifier,
                Details = "Password reset completed using email OTP"
            });

            await db.SaveChangesAsync();
            OtpStore.TryRemove(email, out _);

            return Results.Ok(new
            {
                message = "Password reset successfully. Please sign in with your new password."
            });
        }).AllowAnonymous();
    }

    private static string HashOtp(string otp) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(otp)));

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        try
        {
            return new MailAddress(email).Address.Equals(
                email, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public sealed record ForgotPasswordRequest(string? Email);

    public sealed record ResetPasswordRequest(
        string? Email,
        string? Otp,
        string? NewPassword);
}
