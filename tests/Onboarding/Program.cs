using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

if(args.Contains("--database")){await DatabaseChecks.Run();return;}
var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
builder.Services.AddAuthorization(o=>{
 o.AddPolicy(AccessPolicies.Staff,p=>p.RequireAuthenticatedUser().RequireRole("staff","admin"));
 o.AddPolicy(AccessPolicies.AppUser,p=>p.RequireAuthenticatedUser().RequireClaim("app_user_id"));
});
builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,TestAuth>("test",_=>{});
var fake=new FakeStore();builder.Services.AddSingleton<IEnquiryOnboarding>(fake);builder.Services.AddSingleton<IPortalConversationList>(fake);
await using var app=builder.Build();app.UseExceptionHandler(e=>e.Run(c=>{c.Response.StatusCode=409;return Task.CompletedTask;}));
app.UseAuthentication();app.UseAuthorization();app.MapOnboardingModule();app.MapPortalConversationList();await app.StartAsync();var client=app.GetTestClient();int passed=0;
async Task Check(string name,string method,string path,int status,object? body=null,string? role="staff")
{
 using var request=new HttpRequestMessage(new(method),path);if(role is not null)request.Headers.Authorization=new("Bearer",role);
 if(body is not null)request.Content=JsonContent.Create(body);using var result=await client.SendAsync(request);
 if((int)result.StatusCode!=status)throw new Exception($"{name}: expected {status}, got {(int)result.StatusCode}");passed++;Console.WriteLine("PASS: "+name);
}
var path=$"/api/admin/enquiries/{FakeStore.EnquiryId}/onboarding";var input=new ApproveEnquiry(Guid.NewGuid(),"owner",null);
await Check("Anonymous approval denied","POST",path,401,input,null);
await Check("Client approval denied","POST",path,403,input,"client");
await Check("Candidate approval denied","POST",path,403,input,"candidate");
await Check("Staff approval accepted","POST",path,201,input);
await Check("Candidate invitation accepted without company","POST",path,201,new ApproveEnquiry(null,null,null));
await Check("Invalid company UUID rejected","POST",path,400,input with {CompanyId=Guid.Empty});
await Check("Invalid replacement UUID rejected","POST",path,400,input with {ReplaceInvitationId=Guid.Empty});
await Check("Invalid role rejected","POST",path,400,input with {MembershipRole="admin"});
await Check("Staff can inspect invitation state","GET",path,200);
await Check("Client cannot inspect admin invitation state","GET",path,403,role:"client");
await Check("Unknown enquiry approval returns 404","POST",$"/api/admin/enquiries/{Guid.NewGuid()}/onboarding",404,input);
fake.Conflict=true;await Check("Repeated approval conflict returned","POST",path,409,input);fake.Conflict=false;
await Check("Anonymous portal list denied","GET","/api/portal/conversations",401,role:null);
await Check("Unonboarded identity portal list denied","GET","/api/portal/conversations",403,role:"unlinked");
await Check("Client can list conversations","GET","/api/portal/conversations",200,role:"client");
await Check("Candidate can list conversations","GET","/api/portal/conversations",200,role:"candidate");
await Check("Invalid page rejected","GET","/api/portal/conversations?page=0",400,role:"client");
await Check("Oversized page rejected","GET","/api/portal/conversations?pageSize=101",400,role:"client");
Console.WriteLine($"{passed} onboarding HTTP checks passed.");

sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
{
 protected override Task<AuthenticateResult> HandleAuthenticateAsync()
 {
  var header=Request.Headers.Authorization.ToString();if(!header.StartsWith("Bearer "))return Task.FromResult(AuthenticateResult.NoResult());
  var role=header[7..];var claims=new List<Claim>{new(ClaimTypes.Role,role)};
  if(role!="unlinked")claims.Add(new("app_user_id",Guid.NewGuid().ToString()));
  return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,"test")),"test")));
 }
}
sealed class FakeStore:IEnquiryOnboarding,IPortalConversationList
{
 public static readonly Guid EnquiryId=Guid.NewGuid();public bool Conflict;
 public Task<OnboardingInvitation?> Approve(Guid id,ApproveEnquiry input,Guid actor,CancellationToken ct)
 {if(Conflict)throw new OnboardingConflictException();return Task.FromResult<OnboardingInvitation?>(id==EnquiryId?new(Guid.NewGuid(),"test-only-token",DateTimeOffset.UtcNow,"http://localhost:5173/accept-invitation"):null);}
 public Task<EnquiryOnboarding?> Get(Guid id,CancellationToken ct)=>Task.FromResult<EnquiryOnboarding?>(new(id,Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow,null));
 public Task<PortalConversationPage> List(ClaimsPrincipal user,int page,int size,CancellationToken ct)=>Task.FromResult(new PortalConversationPage([],0,page,size));
}
