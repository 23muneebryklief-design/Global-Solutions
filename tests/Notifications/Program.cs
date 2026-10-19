using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
var fake=new FakeOutbox();builder.Services.AddSingleton<IEmailOutbox>(fake);builder.Services.Configure<EmailOptions>(_=>{});
await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapNotificationsModule();await app.StartAsync();var client=app.GetTestClient();int passed=0;
void Assert(bool value){if(!value)throw new Exception("Notification assertion failed.");}
async Task Check(string name,Func<Task> run){await run();passed++;Console.WriteLine("PASS: "+name);}
async Task Status(string name,string path,int status,string? role="staff")=>await Check(name,async()=>{using var req=new HttpRequestMessage(HttpMethod.Get,path);if(role is not null)req.Headers.Authorization=new("Bearer",role);using var res=await client.SendAsync(req);Assert((int)res.StatusCode==status);});
await Status("Anonymous cannot view queue","/api/admin/emails",401,null);
await Status("Client cannot view queue","/api/admin/emails",403,"client");
await Status("Staff can view queue","/api/admin/emails",200);
await Status("Admin can view queue","/api/admin/emails",200,"admin");
await Status("Invalid delivery status rejected","/api/admin/emails?status=delivered",400);
await Status("Invalid queue page rejected","/api/admin/emails?page=0",400);
await Status("Oversized queue page rejected","/api/admin/emails?pageSize=101",400);
await Status("Staff can view safe configuration","/api/admin/emails/configuration",200);
await Status("Client cannot view configuration","/api/admin/emails/configuration",403,"client");
await Status("Unknown email attempts return 404",$"/api/admin/emails/{Guid.NewGuid()}/attempts",404);
await Check("Configuration never exposes API key",async()=>{using var req=new HttpRequestMessage(HttpMethod.Get,"/api/admin/emails/configuration");req.Headers.Authorization=new("Bearer","staff");using var res=await client.SendAsync(req);Assert(!(await res.Content.ReadAsStringAsync()).Contains("apiKey",StringComparison.OrdinalIgnoreCase));});
var config=new EmailOptions{Enabled=true,ApiKey="test-only",FromEmail="sender@example.invalid",ReplyTo="reply@example.invalid"};
var email=new OutboxEmail(Guid.NewGuid(),Guid.NewGuid(),1,"recipient@example.invalid","Subject","Body","sender@example.invalid","reply@example.invalid",DateTimeOffset.UtcNow);
var handler=new ProviderHandler();using var http=new HttpClient(handler);var sender=new ResendEmailSender(http,Options.Create(config));
await Check("Provider request uses stable idempotency and plaintext payload",async()=>{var r=await sender.Send(email,default);Assert(r.Accepted&&r.ProviderMessageId=="provider-123"&&handler.Key=="gts-email/"+email.Id&&handler.Body!.Contains("reply_to")&&handler.Body.Contains("Body"));});
await Check("Retried request reuses provider key",async()=>{await sender.Send(email with {Attempt=2},default);Assert(handler.Key=="gts-email/"+email.Id);});
await Check("Provider rate limit is retryable with delay",async()=>{handler.Status=429;var r=await sender.Send(email,default);Assert(r.Retryable&&r.RetryAfter==TimeSpan.FromSeconds(120)&&r.Code=="http_429");});
await Check("Provider outage is retryable",async()=>{handler.Status=503;Assert((await sender.Send(email,default)).Retryable);});
await Check("Invalid credentials are terminal",async()=>{handler.Status=401;Assert(!(await sender.Send(email,default)).Retryable);});
await Check("Validation errors are terminal",async()=>{handler.Status=422;Assert(!(await sender.Send(email,default)).Retryable);});
await Check("Network failure remains unknown and retryable",async()=>{handler.NetworkFailure=true;var r=await sender.Send(email,default);Assert(r.Unknown&&r.Retryable);handler.NetworkFailure=false;});
await Check("Ambiguous success is not falsely marked sent",async()=>{handler.Status=200;handler.InvalidBody=true;var r=await sender.Send(email,default);Assert(!r.Accepted&&r.Unknown&&r.Retryable);});
await Check("Disabled processor does not claim or send",async()=>{var processor=new EmailProcessor(fake,sender,Options.Create(new EmailOptions()));Assert(!await processor.ProcessOne(default)&&fake.Claims==0);});
await Check("Disabled provider adapter makes no request",async()=>{var calls=handler.Calls;var disabled=new ResendEmailSender(http,Options.Create(new EmailOptions()));Assert(!(await disabled.Send(email,default)).Accepted&&handler.Calls==calls);});
Console.WriteLine($"{passed} notification HTTP/provider checks passed.");

sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
{protected override Task<AuthenticateResult> HandleAuthenticateAsync(){var h=Request.Headers.Authorization.ToString();return Task.FromResult(!h.StartsWith("Bearer ")?AuthenticateResult.NoResult():AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role,h[7..])],"test")),"test")));}}
sealed class FakeOutbox:IEmailOutbox
{
 public int Claims;
 public Task<OutboxEmail?> Claim(CancellationToken ct){Claims++;return Task.FromResult<OutboxEmail?>(null);}
 public Task<bool> Complete(OutboxEmail e,EmailSendResult r,CancellationToken ct)=>Task.FromResult(true);
 public Task<EmailPage> List(string? status,int page,int size,CancellationToken ct)=>Task.FromResult(new EmailPage([],0,page,size));
 public Task<List<DeliveryAttempt>?> Attempts(Guid id,CancellationToken ct)=>Task.FromResult<List<DeliveryAttempt>?>(null);
}
sealed class ProviderHandler:HttpMessageHandler
{
 public int Status=200,Calls;public bool NetworkFailure,InvalidBody;public string? Key,Body;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  Calls++;if(NetworkFailure)throw new HttpRequestException();Key=request.Headers.GetValues("Idempotency-Key").Single();Body=await request.Content!.ReadAsStringAsync(ct);
  var response=new HttpResponseMessage((HttpStatusCode)Status){Content=new StringContent(InvalidBody?"{}":"{\"id\":\"provider-123\"}")};response.Headers.RetryAfter=new(TimeSpan.FromSeconds(120));return response;
 }
}
