using System.Net.Mail;
using System.Text.Json.Serialization;

namespace GlobalTalentSolutions.Identity;

public sealed class SupabaseAuthOptions
{
    public string Url { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string FrontendUrl { get; set; } = "http://localhost:5173";
}
public sealed record LoginInput(string Email, string Password);
public sealed record RegisterInput(string Email, string Password, string InvitationToken);
public sealed record EmailInput(string Email);
public sealed record PasswordInput(string Password);
public sealed record ChangeEmailInput(string Email);
public sealed record AcceptInvitationInput(string Token, string DisplayName);
public sealed record InviteInput(string Email, string AccountType, Guid? CompanyId, string? MembershipRole);
public sealed record CompanyInput(string Name, string CountryCode);
public sealed record MembershipInput(string Role, string Status);
public sealed record VerifiedIdentity(Guid Id, string Email, Guid SessionId);
public sealed record AppAccount(Guid Id, Guid AuthUserId, string Email, string AccountType, string Status, string? StaffRole);
public sealed record CompanyRecord(Guid Id, string Name, string CountryCode, string Status);
public sealed record MemberRecord(Guid UserId, string DisplayName, string Email, string Role, string Status);
public sealed record InvitationRecord(Guid Id, string Email, string AccountType, Guid? CompanyId, string? MembershipRole);
public sealed record InvitationCreated(Guid Id, string Token, DateTimeOffset ExpiresAt, string AcceptanceUrl);
public sealed record ProviderSession(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
public sealed record AccessTokenResponse(string AccessToken, int ExpiresIn, string TokenType = "Bearer");
public static class IdentityValidation
{
    public static bool Email(string? value) => value is { Length: <= 320 } &&
        MailAddress.TryCreate(value, out var parsed) && parsed.Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
    public static bool Password(string? value) => value is { Length: >= 12 and <= 128 };
    public static bool Token(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
public sealed class IdentityConflictException(string message) : Exception(message);
public sealed class IdentityForbiddenException : Exception;
public sealed class IdentityProviderException : Exception;
