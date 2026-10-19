using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

if(args.Contains("--database")) {await HostedChecks.Run();return;}
var builder=WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();
builder.Logging.ClearProviders();
builder.Services.AddEnquiriesModule();
builder.Services.AddAuthorization(options=>options.AddPolicy(AccessPolicies.Staff,p=>p.RequireAuthenticatedUser().RequireRole("admin","staff")));
builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,TestAuthentication>("test",_=>{});
var store=new FakeStore();var directory=new FakeDirectory();
builder.Services.AddSingleton<IEnquiryStore>(store);builder.Services.AddSingleton<IStaffDirectory>(directory);
await using var app=builder.Build();
app.UseExceptionHandler(error=>error.Run(async context=>{context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new {message="Conflict"});}));
app.Use(async(context,next)=>
{
 if(IPAddress.TryParse(context.Request.Headers["X-Test-IP"].ToString(),out var ip)) context.Connection.RemoteIpAddress=ip;
 await next(context);
});
app.UseEnquiryRequestLimits();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();app.MapEnquiriesModule();
await app.StartAsync();
var client=app.GetTestClient();int passed=0,sequence=1;
var input=new SubmitEnquiry("business","Example Person","person@example.test",null,"Example Company","us","Staffing","Looking for an experienced assistant.",true,Guid.NewGuid());
async Task Check(string name,Func<Task> action){await action();passed++;Console.WriteLine("PASS: "+name);}
void Assert(bool value,string name){if(!value)throw new Exception(name);}
async Task<HttpResponseMessage> Request(HttpMethod method,string path,object? body=null,string? role=null,string? ip=null)
{
 using var request=new HttpRequestMessage(method,path);
 request.Headers.Add("X-Test-IP",ip??"127.0.0."+sequence++);
 if(role is not null)request.Headers.Authorization=new("Bearer",role);
 if(body is not null)request.Content=JsonContent.Create(body);
 return await client.SendAsync(request);
}
async Task Status(string name,HttpMethod method,string path,int status,object? body=null,string? role=null)
{
 await Check(name,async()=>{using var response=await Request(method,path,body,role);Assert((int)response.StatusCode==status,$"{name}: expected {status}, got {(int)response.StatusCode}");});
}
await Status("Business can enquire without an account",HttpMethod.Post,"/api/enquiries",201,input);
await Status("Candidate can enquire without an account",HttpMethod.Post,"/api/enquiries",201,input with {EnquiryType="candidate",CompanyName=null,SubmissionKey=Guid.NewGuid()});
await Status("Honeypot submission is quietly accepted",HttpMethod.Post,"/api/enquiries",202,input with {Website="https://spam.test"});
await Check("Honeypot does not write to the store",async()=>
{
 var before=store.Submissions;using var response=await Request(HttpMethod.Post,"/api/enquiries",input with {Website="spam"});Assert(store.Submissions==before,"Honeypot stored");});
