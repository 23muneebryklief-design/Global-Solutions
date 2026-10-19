using System.Data.Common;
using System.Security.Claims;

namespace GlobalTalentSolutions.SharedKernel;

public sealed record EnquiryConversationSource(Guid Id, string Message, DateTimeOffset CreatedAt);
public interface IEnquiryThreadInitializer
{
    Task Initialize(DbConnection connection, DbTransaction transaction, EnquiryConversationSource source, CancellationToken ct);
}
public interface IEnquiryConversationAccess
{
    Task<EnquiryConversationSource?> Source(Guid enquiryId, CancellationToken ct);
    Task<bool> CanAccess(Guid enquiryId, ClaimsPrincipal user, CancellationToken ct);
}
