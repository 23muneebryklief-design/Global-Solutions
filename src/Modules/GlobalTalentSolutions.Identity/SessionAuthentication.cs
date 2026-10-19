using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Identity;

public sealed class SessionAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ISupabaseAuthClient provider, IIdentityStore store)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    public const string SchemeName = "Supabase";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase) || header.Length > 16384)
            return AuthenticateResult.Fail("Invalid bearer token.");
        var identity = await provider.Verify(header[7..], Context.RequestAborted);
        if (identity is null || !await store.SessionActive(identity,Context.RequestAborted))
            return AuthenticateResult.Fail("Invalid or revoked session.");
        var account = await store.FindAccount(identity.Id,Context.RequestAborted);
        if (account is not null && account.Status != "active") return AuthenticateResult.Fail("Account is unavailable.");
        if (account is not null && account.Email != identity.Email)
            await store.SyncEmail(identity.Id,identity.Email,Context.RequestAborted);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier,identity.Id.ToString()),
            new(ClaimTypes.Email,identity.Email),
            new("session_id",identity.SessionId.ToString())
        };
        if(account is not null)
        {
            claims.Add(new("app_user_id",account.Id.ToString()));
            claims.Add(new("account_type",account.AccountType));
            if(account.AccountType == "staff" && account.StaffRole is not null)
                claims.Add(new(ClaimTypes.Role,account.StaffRole));
        }
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims,SchemeName)),SchemeName));
    }
}
