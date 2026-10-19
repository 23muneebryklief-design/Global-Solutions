using System.Security.Claims;
using System.Threading.RateLimiting;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace GlobalTalentSolutions.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SupabaseAuthOptions>(configuration.GetSection("Supabase"));
        services.AddHttpClient<ISupabaseAuthClient,SupabaseAuthClient>(client => client.Timeout=TimeSpan.FromSeconds(15))
            .RedactLoggedHeaders(["Authorization","apikey"])
            .RemoveAllLoggers();
        services.AddScoped<IdentityStore>();
        services.AddScoped<IIdentityStore>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddScoped<ICompanyAccess>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddScoped<IStaffDirectory>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddScoped<IOnboardingInvitations>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddScoped<ICompanyMemberships>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddScoped<IInvitationEmailValidity>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddScoped<IEmailContact>(provider=>provider.GetRequiredService<IdentityStore>());
        services.AddAuthentication(SessionAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions,SessionAuthentication>(SessionAuthentication.SchemeName,_=>{});
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AccessPolicies.AppUser,p=>p.RequireAuthenticatedUser().RequireClaim("app_user_id"));
            options.AddPolicy(AccessPolicies.Staff,p=>p.RequireAuthenticatedUser().RequireRole("admin","staff"));
            options.AddPolicy(AccessPolicies.Admin,p=>p.RequireAuthenticatedUser().RequireRole("admin"));
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode=StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",
                _=>new FixedWindowRateLimiterOptions { PermitLimit=15,Window=TimeSpan.FromMinutes(1),QueueLimit=0 }));
            options.AddPolicy("recovery",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",
                _=>new FixedWindowRateLimiterOptions { PermitLimit=3,Window=TimeSpan.FromMinutes(30),QueueLimit=0 }));
        });
        return services;
    }
    public static void MapIdentityModule(this IEndpointRouteBuilder app)
    {
        var auth=app.MapGroup("/api/auth").WithTags("Authentication").RequireRateLimiting("auth");
        auth.MapPost("/login",async(LoginInput input,ISupabaseAuthClient provider,IIdentityStore store,HttpContext context,IHostEnvironment env,CancellationToken ct)=>
        {
            if(!IdentityValidation.Email(input.Email) || input.Password is not {Length:>=1 and <=128}) return Results.BadRequest(new {message="Enter a valid email and password."});
            var session=await provider.Login(input,ct);
            return await SessionResponse(session,provider,store,context,env,ct);
        });
        auth.MapPost("/refresh",async(HttpContext context,ISupabaseAuthClient provider,IIdentityStore store,IHostEnvironment env,CancellationToken ct)=>
        {
            // Non-simple custom header plus SameSite=Strict prevents cross-site refresh.
            // Do not enable credentialed CORS for untrusted origins.
            if(context.Request.Headers["X-GTS-Client"]!="portal") return Results.BadRequest(new {message="X-GTS-Client: portal is required."});
            if(!context.Request.Cookies.TryGetValue("gts_refresh",out var refresh)) return Results.Unauthorized();
            var session=await provider.Refresh(refresh,ct);
            return await SessionResponse(session,provider,store,context,env,ct);
        });
        auth.MapPost("/logout",async(HttpContext context,ISupabaseAuthClient provider,IHostEnvironment env,CancellationToken ct)=>
        {
            await provider.Logout(Bearer(context),ct);
            context.Response.Cookies.Delete("gts_refresh",Cookie(env));
            return Results.NoContent();
        }).RequireAuthorization();
        auth.MapGet("/me",async(ClaimsPrincipal user,IIdentityStore store,CancellationToken ct)=>
            Results.Ok(await store.FindAccount(user.AuthId(),ct))).RequireAuthorization(AccessPolicies.AppUser);
        auth.MapPost("/register",async(RegisterInput input,IIdentityStore store,ISupabaseAuthClient provider,CancellationToken ct)=>
        {
            if(!IdentityValidation.Email(input.Email)||!IdentityValidation.Password(input.Password)||!IdentityValidation.Token(input.InvitationToken))
                return Results.BadRequest(new {message="A valid invitation, email and a 12–128 character password are required."});
            var invitation=await store.FindInvitation(input.InvitationToken,ct);
            if(invitation is null || !invitation.Email.Equals(input.Email.Trim(),StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new {message="Invitation is unavailable."});
            await provider.Register(input,ct);
            return Results.Accepted(value:new {message="Check your email to confirm your account, then sign in and accept your invitation."});
        });
        auth.MapPost("/password-recovery",async(EmailInput input,ISupabaseAuthClient provider,CancellationToken ct)=>
        {
            if(!IdentityValidation.Email(input.Email)) return Results.BadRequest(new {message="Enter a valid email."});
            await provider.Recover(input.Email,ct);
            return Results.Accepted(value:new {message="If the account exists, password recovery instructions will be sent."});
        }).RequireRateLimiting("recovery");
        auth.MapPut("/password",async(PasswordInput input,HttpContext context,ISupabaseAuthClient provider,CancellationToken ct)=>
        {
            if(!IdentityValidation.Password(input.Password)) return Results.BadRequest(new {message="Password must be 12–128 characters."});
            return await provider.ChangePassword(Bearer(context),input.Password,ct)?Results.NoContent():Results.BadRequest(new {message="Password could not be updated."});
        }).RequireAuthorization();
        auth.MapPost("/invitations/accept",async(AcceptInvitationInput input,ClaimsPrincipal user,IIdentityStore store,CancellationToken ct)=>
        {
            if(!IdentityValidation.Token(input.Token)||string.IsNullOrWhiteSpace(input.DisplayName)||input.DisplayName.Trim().Length>150)
                return Results.BadRequest(new {message="A valid token and display name are required."});
            return Results.Ok(await store.Accept(input,user.AuthId(),user.FindFirstValue(ClaimTypes.Email)!,ct));
        }).RequireAuthorization();
        auth.MapPut("/email",async(ChangeEmailInput input,HttpContext context,ISupabaseAuthClient provider,CancellationToken ct)=>
        {
            if(!IdentityValidation.Email(input.Email)) return Results.BadRequest(new {message="Enter a valid email."});
            return await provider.ChangeEmail(Bearer(context),input.Email,ct)
                ?Results.Accepted(value:new {message="Follow Supabase's email confirmation instructions. Your account permissions will remain unchanged."})
                :Results.BadRequest(new {message="Email change could not be requested."});
        }).RequireAuthorization(AccessPolicies.AppUser);
        var admin=app.MapGroup("/api/admin").WithTags("Administration").RequireAuthorization(AccessPolicies.Staff);
        admin.MapGet("/access",()=>Results.Ok(new {message="Staff access confirmed."}));
        admin.MapGet("/admin-access",()=>Results.Ok(new {message="Administrator access confirmed."})).RequireAuthorization(AccessPolicies.Admin);
        admin.MapPost("/invitations",async(InviteInput input,ClaimsPrincipal user,IIdentityStore store,CancellationToken ct)=>
        {
            if(!IdentityValidation.Email(input.Email) ||
                !(input.AccountType=="client" && input.CompanyId.HasValue && input.MembershipRole is "owner" or "member" ||
                  input.AccountType=="candidate" && input.CompanyId is null && input.MembershipRole is null))
                return Results.BadRequest(new {message="Choose a client with company/role, or a candidate without a company."});
            return Results.Ok(await store.Invite(input,user.AppId(),ct));
        });
        admin.MapPost("/companies",async(CompanyInput input,IIdentityStore store,CancellationToken ct)=>
        {
            if(string.IsNullOrWhiteSpace(input.Name)||input.Name.Trim().Length>200||
                input.CountryCode is not {Length:2} || !input.CountryCode.All(char.IsAsciiLetter))
                return Results.BadRequest(new {message="Company name and two-letter country code are required."});
            return Results.Ok(await store.CreateCompany(input,ct));
        });
        var companies=app.MapGroup("/api/companies").WithTags("Companies").RequireAuthorization(AccessPolicies.AppUser);
        companies.MapGet("/",async(ClaimsPrincipal user,IIdentityStore store,CancellationToken ct)=>Results.Ok(await store.Companies(user,ct)));
        companies.MapGet("/{companyId:guid}",async(Guid companyId,ClaimsPrincipal user,ICompanyAccess access,IIdentityStore store,CancellationToken ct)=>
        {
            if(!await access.CanAccess(companyId,user,false,ct)) return Results.NotFound();
            return Results.Ok(await store.Company(companyId,ct));
        });
        companies.MapGet("/{companyId:guid}/members",async(Guid companyId,ClaimsPrincipal user,ICompanyAccess access,IIdentityStore store,CancellationToken ct)=>
            await access.CanAccess(companyId,user,true,ct)?Results.Ok(await store.Members(companyId,ct)):Results.NotFound());
        companies.MapPatch("/{companyId:guid}/members/{userId:guid}",async(Guid companyId,Guid userId,MembershipInput input,ClaimsPrincipal user,ICompanyAccess access,IIdentityStore store,CancellationToken ct)=>
        {
            if(!await access.CanAccess(companyId,user,true,ct)) return Results.NotFound();
            if(input.Role is not ("owner" or "member") || input.Status is not ("active" or "revoked")) return Results.BadRequest(new {message="Invalid membership role or status."});
            return await store.UpdateMember(companyId,userId,input,user.AppId(),ct)?Results.NoContent():Results.NotFound();
        });
    }
    private static string Bearer(HttpContext context)=>context.Request.Headers.Authorization.ToString()[7..];
    private static CookieOptions Cookie(IHostEnvironment env)=>new()
    {HttpOnly=true,Secure=!env.IsDevelopment(),SameSite=SameSiteMode.Strict,Path="/api/auth",MaxAge=TimeSpan.FromDays(7),IsEssential=true};
    private static async Task<IResult> SessionResponse(ProviderSession? session,ISupabaseAuthClient provider,IIdentityStore store,HttpContext context,IHostEnvironment env,CancellationToken ct)
    {
        context.Response.Headers.CacheControl="no-store";
        if(session is null) {context.Response.Cookies.Delete("gts_refresh",Cookie(env)); return Results.Unauthorized();}
        var identity=await provider.Verify(session.AccessToken,ct);
        if(identity is null || !await store.SessionActive(identity,ct)) return Results.Unauthorized();
        var account=await store.FindAccount(identity.Id,ct);
        if(account is not null && account.Status!="active")
        {await provider.Logout(session.AccessToken,ct); return Results.Unauthorized();}
        context.Response.Cookies.Append("gts_refresh",session.RefreshToken,Cookie(env));
        return Results.Ok(new AccessTokenResponse(session.AccessToken,session.ExpiresIn));
    }
}
