using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using GlobalTalentSolutions.Notifications;
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
builder.Services.AddAuthorization(o=>o.AddPolicy(AccessPolicies.Staff,p=>p.RequireAuthenticatedUser().RequireRole("staff","admin")));
builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,TestAuth>("test",_=>{});
builder.Services.AddRateLimiter(o=>o.AddPolicy("email-webhook",_=>RateLimitPartition.GetNoLimiter("tests")));
var key=RandomNumberGenerator.GetBytes(32);var secret="whsec_"+Convert.ToBase64String(key);var config=new IncomingEmailOptions{Enabled=true,Domain="inbound.example.invalid",WebhookSecret=secret};
builder.Services.AddSingleton<IOptions<IncomingEmailOptions>>(Options.Create(config));builder.Services.Configure<EmailOptions>(_=>{});var store=new FakeStore();builder.Services.AddSingleton<IIncomingEmailStore>(store);
await using var app=builder.Build();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();app.MapIncomingEmailModule();await app.StartAsync();var client=app.GetTestClient();int passed=0;
void Assert(bool value){if(!value)throw new Exception("Incoming HTTP assertion failed.");}
async Task Check(string name,Func<Task> run){await run();passed++;Console.WriteLine("PASS: "+name);}
string Event()=>JsonSerializer.Serialize(new {type="email.received",created_at=DateTimeOffset.UtcNow,data=new {email_id=Guid.NewGuid()}});
string Sign(string body,string id,string time,byte[]? bytes=null)=>"v1,"+Convert.ToBase64String(HMACSHA256.HashData(bytes??key,Encoding.UTF8.GetBytes(id+"."+time+"."+body)));
async Task<int> Webhook(string body,bool headers=true,string? signature=null,string? timestamp=null,string? signedBody=null)
{
 var id="msg_test";var time=timestamp??DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();using var req=new HttpRequestMessage(HttpMethod.Post,"/api/webhooks/resend");req.Content=new StringContent(body,Encoding.UTF8,"application/json");
 if(headers){req.Headers.Add("svix-id",id);req.Headers.Add("svix-timestamp",time);req.Headers.Add("svix-signature",signature??Sign(signedBody??body,id,time));}
 using var response=await client.SendAsync(req);return (int)response.StatusCode;
}
await Check("Svix published reference vector verifies",()=>{Assert(WebhookSignature.Verify(Encoding.UTF8.GetBytes("{\"event_type\":\"ping\",\"data\":{\"success\":true}}"),"msg_loFOjxBNrRLzqYUf","1731705121","v1,rAvfW3dJ/X/qxhsaXPOyyCGmRKsaKWcsNccKXlIktD0=",["whsec_plJ3nmyCDGBKInavdOK15jsl"],DateTimeOffset.FromUnixTimeSeconds(1731705121)));return Task.CompletedTask;});
await Check("Signed webhook needs no portal login",async()=>Assert(await Webhook(Event())==202));
await Check("Missing signature rejected",async()=>Assert(await Webhook(Event(),false)==401));
await Check("Invalid signature rejected",async()=>Assert(await Webhook(Event(),signature:"v1,bad")==401));
await Check("Payload tampering rejected",async()=>Assert(await Webhook(Event(),signedBody:"different")==401));
await Check("Stale signature rejected",async()=>Assert(await Webhook(Event(),timestamp:DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds().ToString())==401));
await Check("Future timestamp rejected",async()=>Assert(await Webhook(Event(),timestamp:DateTimeOffset.UtcNow.AddMinutes(6).ToUnixTimeSeconds().ToString())==401));
await Check("Raw whitespace is preserved for verification",async()=>Assert(await Webhook("  "+Event()+"\n")==202));
await Check("Malformed signed JSON rejected",async()=>Assert(await Webhook("not json")==400));
await Check("Unsupported signed event acknowledged",async()=>Assert(await Webhook(JsonSerializer.Serialize(new {type="email.opened",created_at=DateTimeOffset.UtcNow,data=new{}}))==202));
await Check("Oversized body rejected",async()=>Assert(await Webhook(new string('x',270000))==413));
await Check("Secret rotation accepts the previous secret",async()=>{config.PreviousWebhookSecret=secret;config.WebhookSecret="whsec_"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));Assert(await Webhook(Event())==202);config.WebhookSecret=secret;config.PreviousWebhookSecret="";});
await Check("Disabled reception returns 503 without storing",async()=>{config.Enabled=false;var count=store.Received;Assert(await Webhook(Event())==503&&store.Received==count);config.Enabled=true;});
async Task Status(string name,string path,int status,string? role=null,object? body=null)=>await Check(name,async()=>{using var req=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,path);if(role is not null)req.Headers.Authorization=new("Bearer",role);if(body is not null)req.Content=JsonContent.Create(body);using var res=await client.SendAsync(req);Assert((int)res.StatusCode==status);});
await Status("Anonymous cannot view review queue","/api/admin/email-events",401);
await Status("Client cannot view review queue","/api/admin/email-events",403,"client");
await Status("Staff can view review queue","/api/admin/email-events?status=unmatched",200,"staff");
await Status("Invalid status rejected","/api/admin/email-events?status=sent",400,"staff");
await Status("Invalid page rejected","/api/admin/email-events?page=0",400,"staff");
await Status("Oversized page rejected","/api/admin/email-events?pageSize=101",400,"staff");
await Status("Missing detail returns 404",$"/api/admin/email-events/{Guid.NewGuid()}",404,"staff");
await Status("Staff can inspect safe configuration","/api/admin/email-events/configuration",200,"staff");
var review=new ReviewEmailInput(DateTimeOffset.UtcNow,"attach",Guid.NewGuid(),"Confirmed contact by phone",null);
await Status("Client cannot attach a reviewed email",$"/api/admin/email-events/{Guid.NewGuid()}/review",403,"client",review);
await Status("Review requires a reason",$"/api/admin/email-events/{Guid.NewGuid()}/review",400,"staff",review with {Reason=""});
await Status("Review requires concurrency timestamp",$"/api/admin/email-events/{Guid.NewGuid()}/review",400,"staff",review with {ExpectedUpdatedAt=null});
await Status("Attach requires target thread",$"/api/admin/email-events/{Guid.NewGuid()}/review",400,"staff",review with {ThreadId=null});
await Status("Unknown review returns 404",$"/api/admin/email-events/{Guid.NewGuid()}/review",404,"staff",review);
var receivedId=Guid.NewGuid().ToString();var handler=new ReceivedHandler(receivedId);using var http=new HttpClient(handler);var provider=new ReceivedEmailProvider(http,Options.Create(new EmailOptions{ApiKey="fake-never-sent"}));
await Check("Provider parser uses trusted authentication result",async()=>{var email=await provider.Get(receivedId,default);Assert(email.Authenticated&&email.Sender=="contact@example.invalid"&&handler.Path!.Contains("html_format=cid"));});
await Check("Forged headers do not replace missing authentication",async()=>{handler.Authentication=false;Assert(!(await provider.Get(receivedId,default)).Authenticated);handler.Authentication=true;});
await Check("Failed DMARC cannot auto-route",async()=>{handler.Dmarc="fail";Assert(!(await provider.Get(receivedId,default)).Authenticated);handler.Dmarc="pass";});
await Check("Provider ID mismatch rejected",async()=>{handler.Mismatch=true;try{await provider.Get(receivedId,default);throw new Exception("Expected mismatch");}catch(ReceivedEmailException e){Assert(!e.Retryable);}handler.Mismatch=false;});
await Check("Provider 429 will retry",async()=>{handler.Status=429;try{await provider.Get(receivedId,default);throw new Exception("Expected provider error");}catch(ReceivedEmailException e){Assert(e.Retryable);}handler.Status=200;});
await Check("Provider 401 stops automatic retry",async()=>{handler.Status=401;try{await provider.Get(receivedId,default);throw new Exception("Expected provider error");}catch(ReceivedEmailException e){Assert(!e.Retryable);}});
Console.WriteLine($"{passed} incoming HTTP/signature/provider checks passed.");

sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
{protected override Task<AuthenticateResult> HandleAuthenticateAsync(){var h=Request.Headers.Authorization.ToString();return Task.FromResult(!h.StartsWith("Bearer ")?AuthenticateResult.NoResult():AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role,h[7..]),new("app_user_id",Guid.NewGuid().ToString())],"test")),"test")));}}
sealed class FakeStore:IIncomingEmailStore
{
 public int Received;
 public Task<bool> Receive(VerifiedEmailEvent input,CancellationToken ct){Received++;return Task.FromResult(true);}
 public Task<ClaimedEmailEvent?> Claim(CancellationToken ct)=>Task.FromResult<ClaimedEmailEvent?>(null);
 public Task<bool> Finish(ClaimedEmailEvent item,IncomingEmail? content,CancellationToken ct)=>Task.FromResult(true);
 public Task Fail(ClaimedEmailEvent item,string code,bool retryable,CancellationToken ct)=>Task.CompletedTask;
 public Task<EmailEventPage> List(string? status,int page,int size,CancellationToken ct)=>Task.FromResult(new EmailEventPage([],0,page,size));
 public Task<EmailReviewDetail?> Detail(Guid id,CancellationToken ct)=>Task.FromResult<EmailReviewDetail?>(null);
 public Task<bool> Review(Guid id,ReviewEmailInput input,Guid staffId,CancellationToken ct)=>Task.FromResult(false);
}
sealed class ReceivedHandler(string id):HttpMessageHandler
{
 public bool Authentication=true,Mismatch;public string Dmarc="pass";public int Status=200;public string? Path;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  Path=request.RequestUri!.ToString();var content=JsonSerializer.Serialize(new {id=Mismatch?Guid.NewGuid().ToString():id,from="Contact <contact@example.invalid>",to=new[]{"r-test@inbound.example.invalid"},subject="Reply",text="Plain text reply",html=(string?)null,headers=new Dictionary<string,string>{{"authentication-results","dmarc=pass; spf=pass"}},authentication=Authentication?new {spf="pass",dkim="pass",dmarc=Dmarc}:null,attachments=Array.Empty<object>()});
  return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=new StringContent(content)});
 }
}
