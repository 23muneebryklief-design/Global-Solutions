using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

if(args.Contains("--database")){await HostedChecks.Run();return;}
var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
builder.Services.AddConversationsModule();
builder.Services.AddAuthorization(o=>{
 o.AddPolicy(AccessPolicies.Staff,p=>p.RequireAuthenticatedUser().RequireRole("admin","staff"));
 o.AddPolicy(AccessPolicies.AppUser,p=>p.RequireAuthenticatedUser());
});
builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,TestAuth>("test",_=>{});
var fake=new FakeStore();builder.Services.AddSingleton<IConversationStore>(fake);builder.Services.AddSingleton<IEnquiryConversationAccess>(fake);
await using var app=builder.Build();app.UseConversationRequestLimits();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();app.MapConversationsModule();await app.StartAsync();
var client=app.GetTestClient();var thread=FakeStore.ThreadId;int passed=0;
async Task Check(string name,string method,string path,int expected,object? body=null,string? role="client")
{
 using var request=new HttpRequestMessage(new(method),path);
 if(role is not null)request.Headers.Authorization=new("Bearer",role);
 if(body is not null)request.Content=new StringContent(System.Text.Json.JsonSerializer.Serialize(body),System.Text.Encoding.UTF8,"application/json");
 using var response=await client.SendAsync(request);
 if((int)response.StatusCode!=expected)throw new Exception($"{name}: expected {expected}, received {(int)response.StatusCode}");
 passed++;Console.WriteLine("PASS: "+name);
}
var portal=$"/api/portal/conversations/{thread}";var admin=$"/api/admin/conversations/{thread}";
var message=new MessageInput("A portal reply",Guid.NewGuid());
await Check("Anonymous messages denied","GET",portal+"/messages",401,role:null);
await Check("Client admin inbox denied","GET",admin+"/messages",403);
await Check("Own thread readable","GET",portal+"/messages",200);
await Check("Other thread hidden","GET",$"/api/portal/conversations/{Guid.NewGuid()}/messages",404);
await Check("Other thread cannot receive reply","POST",$"/api/portal/conversations/{Guid.NewGuid()}/messages",404,message);
await Check("Reply accepted","POST",portal+"/messages",201,message);
fake.Replay=true;await Check("Retry returns existing reply","POST",portal+"/messages",200,message);fake.Replay=false;
await Check("Empty reply rejected","POST",portal+"/messages",400,message with {Body="  "});
await Check("Oversized reply rejected","POST",portal+"/messages",400,message with {Body=new string('a',20001)});
await Check("Null character rejected","POST",portal+"/messages",400,message with {Body="bad\0body"});
await Check("Retry key required","POST",portal+"/messages",400,message with {ClientMessageKey=null});
await Check("Empty UUID rejected","POST",portal+"/messages",400,message with {ClientMessageKey=Guid.Empty});
await Check("Page zero rejected","GET",portal+"/messages?page=0",400);
await Check("Oversized page rejected","GET",portal+"/messages?pageSize=101",400);
await Check("Staff reads messages","GET",admin+"/messages",200,role:"staff");
await Check("Staff replies","POST",admin+"/messages",201,message,"staff");
await Check("Staff notes readable","GET",admin+"/notes",200,role:"staff");
await Check("Staff note accepted","POST",admin+"/notes",201,new NoteInput("Private note"),"staff");
await Check("Client cannot read staff notes","GET",admin+"/notes",403);
await Check("Client cannot write staff notes","POST",admin+"/notes",403,new NoteInput("Private note"));
await Check("Portal has no notes endpoint","GET",portal+"/notes",404);
await Check("Blank note rejected","POST",admin+"/notes",400,new NoteInput(" "),"staff");
await Check("Large note rejected","POST",admin+"/notes",400,new NoteInput(new string('a',10001)),"staff");
await Check("Invalid notes page rejected","GET",admin+"/notes?pageSize=0",400,role:"staff");
await Check("Oversized HTTP payload rejected","POST",portal+"/messages",413,message with {Body=new string('x',140000)});
await Check("Staff obtains enquiry thread","GET",$"/api/admin/enquiries/{FakeStore.EnquiryId}/conversation",200,role:"staff");
await Check("Client obtains own enquiry thread","GET",$"/api/portal/enquiries/{FakeStore.EnquiryId}/conversation",200);
await Check("Other enquiry hidden","GET",$"/api/portal/enquiries/{Guid.NewGuid()}/conversation",404);
fake.Revoked=true;await Check("Revoked participant cannot obtain thread","GET",$"/api/portal/enquiries/{FakeStore.EnquiryId}/conversation",404);
fake.Revoked=false;
bool limited=false;
for(var i=0;i<61;i++)
{
 using var request=new HttpRequestMessage(HttpMethod.Post,portal+"/messages");request.Headers.Authorization=new("Bearer","client");request.Content=JsonContent.Create(message);
 using var response=await client.SendAsync(request);
 if((int)response.StatusCode==429){limited=true;break;}
 if((int)response.StatusCode!=201)throw new Exception("Unexpected reply status while testing rate limit.");
}
if(!limited)throw new Exception("Reply rate limit was not enforced.");passed++;Console.WriteLine("PASS: Reply rate limit returns 429");
Console.WriteLine($"{passed} conversation HTTP checks passed.");

sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
{
 protected override Task<AuthenticateResult> HandleAuthenticateAsync()
 {
  var token=Request.Headers.Authorization.ToString();if(!token.StartsWith("Bearer "))return Task.FromResult(AuthenticateResult.NoResult());
  return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role,token[7..])],"test")),"test")));
 }
}
sealed class FakeStore:IConversationStore,IEnquiryConversationAccess
{
 public static readonly Guid ThreadId=Guid.NewGuid(),EnquiryId=Guid.NewGuid();public bool Replay,Revoked;
 public Task<ThreadRecord?> EnsureForEnquiry(Guid id,CancellationToken ct)=>Task.FromResult<ThreadRecord?>(id==EnquiryId?new(ThreadId,id,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow):null);
 public Task<bool> CanAccess(Guid id,ClaimsPrincipal user,CancellationToken ct)=>Task.FromResult(!Revoked&&id==ThreadId);
 Task<bool> IEnquiryConversationAccess.CanAccess(Guid id,ClaimsPrincipal user,CancellationToken ct)=>Task.FromResult(id==EnquiryId);
 public Task<EnquiryConversationSource?> Source(Guid id,CancellationToken ct)=>Task.FromResult<EnquiryConversationSource?>(null);
 public Task<MessagePage> Messages(Guid id,int page,int size,CancellationToken ct)=>Task.FromResult(new MessagePage([],0,page,size));
 public async Task<MessageResult?> Send(Guid id,MessageInput input,ClaimsPrincipal user,CancellationToken ct)=>await CanAccess(id,user,ct)?new(new(Guid.NewGuid(),id,"client",Guid.NewGuid(),"portal",input.Body!,DateTimeOffset.UtcNow),!Replay):null;
 public Task<NotePage> Notes(Guid id,int page,int size,CancellationToken ct)=>Task.FromResult(new NotePage([],0,page,size));
 public Task<InternalNote?> AddNote(Guid id,NoteInput input,ClaimsPrincipal user,CancellationToken ct)=>Task.FromResult<InternalNote?>(new(Guid.NewGuid(),id,Guid.NewGuid(),input.Body!,DateTimeOffset.UtcNow));
}