await Status("Missing consent is rejected",HttpMethod.Post,"/api/enquiries",400,input with {Consent=false});
await Status("Invalid email is rejected",HttpMethod.Post,"/api/enquiries",400,input with {Email="invalid"});
await Status("Missing name is rejected",HttpMethod.Post,"/api/enquiries",400,input with {Name=null});
await Status("Missing service is rejected",HttpMethod.Post,"/api/enquiries",400,input with {Service=null});
await Status("Too short message is rejected",HttpMethod.Post,"/api/enquiries",400,input with {Message="short"});
await Status("Business requires company name",HttpMethod.Post,"/api/enquiries",400,input with {CompanyName=null});
await Status("Candidate cannot submit business company",HttpMethod.Post,"/api/enquiries",400,input with {EnquiryType="candidate"});
await Status("Unknown enquiry type rejected",HttpMethod.Post,"/api/enquiries",400,input with {EnquiryType="staff"});
await Status("Submission retry key required",HttpMethod.Post,"/api/enquiries",400,input with {SubmissionKey=null});
await Status("Empty retry UUID rejected",HttpMethod.Post,"/api/enquiries",400,input with {SubmissionKey=Guid.Empty});
await Status("Invalid country rejected",HttpMethod.Post,"/api/enquiries",400,input with {CountryCode="USA"});
await Status("Null text characters rejected before database",HttpMethod.Post,"/api/enquiries",400,input with {Message="Message with a null\0character"});
await Check("Fields are trimmed and email/country normalized",async()=>
{
 using var response=await Request(HttpMethod.Post,"/api/enquiries",input with {Name=" Example Person ",Email=" Person@Example.test ",CountryCode=" gb ",CompanyName=" Example Company ",SubmissionKey=Guid.NewGuid()});
 Assert(response.StatusCode==HttpStatusCode.Created,"Normalization rejected");Assert(store.Last?.Email=="person@example.test"&&store.Last.CountryCode=="GB"&&store.Last.Name=="Example Person","Incorrect normalization");
});
await Check("Public receipt does not expose internal or contact fields",async()=>
{
 using var response=await Request(HttpMethod.Post,"/api/enquiries",input with {SubmissionKey=Guid.NewGuid()});
 var body=await response.Content.ReadAsStringAsync();Assert(body.Contains("reference")&&!body.Contains("assignedStaffId")&&!body.Contains("email")&&!body.Contains("updatedAt"),"Leaking receipt");
 Assert(response.Headers.CacheControl?.NoStore==true,"Receipt is cacheable");
});
await Check("Oversized JSON rejected with 413",async()=>
{
 using var request=new HttpRequestMessage(HttpMethod.Post,"/api/enquiries");
 request.Content=new StringContent(new string('x',65537),Encoding.UTF8,"application/json");
 using var response=await client.SendAsync(request);Assert((int)response.StatusCode==413,"Oversized JSON accepted");
});
await Check("Sixth submission from same IP is throttled",async()=>
{
 for(int n=0;n<5;n++){using var response=await Request(HttpMethod.Post,"/api/enquiries",input with {Website="spam"},ip:"192.0.2.1");Assert((int)response.StatusCode==202,"Early throttle");}
 using var last=await Request(HttpMethod.Post,"/api/enquiries",input,ip:"192.0.2.1");Assert((int)last.StatusCode==429,"No throttle");
});
await Status("Anonymous cannot list enquiries",HttpMethod.Get,"/api/admin/enquiries",401);
await Status("Client cannot list enquiries",HttpMethod.Get,"/api/admin/enquiries",403,role:"client");
await Status("Candidate cannot read staff enquiry detail",HttpMethod.Get,$"/api/admin/enquiries/{Guid.NewGuid()}",403,role:"candidate");
await Status("Staff can list enquiries",HttpMethod.Get,"/api/admin/enquiries?status=new&enquiryType=business",200,role:"staff");
await Status("Admin can list enquiries",HttpMethod.Get,"/api/admin/enquiries",200,role:"admin");
await Status("Invalid inbox status rejected",HttpMethod.Get,"/api/admin/enquiries?status=unknown",400,role:"staff");
await Status("Page zero rejected",HttpMethod.Get,"/api/admin/enquiries?page=0",400,role:"staff");
await Status("Oversized page rejected",HttpMethod.Get,"/api/admin/enquiries?pageSize=101",400,role:"staff");
await Status("Incompatible assignment filters rejected",HttpMethod.Get,$"/api/admin/enquiries?unassigned=true&assignedStaffId={Guid.NewGuid()}",400,role:"staff");
await Status("Staff can retrieve assignees",HttpMethod.Get,"/api/admin/enquiries/assignees",200,role:"staff");
await Status("Client cannot retrieve assignees",HttpMethod.Get,"/api/admin/enquiries/assignees",403,role:"client");
await Status("Missing enquiry returns 404",HttpMethod.Get,$"/api/admin/enquiries/{Guid.NewGuid()}",404,role:"staff");
var update=new UpdateEnquiry(DateTimeOffset.UtcNow,"in_progress",FakeDirectory.ActiveId);
await Status("Anonymous cannot update enquiry",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",401,update);
await Status("Client cannot update enquiry",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",403,update,"client");
await Status("Concurrent-update timestamp is required",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",400,update with {ExpectedUpdatedAt=null},"staff");
await Status("Inactive or non-staff assignee rejected",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",400,update with {AssignedStaffId=Guid.NewGuid()},"staff");
await Status("Invalid status update rejected",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",400,update with {Status="deleted"},"staff");
await Status("Assign-and-clear conflict rejected",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",400,update with {ClearAssignment=true},"staff");
store.Outcome=UpdateOutcome.Conflict;
await Status("Stale update returns 409",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",409,update,"staff");
store.Outcome=UpdateOutcome.NotFound;
await Status("Update of unknown enquiry returns 404",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",404,update,"staff");
store.Outcome=UpdateOutcome.Updated;
await Status("Staff can change status and assignment",HttpMethod.Patch,$"/api/admin/enquiries/{Guid.NewGuid()}",200,update,"staff");
Console.WriteLine($"{passed} enquiry HTTP checks passed.");

sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
 protected override Task<AuthenticateResult> HandleAuthenticateAsync()
 {
  var token=Request.Headers.Authorization.ToString();
  if(!token.StartsWith("Bearer "))return Task.FromResult(AuthenticateResult.NoResult());
  var role=token[7..];var identity=new ClaimsIdentity([new(ClaimTypes.Role,role)],"test");
  return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity),"test")));
 }
}
sealed class FakeDirectory:IStaffDirectory
{
 public static readonly Guid ActiveId=Guid.NewGuid();
 public Task<bool> IsActiveStaff(Guid id,CancellationToken ct)=>Task.FromResult(id==ActiveId);
 public Task<List<StaffMember>> ActiveStaff(CancellationToken ct)=>Task.FromResult(new List<StaffMember>{new(ActiveId,"Staff","staff")});
}
sealed class FakeStore:IEnquiryStore
{
 public int Submissions;public NormalizedEnquiry? Last;public UpdateOutcome Outcome=UpdateOutcome.Updated;
 public Task<SubmissionResult> Submit(NormalizedEnquiry input,CancellationToken ct){Submissions++;Last=input;return Task.FromResult(new SubmissionResult("GTS-test",true));}
 public Task<EnquiryPage> List(EnquiryQuery query,CancellationToken ct)=>Task.FromResult(new EnquiryPage([],0,query.Page,query.PageSize));
 public Task<EnquiryRecord?> Get(Guid id,CancellationToken ct)=>Task.FromResult<EnquiryRecord?>(null);
 public Task<UpdateResult> Update(Guid id,UpdateEnquiry input,CancellationToken ct)=>Task.FromResult(new UpdateResult(Outcome));
}
