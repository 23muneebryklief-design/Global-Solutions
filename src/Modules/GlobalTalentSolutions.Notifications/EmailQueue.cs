using System.Data;
using System.Data.Common;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlobalTalentSolutions.Notifications;

public sealed class EmailQueue(IServiceProvider services,IOptions<EmailOptions> options,IDataProtectionProvider protection):IEmailNotifications,IEmailOutbox
{
    private NpgsqlDataSource Database=>services.GetService<NpgsqlDataSource>()??throw new EmailUnavailableException();
    private readonly IDataProtector protector=protection.CreateProtector("GlobalTalentSolutions.EmailOutbox.v1");
    private static NpgsqlCommand Cmd(string sql,NpgsqlConnection connection,params object?[] values)
    {var cmd=new NpgsqlCommand(sql,connection);foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);return cmd;}
    private async Task Enqueue(DbConnection connection,DbTransaction tx,string key,string kind,string recipient,string subject,string body,Guid? enquiryId,Guid? invitationId,Guid? messageId,CancellationToken ct,string? replyTo=null)
    {
        // Encrypt all payloads, including invitation bearer tokens, before storage.
        var encrypted=protector.Protect(body);
        await using var cmd=Cmd("insert into gts_notifications.email_outbox(idempotency_key,kind,recipient_email,subject,body,enquiry_id,invitation_id,message_id,reply_to) values($1,$2,$3,$4,$5,$6,$7,$8,$9) on conflict(idempotency_key) do nothing",(NpgsqlConnection)connection,key,kind,recipient.Trim().ToLowerInvariant(),subject,"dp:v1:"+encrypted,enquiryId,invitationId,messageId,replyTo is null?null:"dp:v1:"+protector.Protect(replyTo));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task EnquiryCreated(DbConnection c,DbTransaction t,Guid id,string reference,string email,CancellationToken ct)
    {
        var replyTo=await ReplyRoute(c,t,id,email,ct);
        await Enqueue(c,t,$"enquiry/{id}/confirmation","enquiry_confirmation",email,$"We received your enquiry — {reference}",$"Thank you for contacting Global Talent Solutions. Your enquiry reference is {reference}. Our team will contact you shortly.",id,null,null,ct,replyTo);
        await Enqueue(c,t,$"enquiry/{id}/team","team_alert",options.Value.TeamEmail,$"New enquiry — {reference}",$"A new enquiry ({reference}) is waiting in the Global Talent Solutions admin portal. Sign in to view it.",id,null,null,ct);
    }
    public Task InvitationCreated(DbConnection c,DbTransaction t,Guid id,string email,string url,CancellationToken ct)=>Enqueue(c,t,$"invitation/{id}","invitation",email,"Your Global Talent Solutions invitation",$"You have been invited to Global Talent Solutions. Open this link to set up your account:\n\n{url}\n\nThis invitation expires in three days. If you did not expect it, you can ignore this email.",null,id,null,ct);
    public async Task CancelInvitation(DbConnection c,DbTransaction t,Guid id,CancellationToken ct)
    {
        await using var cmd=Cmd("update gts_notifications.email_outbox set status='cancelled',locked_until=null,lock_token=null,last_error='invitation_replaced' where invitation_id=$1 and status in ('pending','failed')",(NpgsqlConnection)c,id);await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task StaffReplied(DbConnection c,DbTransaction t,Guid messageId,Guid enquiryId,string reference,string email,string body,CancellationToken ct)
    {
        var route=await ReplyRoute(c,t,enquiryId,email,ct);
        await Enqueue(c,t,$"message/{messageId}","conversation_reply",email,$"Global Talent Solutions — {reference}",body,enquiryId,null,messageId,ct,route);
    }
    private async Task<string?> ReplyRoute(DbConnection c,DbTransaction t,Guid enquiryId,string email,CancellationToken ct)
    {
        var incoming=services.GetService<IOptions<IncomingEmailOptions>>()?.Value;
        return incoming is {Enabled:true,DomainValid:true}?await services.GetRequiredService<IIncomingConversations>().CreateRoute(c,t,enquiryId,email,incoming.Domain,ct):null;
    }
    public Task<OutboxEmail?> Claim(CancellationToken ct)=>Claim(null,ct);
    // Tests restrict claims to a fixture UUID, never consuming a real queued email.
    internal async Task<OutboxEmail?> Claim(Guid? onlyId,CancellationToken ct)
    {
        var config=options.Value;if(!config.Ready)return null;
        await using var connection=await Database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        // Stop automatic retries before the provider's 24-hour deduplication window expires.
        await using(var expire=Cmd("update gts_notifications.email_outbox set status='failed',locked_until=null,lock_token=null,available_at='infinity',last_error='retry_limit_or_window_exhausted' where (status in ('pending','failed') or (status='processing' and locked_until<now())) and (attempts>=8 or first_attempt_at<now()-interval '20 hours') and available_at<>'infinity' and ($1::uuid is null or id=$1)",connection,onlyId))await expire.ExecuteNonQueryAsync(ct);
        Guid id=Guid.Empty;Guid? invitationId=null;
        await using(var select=Cmd("select id,invitation_id from gts_notifications.email_outbox where ((status in ('pending','failed') and available_at<=now()) or (status='processing' and locked_until<now())) and attempts<8 and (first_attempt_at is null or first_attempt_at>now()-interval '20 hours') and ($1::uuid is null or id=$1) order by available_at,created_at,id for update skip locked limit 1",connection,onlyId))
        await using(var rows=await select.ExecuteReaderAsync(ct))
        {if(await rows.ReadAsync(ct)){id=rows.GetGuid(0);invitationId=rows.IsDBNull(1)?null:rows.GetGuid(1);}}
        if(id==Guid.Empty){await tx.CommitAsync(ct);return null;}
        if(invitationId is {} invitation&&!await services.GetRequiredService<IInvitationEmailValidity>().CanSend(connection,tx,invitation,ct))
        {
            await using var cancel=Cmd("update gts_notifications.email_outbox set status='cancelled',locked_until=null,lock_token=null,last_error='invitation_unavailable' where id=$1",connection,id);await cancel.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return null;
        }
        OutboxEmail result;
        await using(var lost=Cmd("update gts_notifications.delivery_attempts a set error_code='worker_lease_expired' from gts_notifications.email_outbox e where e.id=$1 and e.status='processing' and a.outbox_id=e.id and a.attempt_number=e.attempts and a.outcome='unknown'",connection,id))await lost.ExecuteNonQueryAsync(ct);
        await using(var claim=Cmd("update gts_notifications.email_outbox set status='processing',attempts=attempts+1,locked_until=now()+interval '2 minutes',lock_token=gen_random_uuid(),first_attempt_at=coalesce(first_attempt_at,now()),from_email=coalesce(from_email,$2),reply_to=coalesce(reply_to,$3) where id=$1 returning id,lock_token,attempts,recipient_email,subject,body,from_email,reply_to,first_attempt_at",connection,id,config.FromEmail,config.ReplyTo))
        await using(var rows=await claim.ExecuteReaderAsync(ct))
        {
            await rows.ReadAsync(ct);var body=rows.GetString(5);
            // A missing key leaves the row untouched, rather than sending ciphertext.
            var clear=body.StartsWith("dp:v1:",StringComparison.Ordinal)?protector.Unprotect(body[6..]):body;
            var replyTo=rows.GetString(7);if(replyTo.StartsWith("dp:v1:",StringComparison.Ordinal))replyTo=protector.Unprotect(replyTo[6..]);
            result=new(rows.GetGuid(0),rows.GetGuid(1),rows.GetInt32(2),rows.GetString(3),rows.GetString(4),clear,rows.GetString(6),replyTo,rows.GetFieldValue<DateTimeOffset>(8));
        }
        await using(var attempt=Cmd("insert into gts_notifications.delivery_attempts(outbox_id,attempt_number,provider,outcome,error_code) values($1,$2,'resend','unknown','attempt_in_progress')",connection,id,result.Attempt))await attempt.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);return result;
    }
    public async Task<bool> Complete(OutboxEmail email,EmailSendResult result,CancellationToken ct)
    {
        var delay=result.RetryAfter??TimeSpan.FromSeconds(Math.Min(3600,30*Math.Pow(2,email.Attempt-1)));
        var retry=!result.Accepted&&result.Retryable&&email.Attempt<8&&DateTimeOffset.UtcNow+delay<email.FirstAttemptAt.AddHours(20);
        await using var connection=await Database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var cmd=Cmd("update gts_notifications.email_outbox set status=$3,locked_until=null,lock_token=null,sent_at=case when $4 then now() else null end,last_error=case when $4 then null else $5 end,available_at=case when $6 then now()+$7::interval else 'infinity'::timestamptz end where id=$1 and lock_token=$2 and status='processing' returning id",connection,email.Id,email.LockToken,result.Accepted?"sent":"failed",result.Accepted,result.Code,retry,delay))
            if(await cmd.ExecuteScalarAsync(ct)is null)return false;
        await using(var cmd=Cmd("update gts_notifications.delivery_attempts set outcome=$3,provider_message_id=$4,error_code=$5 where outbox_id=$1 and attempt_number=$2",connection,email.Id,email.Attempt,result.Accepted?"accepted":result.Unknown?"unknown":"failed",result.ProviderMessageId,result.Accepted?null:result.Code))await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);return true;
    }
    public async Task<EmailPage> List(string? status,int page,int pageSize,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await using var count=Cmd("select count(*) from gts_notifications.email_outbox where ($1::text is null or status=$1)",connection,status);var total=(long)(await count.ExecuteScalarAsync(ct))!;var items=new List<EmailSummary>();
        await using(var cmd=Cmd("select id,kind,recipient_email,status,attempts,available_at,created_at,sent_at,last_error,enquiry_id,invitation_id,message_id,delivery_status,delivery_event_at from gts_notifications.email_outbox where ($1::text is null or status=$1) order by created_at desc,id desc limit $2 offset $3",connection,status,pageSize,(long)(page-1)*pageSize))
        await using(var r=await cmd.ExecuteReaderAsync(ct))while(await r.ReadAsync(ct))items.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetFieldValue<DateTimeOffset>(5),r.GetFieldValue<DateTimeOffset>(6),r.IsDBNull(7)?null:r.GetFieldValue<DateTimeOffset>(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetGuid(9),r.IsDBNull(10)?null:r.GetGuid(10),r.IsDBNull(11)?null:r.GetGuid(11),r.GetString(12),r.IsDBNull(13)?null:r.GetFieldValue<DateTimeOffset>(13)));
        await tx.CommitAsync(ct);return new(items,total,page,pageSize);
    }
    public async Task<List<DeliveryAttempt>?> Attempts(Guid id,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);await using(var exists=Cmd("select exists(select 1 from gts_notifications.email_outbox where id=$1)",connection,id))if(!(bool)(await exists.ExecuteScalarAsync(ct))!)return null;
        var items=new List<DeliveryAttempt>();await using var cmd=Cmd("select attempt_number,provider,provider_message_id,outcome,error_code,created_at from gts_notifications.delivery_attempts where outbox_id=$1 order by attempt_number",connection,id);
        await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add(new(r.GetInt32(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetFieldValue<DateTimeOffset>(5)));return items;
    }
}
