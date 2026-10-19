using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.Notifications;
using GlobalTalentSolutions.Documents;
using GlobalTalentSolutions.Recruitment;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
var databaseConnection = builder.Configuration.GetConnectionString("Supabase");
if (!string.IsNullOrWhiteSpace(databaseConnection))
{
    var connectionOptions = new NpgsqlConnectionStringBuilder(databaseConnection);
    if (string.IsNullOrWhiteSpace(connectionOptions.RootCertificate))
        connectionOptions.RootCertificate = Path.Combine(AppContext.BaseDirectory, "Certificates", "supabase-ca.crt");
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionOptions.ConnectionString));
}
builder.Services.AddHealthChecks()
    .AddCheck<SupabaseDatabaseHealthCheck>("supabase-postgres", tags: ["ready"]);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() { Title = "Global Talent Solutions API", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Sign in with /api/auth/login and paste the accessToken here."
    });
    options.OperationFilter<EndpointSecurityDocumentation>();
});
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddEnquiriesModule();
builder.Services.AddConversationsModule();
builder.Services.AddNotificationsModule(builder.Configuration);
builder.Services.AddDocumentsModule();
builder.Services.AddRecruitmentModule();

var app = builder.Build();
app.Use(async (context, next) =>
{
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    try { await next(context); }
    finally
    {
        app.Logger.LogInformation(
            "HTTP {Method} {Path} returned {StatusCode} in {ElapsedMs} ms; TraceId {TraceId}",
            context.Request.Method, context.Request.Path, context.Response.StatusCode,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            context.TraceIdentifier);
    }
});
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseEnquiryRequestLimits();
app.UseConversationRequestLimits();
app.UseRateLimiter();
app.Use(async (context,next) =>
{
    if(context.Request.Path.StartsWithSegments("/api/auth") || context.Request.Path.StartsWithSegments("/api/admin") || context.Request.Path.StartsWithSegments("/api/portal"))
        context.Response.Headers.CacheControl="no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.MapHealthChecks("/api/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/api/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapIdentityModule();
app.MapEnquiriesModule();
app.MapOnboardingModule();
app.MapConversationsModule();
app.MapPortalConversationList();
app.MapNotificationsModule();
app.MapIncomingEmailModule();
app.Run();

public partial class Program;
