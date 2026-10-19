using System.Data.Common;

namespace GlobalTalentSolutions.SharedKernel;

public sealed record IncomingEmail(string ProviderId,string Sender,string[] Recipients,string Subject,string? Text,bool HasHtml,int AttachmentCount,bool Authenticated);
public sealed record IncomingImport(Guid? ThreadId,Guid? MessageId,string? ReviewReason);
public interface IIncomingConversations
{
    Task<string?> CreateRoute(DbConnection connection,DbTransaction transaction,Guid enquiryId,string recipient,string domain,CancellationToken ct);
    Task<IncomingImport> Import(DbConnection connection,DbTransaction transaction,IncomingEmail email,string domain,CancellationToken ct);
    Task<IncomingImport> AttachReviewed(DbConnection connection,DbTransaction transaction,Guid threadId,IncomingEmail email,string? editedBody,CancellationToken ct);
}
