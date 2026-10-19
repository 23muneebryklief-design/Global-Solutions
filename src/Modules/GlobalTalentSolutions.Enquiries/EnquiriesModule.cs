using System.Threading.RateLimiting;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GlobalTalentSolutions.Enquiries;

public static class EnquiriesModule
{
    public static IServiceCollection AddEnquiriesModule(this IServiceCollection services)
    {
        services.AddScoped<EnquiryStore>();
        services.AddScoped<IEnquiryStore>(provider=>provider.GetRequiredService<EnquiryStore>());
        services.AddScoped<IEnquiryConversationAccess>(provider=>provider.GetRequiredService<EnquiryStore>());
        services.AddScoped<IEnquiryMailDetails>(provider=>provider.GetRequiredService<EnquiryStore>());
        services.AddScoped<OnboardingStore>();
        services.AddScoped<IEnquiryOnboarding>(provider=>provider.GetRequiredService<OnboardingStore>());
        services.AddScoped<IInvitationAcceptanceLinker>(provider=>provider.GetRequiredService<OnboardingStore>());
        services.AddScoped<IPortalEnquiries>(provider=>provider.GetRequiredService<OnboardingStore>());
        services.AddRateLimiter(options=>
        {
            options.RejectionStatusCode=StatusCodes.Status429TooManyRequests;
            options.AddPolicy("enquiry-form",context=>RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString()??"unknown",
                _=>new FixedWindowRateLimiterOptions { PermitLimit=5,Window=TimeSpan.FromMinutes(10),QueueLimit=0 }));
        });
        return services;
    }
    public static void UseEnquiryRequestLimits(this IApplicationBuilder app)
    {
        app.Use(async(context,next)=>
        {
            if(HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.Value?.TrimEnd('/').Equals("/api/enquiries",StringComparison.OrdinalIgnoreCase)==true)
            {
                const long limit=64*1024;
                var bodyLimit=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if(bodyLimit is {IsReadOnly:false}) bodyLimit.MaxRequestBodySize=limit;
                if(context.Request.ContentLength>limit) {context.Response.StatusCode=413;return;}
            }
            await next(context);
        });
    }
    public static void MapEnquiriesModule(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/enquiries",async(SubmitEnquiry input,IEnquiryStore store,HttpContext context,CancellationToken ct)=>
        {
            context.Response.Headers.CacheControl="no-store";
            if(!string.IsNullOrWhiteSpace(input.Website))
                return Results.Accepted(value:new {message="Your enquiry has been received."});
            var errors=EnquiryValidation.Validate(input);
            if(errors.Count>0) return Results.ValidationProblem(errors);
            var result=await store.Submit(EnquiryValidation.Normalize(input),ct);
            return Results.Json(new EnquiryReceipt(result.Reference),statusCode:result.Created?201:200);
        }).AllowAnonymous().RequireRateLimiting("enquiry-form").WithTags("Public enquiries")
          .WithName("SubmitEnquiry").Produces<EnquiryReceipt>(201).Produces<EnquiryReceipt>(200)
          .ProducesValidationProblem().Produces(413).Produces(429);

        var staff=app.MapGroup("/api/admin/enquiries").RequireAuthorization(AccessPolicies.Staff).WithTags("Admin enquiries");
        staff.MapGet("/",async(string? status,string? enquiryType,Guid? assignedStaffId,bool? unassigned,string? search,int? page,int? pageSize,IEnquiryStore store,CancellationToken ct)=>
        {
            var query=new EnquiryQuery(status,enquiryType,assignedStaffId,unassigned??false,search,page??1,pageSize??25);
            var errors=EnquiryValidation.Validate(query);
            if(errors.Count>0) return Results.ValidationProblem(errors);
            return Results.Ok(await store.List(query,ct));
        }).WithName("ListEnquiries");
        staff.MapGet("/assignees",async(IStaffDirectory directory,CancellationToken ct)=>Results.Ok(await directory.ActiveStaff(ct)))
            .WithName("ListEnquiryAssignees");
        staff.MapGet("/{id:guid}",async(Guid id,IEnquiryStore store,CancellationToken ct)=>
            await store.Get(id,ct) is {} enquiry?Results.Ok(enquiry):Results.NotFound()).WithName("GetEnquiry");
        staff.MapPatch("/{id:guid}",async(Guid id,UpdateEnquiry input,IEnquiryStore store,IStaffDirectory directory,CancellationToken ct)=>
        {
            var errors=EnquiryValidation.Validate(input);
            if(errors.Count>0) return Results.ValidationProblem(errors);
            if(input.AssignedStaffId is {} assignee && !await directory.IsActiveStaff(assignee,ct))
                return Results.ValidationProblem(new Dictionary<string,string[]> {["assignedStaffId"]=["Choose an active staff member."]});
            var result=await store.Update(id,input,ct);
            return result.Outcome switch
            {
                UpdateOutcome.Updated=>Results.Ok(result.Enquiry),
                UpdateOutcome.NotFound=>Results.NotFound(),
                _=>Results.Conflict(new {message="This enquiry changed since your last read. Reload it before saving."})
            };
        }).WithName("UpdateEnquiry").Produces<EnquiryRecord>().ProducesValidationProblem().Produces(409);
    }
}
