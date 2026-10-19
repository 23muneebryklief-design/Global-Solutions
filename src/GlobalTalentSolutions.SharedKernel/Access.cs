using System.Security.Claims;

namespace GlobalTalentSolutions.SharedKernel;

public static class AccessPolicies
{
    public const string AppUser = "AppUser";
    public const string Staff = "Staff";
    public const string Admin = "Admin";
    public static Guid AuthId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static Guid AppId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("app_user_id")!);
}
public interface ICompanyAccess
{
    Task<bool> CanAccess(Guid companyId, ClaimsPrincipal user, bool ownerRequired, CancellationToken ct);
}
public sealed record StaffMember(Guid Id, string DisplayName, string Role);
public interface IStaffDirectory
{
    Task<bool> IsActiveStaff(Guid userId, CancellationToken ct);
    Task<List<StaffMember>> ActiveStaff(CancellationToken ct);
}
