using System.Security.Claims;
using System.Threading.RateLimiting;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GlobalTalentSolutions.Conversations;

public static class ConversationsModule
{
    public static IServiceCollection AddConversationsModule(this IServiceCollection services)
    {
        services.AddScoped<IEnquiryThreadInitializer,ThreadInitializer>();
        services.AddScoped<IConversationStore,ConversationStore>();
        services.AddScoped<IPortalConversationList,PortalConversationList>();
        services.AddScoped<IIncomingConversations,IncomingConversations>();
        services.AddRateLimiter(options=>{options.RejectionStatusCode=429;options.AddPolicy("conversation-write",context=>RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString()??"unknown",
            _=>new FixedWindowRateLimiterOptions {PermitLimit=60,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
        return services;
    }
    public static void UseConversationRequestLimits(this IApplicationBuilder app)
    {
        app.Use(async(context,next)=>
        {
            if(HttpMethods.IsPost(context.Request.Method) &&
               (context.Request.Path.StartsWithSegments("/api/admin/conversations")||context.Request.Path.StartsWithSegments("/api/portal/conversations")))
            {
                const long limit=128*1024;
                var feature=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if(feature is {IsReadOnly:false})feature.MaxRequestBodySize=limit;
                if(context.Request.ContentLength>limit){context.Response.StatusCode=413;return;}
            }
            await next(context);
        });
    }
    public static void MapConversationsModule(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/enquiries/{id:guid}/conversation",async(Guid id,IConversationStore store,CancellationToken ct)=>
            await store.EnsureForEnquiry(id,ct) is {} item?Results.Ok(item):Results.NotFound())
            .RequireAuthorization(AccessPolicies.Staff).WithTags("Admin conversations");
        app.MapGet("/api/portal/enquiries/{id:guid}/conversation",async(Guid id,ClaimsPrincipal user,IEnquiryConversationAccess access,IConversationStore store,CancellationToken ct)=>
        {
            if(!await access.CanAccess(id,user,ct))return Results.NotFound();
            var item=await store.EnsureForEnquiry(id,ct);
            return item is not null && await store.CanAccess(item.Id,user,ct)?Results.Ok(item):Results.NotFound();
        }).RequireAuthorization(AccessPolicies.AppUser).WithTags("Portal conversations");
        var admin=app.MapGroup("/api/admin/conversations").RequireAuthorization(AccessPolicies.Staff).WithTags("Admin conversations");
        var portal=app.MapGroup("/api/portal/conversations").RequireAuthorization(AccessPolicies.AppUser).WithTags("Portal conversations");
        MapMessages(admin);MapMessages(portal);
        admin.MapGet("/{id:guid}/notes",async(Guid id,int? page,int? pageSize,ClaimsPrincipal user,IConversationStore store,CancellationToken ct)=>
        {
            if(!PageValid(page,pageSize))return Results.BadRequest(new {message="Use page 1–100000 and pageSize 1–100."});
            if(!await store.CanAccess(id,user,ct))return Results.NotFound();
            return Results.Ok(await store.Notes(id,page??1,pageSize??50,ct));
        });
        admin.MapPost("/{id:guid}/notes",async(Guid id,NoteInput input,ClaimsPrincipal user,IConversationStore store,CancellationToken ct)=>
        {
            if(!ValidBody(input.Body,10000))return Results.ValidationProblem(new Dictionary<string,string[]>{["body"]=["Note must be 1–10000 characters without null characters."]});
            return await store.AddNote(id,input,user,ct) is {} note?Results.Json(note,statusCode:201):Results.NotFound();
        }).RequireRateLimiting("conversation-write");
    }
    private static void MapMessages(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/messages",async(Guid id,int? page,int? pageSize,ClaimsPrincipal user,IConversationStore store,CancellationToken ct)=>
        {
            if(!PageValid(page,pageSize))return Results.BadRequest(new {message="Use page 1–100000 and pageSize 1–100."});
            if(!await store.CanAccess(id,user,ct))return Results.NotFound();
            return Results.Ok(await store.Messages(id,page??1,pageSize??50,ct));
        });
        group.MapPost("/{id:guid}/messages",async(Guid id,MessageInput input,ClaimsPrincipal user,IConversationStore store,CancellationToken ct)=>
        {
            if(!ValidBody(input.Body,20000)||input.ClientMessageKey is null||input.ClientMessageKey==Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string,string[]>{["message"]=["Provide a body of 1–20000 characters without null characters, and a nonempty clientMessageKey UUID."]});
            var result=await store.Send(id,input,user,ct);
            return result is null?Results.NotFound():Results.Json(result.Message,statusCode:result.Created?201:200);
        }).RequireRateLimiting("conversation-write");
    }
    private static bool PageValid(int? page,int? size)=>(page??1) is >=1 and <=100000 && (size??50) is >=1 and <=100;
    private static bool ValidBody(string? body,int max)=>!string.IsNullOrWhiteSpace(body)&&body.Trim().Length<=max&&!body.Contains('\0');
}
