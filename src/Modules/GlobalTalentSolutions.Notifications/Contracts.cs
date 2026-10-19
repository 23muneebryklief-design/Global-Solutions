using System.Net.Mail;

namespace GlobalTalentSolutions.Notifications;

public sealed class EmailOptions
{
    public bool Enabled {get;set;}
    public string ApiKey {get;set;}="";
    public string FromEmail {get;set;}="";
    public string ReplyTo {get;set;}="";
    public string TeamEmail {get;set;}="23muneebryklief@gmail.com";
    public string? KeyDirectory {get;set;}
    public bool Configured=>!string.IsNullOrWhiteSpace(ApiKey)&&Address(FromEmail)&&Address(ReplyTo)&&Address(TeamEmail);
    public bool Ready=>Enabled&&Configured;
    private static bool Address(string value)=>!value.Contains('\r')&&!value.Contains('\n')&&MailAddress.TryCreate(value,out _);
}
public sealed record OutboxEmail(Guid Id,Guid LockToken,int Attempt,string Recipient,string Subject,string Body,string FromEmail,string ReplyTo,DateTimeOffset FirstAttemptAt);
public sealed record EmailSendResult(bool Accepted,string? ProviderMessageId,string Code,bool Retryable,TimeSpan? RetryAfter=null,bool Unknown=false);
public interface IEmailSender {Task<EmailSendResult> Send(OutboxEmail email,CancellationToken ct);}
public sealed record EmailSummary(Guid Id,string Kind,string RecipientEmail,string Status,int Attempts,DateTimeOffset AvailableAt,DateTimeOffset CreatedAt,DateTimeOffset? SentAt,string? LastError,Guid? EnquiryId,Guid? InvitationId,Guid? MessageId,string DeliveryStatus="unknown",DateTimeOffset? DeliveryEventAt=null);
public sealed record EmailPage(List<EmailSummary> Items,long TotalCount,int Page,int PageSize);
public sealed record DeliveryAttempt(int AttemptNumber,string Provider,string? ProviderMessageId,string Outcome,string? ErrorCode,DateTimeOffset CreatedAt);
public interface IEmailOutbox
{
    Task<OutboxEmail?> Claim(CancellationToken ct);
    Task<bool> Complete(OutboxEmail email,EmailSendResult result,CancellationToken ct);
    Task<EmailPage> List(string? status,int page,int pageSize,CancellationToken ct);
    Task<List<DeliveryAttempt>?> Attempts(Guid id,CancellationToken ct);
}
public sealed class EmailUnavailableException:Exception;
