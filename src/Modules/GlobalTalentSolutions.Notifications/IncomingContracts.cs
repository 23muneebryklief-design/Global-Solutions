using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlobalTalentSolutions.SharedKernel;

namespace GlobalTalentSolutions.Notifications;

public sealed class IncomingEmailOptions
{
    public bool Enabled {get;set;}
    public string Domain {get;set;}="";
    public string WebhookSecret {get;set;}="";
    public string PreviousWebhookSecret {get;set;}="";
    public bool DomainValid=>Domain.Length is >0 and <=253&&Domain.Contains('.')&&Domain.Split('.').All(label=>label.Length is >=1 and <=63&&char.IsAsciiLetterOrDigit(label[0])&&char.IsAsciiLetterOrDigit(label[^1])&&label.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'));
}
public static class WebhookSignature
{
    public static bool Configured(string secret)=>Key(secret)is not null;
    private static byte[]? Key(string secret)
    {
        if(!secret.StartsWith("whsec_",StringComparison.Ordinal))return null;
        try{var key=Convert.FromBase64String(secret[6..]);return key.Length>=16?key:null;}catch(FormatException){return null;}
    }
    public static bool Verify(byte[] body,string id,string timestamp,string signatures,IEnumerable<string> secrets,DateTimeOffset now)
    {
        if(id.Length is <1 or >200||!id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-')||timestamp.Length>20||signatures.Length>4096||
           !long.TryParse(timestamp,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var seconds)||seconds<now.ToUnixTimeSeconds()-300||seconds>now.ToUnixTimeSeconds()+300)return false;
        var prefix=Encoding.UTF8.GetBytes(id+"."+timestamp+".");var signed=new byte[prefix.Length+body.Length];prefix.CopyTo(signed,0);body.CopyTo(signed,prefix.Length);
        foreach(var secret in secrets)
        {
            var key=Key(secret);if(key is null)continue;var expected=HMACSHA256.HashData(key,signed);
            foreach(var entry in signatures.Split(' ',StringSplitOptions.RemoveEmptyEntries))
            {
                if(!entry.StartsWith("v1,",StringComparison.Ordinal))continue;
                try{var actual=Convert.FromBase64String(entry[3..]);if(actual.Length==expected.Length&&CryptographicOperations.FixedTimeEquals(actual,expected))return true;}catch(FormatException){}
            }
        }
        return false;
    }
}
public sealed record VerifiedEmailEvent(string Id,string Type,string? EmailId,DateTimeOffset At,string Payload,string Hash);
public sealed record ClaimedEmailEvent(Guid Id,Guid LockToken,int Attempts,string Type,string? EmailId,DateTimeOffset At,IncomingEmail? Content,Guid? OutboxId=null,string[]? DeliveryRecipients=null);
public sealed record EmailEventSummary(Guid Id,string Type,string Status,int Attempts,string? Reason,DateTimeOffset ReceivedAt,DateTimeOffset UpdatedAt,Guid? ThreadId,Guid? MessageId);
public sealed record EmailEventPage(List<EmailEventSummary> Items,long TotalCount,int Page,int PageSize);
public sealed record EmailReviewDetail(EmailEventSummary Event,string? Sender,string? Subject,string? Text,bool HasHtml,int AttachmentCount,bool Authenticated,bool BodyTruncated);
public sealed record ReviewEmailInput(DateTimeOffset? ExpectedUpdatedAt,string? Action,Guid? ThreadId,string? Reason,string? EditedBody);
public interface IReceivedEmailProvider {Task<IncomingEmail> Get(string id,CancellationToken ct);}
public interface IIncomingEmailStore
{
    Task<bool> Receive(VerifiedEmailEvent input,CancellationToken ct);
    Task<ClaimedEmailEvent?> Claim(CancellationToken ct);
    Task<bool> Finish(ClaimedEmailEvent item,IncomingEmail? content,CancellationToken ct);
    Task Fail(ClaimedEmailEvent item,string code,bool retryable,CancellationToken ct);
    Task<EmailEventPage> List(string? status,int page,int pageSize,CancellationToken ct);
    Task<EmailReviewDetail?> Detail(Guid id,CancellationToken ct);
    Task<bool> Review(Guid id,ReviewEmailInput input,Guid staffId,CancellationToken ct);
}
public sealed class EmailEventConflictException:Exception;
public sealed class EmailEventForbiddenException:Exception;
public sealed class ReceivedEmailException(string code,bool retryable):Exception
{public string Code {get;}=code;public bool Retryable {get;}=retryable;}
public static class IncomingEmailParsing
{
    public static string? Address(string value)=>value.Length<=500&&!value.Contains('\0')&&!value.Contains('\r')&&!value.Contains('\n')&&MailAddress.TryCreate(value,out var address)?address.Address.ToLowerInvariant():null;
    public static VerifiedEmailEvent Parse(string id,byte[] body)
    {
        using var json=JsonDocument.Parse(body);var root=json.RootElement;
        var type=root.GetProperty("type").GetString();
        if(type is not {Length:>0 and <=100}||!root.GetProperty("created_at").TryGetDateTimeOffset(out var at))throw new JsonException();
        string? emailId=null;
        if(Supported(type))
        {
            if(!root.GetProperty("data").TryGetProperty("email_id",out var value)||!Guid.TryParse(value.GetString(),out var parsed))throw new JsonException();
            emailId=parsed.ToString();
        }
        return new(id,type,emailId,at.ToUniversalTime(),Encoding.UTF8.GetString(body),Convert.ToHexStringLower(SHA256.HashData(body)));
    }
    public static bool Supported(string type)=>type is "email.received" or "email.delivered" or "email.bounced" or "email.complained" or "email.failed" or "email.suppressed" or "email.delivery_delayed";
}
