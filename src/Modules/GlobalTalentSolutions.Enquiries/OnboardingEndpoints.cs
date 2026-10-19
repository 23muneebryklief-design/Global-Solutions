using System.Security.Claims;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;

namespace GlobalTalentSolutions.Enquiries;

public static class OnboardingEndpoints
{
    public static void MapOnboardingModule(this IEndpointRouteBuilder app)
    {
        var routes=app.MapGroup("/api/admin/enquiries").RequireAuthorization(AccessPolicies.Staff).WithTags("Enquiry onboarding");
        routes.MapPost("/{id:guid}/onboarding",async(Guid id,ApproveEnquiry input,ClaimsPrincipal user,IEnquiryOnboarding store,CancellationToken ct)=>
        {
            if(input.CompanyId==Guid.Empty||input.ReplaceInvitationId==Guid.Empty||input.MembershipRole is not (null or "owner" or "member"))
                return Results.BadRequest(new {message="Provide valid company/invitation UUIDs and an owner/member role, or no company/role for a candidate."});
            var result=await store.Approve(id,input,user.AppId(),ct);
            return result is null?Results.NotFound():Results.Json(result,statusCode:201);
        });
        routes.MapGet("/{id:guid}/onboarding",async(Guid id,IEnquiryOnboarding store,CancellationToken ct)=>
            await store.Get(id,ct)is {} result?Results.Ok(result):Results.NotFound());
    }
}
