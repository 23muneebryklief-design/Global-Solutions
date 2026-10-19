using System.Security.Claims;
using System.Text.Json;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Notifications;

public static class IncomingEmailEndpoints
{
    public static void MapIncomingEmailModule(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/resend",async(HttpContext context,IOptions<IncomingEmailOptions> options,IIncomingEmailStore store,CancellationToken ct)=>
        {
            var config=options.Value;
            if(!config.Enabled||!WebhookSignature.Configured(config.WebhookSecret))return Results.StatusCode(503);
            const int limit=256*1024;
            var feature=context.Features.Get<IHttpMaxRequestBodySizeFeature>();if(feature is {IsReadOnly:false})feature.MaxRequestBodySize=limit;
            if(context.Request.ContentLength>limit)return Results.StatusCode(413);
            var headers=context.Request.Headers;
            if(headers["svix-id"].Count!=1||headers["svix-timestamp"].Count!=1||headers["svix-signature"].Count!=1)return Results.Unauthorized();
            using var memory=new MemoryStream();var buffer=new byte[8192];int read;
            while((read=await context.Request.Body.ReadAsync(buffer,ct))>0){if(memory.Length+read>limit)return Results.StatusCode(413);memory.Write(buffer,0,read);}
            var raw=memory.ToArray();var id=headers["svix-id"].ToString();
            if(!WebhookSignature.Verify(raw,id,headers["svix-timestamp"].ToString(),headers["svix-signature"].ToString(),[config.WebhookSecret,config.PreviousWebhookSecret],DateTimeOffset.UtcNow))return Results.Unauthorized();
            VerifiedEmailEvent input;
            try{input=IncomingEmailParsing.Parse(id,raw);}catch(Exception ex)when(ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){return Results.BadRequest(new {message="Invalid webhook event."});}
            await store.Receive(input,ct);return Results.Accepted();
        }).AllowAnonymous().WithTags("Email webhooks").RequireRateLimiting("email-webhook");
        var admin=app.MapGroup("/api/admin/email-events").RequireAuthorization(AccessPolicies.Staff).WithTags("Incoming email review");
        admin.MapGet("/configuration",(IOptions<IncomingEmailOptions> incoming,IOptions<EmailOptions> outgoing)=>Results.Ok(new {enabled=incoming.Value.Enabled,domainConfigured=incoming.Value.DomainValid,signatureConfigured=WebhookSignature.Configured(incoming.Value.WebhookSecret),providerConfigured=!string.IsNullOrWhiteSpace(outgoing.Value.ApiKey)}));
        admin.MapGet("/",async(string? status,int? page,int? pageSize,IIncomingEmailStore store,CancellationToken ct)=>
        {
            if(status is not(null or "pending" or "processing" or "processed" or "unmatched" or "failed")||(page??1)is <1 or >100000||(pageSize??25)is <1 or >100)return Results.BadRequest(new {message="Choose a valid event status, page 1–100000 and pageSize 1–100."});
            return Results.Ok(await store.List(status,page??1,pageSize??25,ct));
        });
        admin.MapGet("/{id:guid}",async(Guid id,IIncomingEmailStore store,CancellationToken ct)=>await store.Detail(id,ct)is {} item?Results.Ok(item):Results.NotFound());
        admin.MapPost("/{id:guid}/review",async(Guid id,ReviewEmailInput input,ClaimsPrincipal user,IIncomingEmailStore store,CancellationToken ct)=>
        {
            if(input.ExpectedUpdatedAt is null||input.Action is not("attach" or "ignore" or "retry")||string.IsNullOrWhiteSpace(input.Reason)||input.Reason.Trim().Length>1000||input.Reason.Contains('\0')||
               (input.Action=="attach"&&(input.ThreadId is null||input.ThreadId==Guid.Empty))||input.EditedBody?.Length>20000||input.EditedBody?.Contains('\0')==true)
                return Results.BadRequest(new {message="Provide expectedUpdatedAt, attach/ignore/retry, a review reason, and a threadId for attach. Edited text is limited to 20000 characters."});
            return await store.Review(id,input,user.AppId(),ct)?Results.NoContent():Results.NotFound();
        });
    }
}
