using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using GlobalTalentSolutions.SharedKernel;
using System.Threading.RateLimiting;

namespace GlobalTalentSolutions.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services,IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection("Email"));
        services.Configure<IncomingEmailOptions>(configuration.GetSection("IncomingEmail"));
        services.AddScoped<IIncomingEmailStore,IncomingEmailStore>();
        services.AddScoped<IncomingEmailProcessor>();services.AddHostedService<IncomingEmailWorker>();
        services.AddHttpClient<IReceivedEmailProvider,ReceivedEmailProvider>(c=>c.Timeout=TimeSpan.FromSeconds(20)).RedactLoggedHeaders(["Authorization"]).RemoveAllLoggers();
        services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("email-webhook",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions {PermitLimit=300,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
        var protection=services.AddDataProtection().SetApplicationName("GlobalTalentSolutions");
        if(configuration["Email:KeyDirectory"] is {Length:>0} path)protection.PersistKeysToFileSystem(new DirectoryInfo(path));
        services.AddScoped<EmailQueue>();
        services.AddScoped<IEmailNotifications>(s=>s.GetRequiredService<EmailQueue>());
        services.AddScoped<IEmailOutbox>(s=>s.GetRequiredService<EmailQueue>());
        services.AddHttpClient<IEmailSender,ResendEmailSender>(c=>c.Timeout=TimeSpan.FromSeconds(20))
            .RedactLoggedHeaders(["Authorization"]).RemoveAllLoggers();
        services.AddScoped<EmailProcessor>();services.AddHostedService<EmailWorker>();
        return services;
    }
    public static void MapNotificationsModule(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/admin/emails").RequireAuthorization(AccessPolicies.Staff).WithTags("Email delivery");
        group.MapGet("/configuration",(IOptions<EmailOptions> options)=>Results.Ok(new {enabled=options.Value.Enabled,configured=options.Value.Configured,provider="resend"}));
        group.MapGet("/",async(string? status,int? page,int? pageSize,IEmailOutbox store,CancellationToken ct)=>
        {
            if(status is not (null or "pending" or "processing" or "sent" or "failed" or "cancelled") || (page??1)is <1 or >100000 || (pageSize??25)is <1 or >100)
                return Results.BadRequest(new {message="Choose a valid status, page 1–100000 and pageSize 1–100."});
            return Results.Ok(await store.List(status,page??1,pageSize??25,ct));
        });
        group.MapGet("/{id:guid}/attempts",async(Guid id,IEmailOutbox store,CancellationToken ct)=>await store.Attempts(id,ct)is {} result?Results.Ok(result):Results.NotFound());
    }
}
