using System.Security.Claims;

namespace GlobalTalentSolutions.Conversations;

public sealed record ThreadRecord(Guid Id, Guid EnquiryId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ConversationMessage(Guid Id, Guid ThreadId, string SenderKind, Guid? SenderUserId, string Channel, string Body, DateTimeOffset CreatedAt);
public sealed record InternalNote(Guid Id, Guid ThreadId, Guid AuthorStaffId, string Body, DateTimeOffset CreatedAt);
public sealed record MessageInput(string? Body, Guid? ClientMessageKey);
public sealed record NoteInput(string? Body);
public sealed record MessagePage(List<ConversationMessage> Items, long TotalCount, int Page, int PageSize);
public sealed record NotePage(List<InternalNote> Items, long TotalCount, int Page, int PageSize);
public sealed record MessageResult(ConversationMessage Message, bool Created);
public sealed class ConversationConflictException : Exception;
public sealed class ConversationUnavailableException : Exception;
public interface IConversationStore
{
    Task<ThreadRecord?> EnsureForEnquiry(Guid enquiryId, CancellationToken ct);
    Task<bool> CanAccess(Guid threadId, ClaimsPrincipal user, CancellationToken ct);
    Task<MessagePage> Messages(Guid threadId, int page, int pageSize, CancellationToken ct);
    Task<MessageResult?> Send(Guid threadId, MessageInput input, ClaimsPrincipal user, CancellationToken ct);
    Task<NotePage> Notes(Guid threadId, int page, int pageSize, CancellationToken ct);
    Task<InternalNote?> AddNote(Guid threadId, NoteInput input, ClaimsPrincipal user, CancellationToken ct);
}
