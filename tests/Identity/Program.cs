using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder=WebApplication.CreateBuilder(new WebApplicationOptions {EnvironmentName="Development"});
if(args.Contains("--database")) {await DatabaseChecks.Run();return;}
builder.WebHost.UseTestServer();
builder.Logging.ClearProviders();
builder.Services.AddProblemDetails();
builder.Services.AddIdentityModule(builder.Configuration);
var store=new TestStore();
builder.Services.AddSingleton<IIdentityStore>(store);
builder.Services.AddSingleton<ICompanyAccess>(store);
builder.Services.AddSingleton<ISupabaseAuthClient>(new TestProvider());
await using var app=builder.Build();
app.Use(async(context,next)=>
{
 if(IPAddress.TryParse(context.Request.Headers["X-Test-IP"].ToString(),out var address)) context.Connection.RemoteIpAddress=address;
 await next(context);
});
app.UseExceptionHandler(error=>error.Run(async context=>
{
 context.Response.StatusCode=409; await context.Response.WriteAsJsonAsync(new {message="Conflict"});
}));
app.UseRateLimiter();
app.UseAuthentication(); app.UseAuthorization();
app.MapIdentityModule();
await app.StartAsync();
var client=app.GetTestClient();
var passed=0;
var requestNumber=1;
async Task Check(string name,Func<Task> action) {await action();passed++;Console.WriteLine("PASS: "+name);}
void Assert(bool value,string reason) {if(!value) throw new Exception(reason);}
async Task<HttpResponseMessage> Request(HttpMethod method,string path,string? token=null,object? body=null,bool portalHeader=false,string? cookie=null)
{
 using var request=new HttpRequestMessage(method,path);
 request.Headers.Add("X-Test-IP","127.0.0."+(requestNumber++));
 if(token is not null) request.Headers.Authorization=new("Bearer",token);
 if(body is not null) request.Content=JsonContent.Create(body);
 if(portalHeader) request.Headers.Add("X-GTS-Client","portal");
 if(cookie is not null) request.Headers.Add("Cookie",cookie);
 return await client.SendAsync(request);
}
async Task Status(string name,HttpMethod method,string path,int expected,string? token=null,object? body=null)
{
 await Check(name,async()=>{using var response=await Request(method,path,token,body);Assert((int)response.StatusCode==expected,$"{name}: expected {expected}, got {(int)response.StatusCode}");});
}
await Status("Anonymous users cannot read profile",HttpMethod.Get,"/api/auth/me",401);
await Status("Invalid tokens rejected",HttpMethod.Get,"/api/auth/me",401,"forged");
await Status("Revoked sessions rejected",HttpMethod.Get,"/api/auth/me",401,"revoked");
await Status("Suspended accounts rejected",HttpMethod.Get,"/api/auth/me",401,"suspended");
await Status("Uninvited verified users lack portal access",HttpMethod.Get,"/api/auth/me",403,"unlinked");
await Status("Candidate can read own profile",HttpMethod.Get,"/api/auth/me",200,"candidate");
await Status("Client cannot access staff endpoints",HttpMethod.Get,"/api/admin/access",403,"owner");
await Status("Staff can access staff endpoints",HttpMethod.Get,"/api/admin/access",200,"staff");
await Status("Staff cannot access admin-only endpoint",HttpMethod.Get,"/api/admin/admin-access",403,"staff");
await Status("Admin can access admin-only endpoint",HttpMethod.Get,"/api/admin/admin-access",200,"admin");
await Status("Company member can read their company",HttpMethod.Get,$"/api/companies/{TestStore.CompanyA}",200,"member");
await Status("Owner cannot read another company",HttpMethod.Get,$"/api/companies/{TestStore.CompanyB}",404,"owner");
await Status("Candidate cannot read company data",HttpMethod.Get,$"/api/companies/{TestStore.CompanyA}",404,"candidate");
await Status("Revoked member loses company access",HttpMethod.Get,$"/api/companies/{TestStore.CompanyA}",404,"revoked-member");
await Status("Member cannot view membership list",HttpMethod.Get,$"/api/companies/{TestStore.CompanyA}/members",404,"member");
await Status("Owner can view membership list",HttpMethod.Get,$"/api/companies/{TestStore.CompanyA}/members",200,"owner");
await Status("Member cannot change roles",HttpMethod.Patch,$"/api/companies/{TestStore.CompanyA}/members/{Guid.NewGuid()}",404,"member",new MembershipInput("owner","active"));
await Status("Owner cannot change another company's roles",HttpMethod.Patch,$"/api/companies/{TestStore.CompanyB}/members/{Guid.NewGuid()}",404,"owner",new MembershipInput("owner","active"));
await Status("Owner cannot self-promote to staff role",HttpMethod.Patch,$"/api/companies/{TestStore.CompanyA}/members/{Guid.NewGuid()}",400,"owner",new MembershipInput("admin","active"));
await Status("Client cannot issue invitations",HttpMethod.Post,"/api/admin/invitations",403,"owner",new InviteInput("new@example.test","candidate",null,null));
await Status("Invitation cannot grant staff privileges",HttpMethod.Post,"/api/admin/invitations",400,"staff",new InviteInput("new@example.test","staff",null,null));
await Status("Client invitation needs company and membership role",HttpMethod.Post,"/api/admin/invitations",400,"staff",new InviteInput("new@example.test","client",null,null));
await Status("Staff can invite candidate",HttpMethod.Post,"/api/admin/invitations",200,"staff",new InviteInput("new@example.test","candidate",null,null));
await Status("Expired invitation rejected at registration",HttpMethod.Post,"/api/auth/register",400,body:new RegisterInput("new@example.test","Strong-pass-12345",new string('b',64)));
await Status("Invitation email mismatch rejected",HttpMethod.Post,"/api/auth/register",400,body:new RegisterInput("wrong@example.test","Strong-pass-12345",new string('a',64)));
await Status("Short registration password rejected",HttpMethod.Post,"/api/auth/register",400,body:new RegisterInput("new@example.test","short",new string('a',64)));
await Status("Valid invitation registration accepted",HttpMethod.Post,"/api/auth/register",202,body:new RegisterInput("new@example.test","Strong-pass-12345",new string('a',64)));
await Status("Anonymous invitation acceptance denied",HttpMethod.Post,"/api/auth/invitations/accept",401,body:new AcceptInvitationInput(new string('a',64),"New User"));
await Status("Weak password update denied",HttpMethod.Put,"/api/auth/password",400,"candidate",new PasswordInput("short"));
await Check("Refresh rejects requests without custom CSRF header",async()=>
{
 using var response=await Request(HttpMethod.Post,"/api/auth/refresh",cookie:"gts_refresh=refresh");
 Assert(response.StatusCode==HttpStatusCode.BadRequest,"CSRF header absent");
});
await Check("Refresh cookie rotates without exposing refresh token",async()=>
{
 using var response=await Request(HttpMethod.Post,"/api/auth/refresh",portalHeader:true,cookie:"gts_refresh=refresh");
 Assert(response.IsSuccessStatusCode,"Refresh failed");
 Assert(response.Headers.GetValues("Set-Cookie").Any(c=>c.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&c.Contains("samesite=strict",StringComparison.OrdinalIgnoreCase)),"Cookie protections absent");
 var body=await response.Content.ReadAsStringAsync();Assert(body.Contains("accessToken")&&!body.Contains("refreshToken"),"Refresh token leaked");
});
await Check("Login sets HttpOnly cookie and no-store response",async()=>
{
 using var response=await Request(HttpMethod.Post,"/api/auth/login",body:new LoginInput("candidate@example.test","password"));
 Assert(response.IsSuccessStatusCode,"Login failed");
 Assert(response.Headers.CacheControl?.NoStore==true,"Missing no-store");
 Assert(response.Headers.GetValues("Set-Cookie").Any(c=>c.Contains("httponly",StringComparison.OrdinalIgnoreCase)),"Missing cookie");
});
await Status("Unknown email recovery remains generic",HttpMethod.Post,"/api/auth/password-recovery",202,body:new EmailInput("unknown@example.test"));
await Status("Password change requires verified session",HttpMethod.Put,"/api/auth/password",401,body:new PasswordInput("Strong-pass-12345"));
await Status("Anonymous logout rejected",HttpMethod.Post,"/api/auth/logout",401);
await Status("Email change requires login",HttpMethod.Put,"/api/auth/email",401,body:new ChangeEmailInput("new@example.test"));
await Status("Admin can request email change without losing role",HttpMethod.Put,"/api/auth/email",202,"admin",new ChangeEmailInput("new@example.test"));
await Check("Supabase verification rejects unconfirmed email",async()=>
{
 var fake=new ProviderHandler(false,true);
 var provider=new SupabaseAuthClient(new HttpClient(fake),Options.Create(new SupabaseAuthOptions {Url="https://example.supabase.co",PublishableKey="public"}));
 Assert(await provider.Verify(Token(),default)is null,"Unverified email accepted");
});
await Check("Supabase rejection prevents trusting forged claims",async()=>
{
 var provider=new SupabaseAuthClient(new HttpClient(new ProviderHandler(true,false)),Options.Create(new SupabaseAuthOptions {Url="https://example.supabase.co",PublishableKey="public"}));
 Assert(await provider.Verify(Token(),default)is null,"Forged token accepted");
});
await Check("Verified Supabase user must match JWT subject and session",async()=>
{
 var provider=new SupabaseAuthClient(new HttpClient(new ProviderHandler(true,true)),Options.Create(new SupabaseAuthOptions {Url="https://example.supabase.co",PublishableKey="public"}));
 Assert(await provider.Verify(Token(),default)is not null,"Valid token rejected");
 Assert(await provider.Verify(Token(Guid.NewGuid()),default)is null,"Subject mismatch accepted");
});
Console.WriteLine($"{passed} identity checks passed.");
static string Token(Guid? sub=null)
{
 var json=JsonSerializer.Serialize(new {sub=(sub??ProviderHandler.AuthId).ToString(),session_id=Guid.NewGuid(),exp=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()});
 return "header."+Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+','-').Replace('/','_')+".signature";
}
sealed class ProviderHandler(bool verified,bool valid) : HttpMessageHandler
{
 public static readonly Guid AuthId=Guid.NewGuid();
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>
 Task.FromResult(new HttpResponseMessage(valid?HttpStatusCode.OK:HttpStatusCode.Unauthorized)
 {Content=JsonContent.Create(new {id=AuthId,email="verified@example.test",email_confirmed_at=verified?"2026-10-19T00:00:00Z":null})});
}
sealed class TestProvider : ISupabaseAuthClient
{
 public Task<VerifiedIdentity?> Verify(string token,CancellationToken ct)=>Task.FromResult<VerifiedIdentity?>(
 token=="forged"?null:new(TestStore.AuthId(token),token+"@example.test",token=="revoked"?Guid.Empty:Guid.NewGuid()));
 public Task<ProviderSession?> Login(LoginInput input,CancellationToken ct)=>Task.FromResult<ProviderSession?>(new("candidate","refresh",3600));
 public Task<ProviderSession?> Refresh(string token,CancellationToken ct)=>Task.FromResult<ProviderSession?>(new("candidate","rotated",3600));
 public Task Logout(string token,CancellationToken ct)=>Task.CompletedTask;
 public Task Register(RegisterInput input,CancellationToken ct)=>Task.CompletedTask;
 public Task Recover(string email,CancellationToken ct)=>Task.CompletedTask;
 public Task<bool> ChangePassword(string token,string password,CancellationToken ct)=>Task.FromResult(true);
 public Task<bool> ChangeEmail(string token,string email,CancellationToken ct)=>Task.FromResult(true);
}
sealed class TestStore : IIdentityStore,ICompanyAccess
{
 public static readonly Guid CompanyA=Guid.NewGuid(),CompanyB=Guid.NewGuid();
 private static readonly string[] Tokens=["owner","member","staff","admin","candidate","suspended","revoked-member","unlinked","revoked"];
 public static Guid AuthId(string token)=>new(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)).AsSpan(0,16));
 public Task<bool> SessionActive(VerifiedIdentity identity,CancellationToken ct)=>Task.FromResult(identity.SessionId!=Guid.Empty);
 public Task SyncEmail(Guid authId,string verifiedEmail,CancellationToken ct)=>Task.CompletedTask;
 public Task<AppAccount?> FindAccount(Guid authId,CancellationToken ct)
 {
  var token=Tokens.FirstOrDefault(t=>AuthId(t)==authId);
  return Task.FromResult<AppAccount?>(token is null or "unlinked"?null:new(authId,authId,token+"@example.test",token is "admin" or "staff"?"staff":token=="candidate"?"candidate":"client",token=="suspended"?"suspended":"active",token is "admin" or "staff"?token:null));
 }
 public Task<bool> CanAccess(Guid companyId,ClaimsPrincipal user,bool ownerRequired,CancellationToken ct)=>
 Task.FromResult(user.IsInRole("admin")||user.IsInRole("staff")||companyId==CompanyA&&
 (user.AuthId()==AuthId("owner")||!ownerRequired&&user.AuthId()==AuthId("member")));
 public Task<InvitationRecord?> FindInvitation(string token,CancellationToken ct)=>Task.FromResult<InvitationRecord?>(token==new string('a',64)?new(Guid.NewGuid(),"new@example.test","candidate",null,null):null);
 public Task<InvitationCreated> Invite(InviteInput input,Guid staffId,CancellationToken ct)=>Task.FromResult(new InvitationCreated(Guid.NewGuid(),new string('a',64),DateTimeOffset.UtcNow.AddDays(3),"https://example.test/invite"));
 public Task<AppAccount> Accept(AcceptInvitationInput input,Guid authId,string email,CancellationToken ct)=>throw new IdentityConflictException("Invalid invitation");
 public Task<List<CompanyRecord>> Companies(ClaimsPrincipal user,CancellationToken ct)=>Task.FromResult(new List<CompanyRecord>{new(CompanyA,"Company","US","active")});
 public Task<CompanyRecord?> Company(Guid id,CancellationToken ct)=>Task.FromResult<CompanyRecord?>(new(id,"Company","US","active"));
 public Task<CompanyRecord> CreateCompany(CompanyInput input,CancellationToken ct)=>Task.FromResult(new CompanyRecord(Guid.NewGuid(),input.Name,input.CountryCode,"active"));
 public Task<List<MemberRecord>> Members(Guid id,CancellationToken ct)=>Task.FromResult(new List<MemberRecord>());
 public Task<bool> UpdateMember(Guid companyId,Guid userId,MembershipInput input,Guid actorId,CancellationToken ct)=>Task.FromResult(true);
}
