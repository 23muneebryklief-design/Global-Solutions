using System.Data.Common;
using System.Security.Claims;

namespace GlobalTalentSolutions.SharedKernel;

public sealed record OnboardingInvitation(Guid Id,string Token,DateTimeOffset ExpiresAt,string AcceptanceUrl);
public sealed record OnboardingInvitationState(Guid Id,DateTimeOffset ExpiresAt,DateTimeOffset? AcceptedAt,DateTimeOffset? RevokedAt);
public interface IOnboardingInvitations
{
    Task<OnboardingInvitation> Create(DbConnection connection,DbTransaction transaction,string email,string accountType,Guid? companyId,string? role,Guid staffId,CancellationToken ct);
    Task<bool> RevokePending(DbConnection connection,DbTransaction transaction,Guid invitationId,CancellationToken ct);
    Task<OnboardingInvitationState?> State(Guid invitationId,CancellationToken ct);
}
public interface IInvitationAcceptanceLinker
{
    Task Link(DbConnection connection,DbTransaction transaction,Guid invitationId,Guid accountId,Guid? companyId,CancellationToken ct);
}
public sealed class OnboardingConflictException:Exception;
public interface ICompanyMemberships
{
    Task<Guid[]> ActiveCompanyIds(Guid userId,CancellationToken ct);
}
public sealed record PortalEnquiry(Guid Id,string Reference,string EnquiryType,string Service,string Status,string? CompanyName,DateTimeOffset CreatedAt);
public sealed record PortalEnquiryPage(List<PortalEnquiry> Items,long TotalCount,int Page,int PageSize);
public interface IPortalEnquiries
{
    Task<PortalEnquiryPage> List(ClaimsPrincipal user,Guid[] excludedEnquiryIds,int page,int pageSize,CancellationToken ct);
}
