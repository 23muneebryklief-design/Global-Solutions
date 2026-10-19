using System.Data;
using System.Text.Json;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlobalTalentSolutions.Notifications;

public sealed partial class IncomingEmailStore(IServiceProvider services,IIncomingConversations conversations,IOptions<IncomingEmailOptions> options,IDataProtectionProvider protection):IIncomingEmailStore
{
    private NpgsqlDataSource Database=>services.GetService<NpgsqlDataSource>()??throw new EmailUnavailableException();
    private readonly IDataProtector protector=protection.CreateProtector("GlobalTalentSolutions.IncomingEmail.v1");
    private static NpgsqlCommand Cmd(string sql,NpgsqlConnection c,params object?[] values)
    {var cmd=new NpgsqlCommand(sql,c);foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);return cmd;}
    private string Protect(string text)=>JsonSerializer.Serialize(new {encrypted=protector.Protect(text)});
    private string Unprotect(string json){using var doc=JsonDocument.Parse(json);return protector.Unprotect(doc.RootElement.GetProperty("encrypted").GetString()!);}
    public async Task<bool> Receive(VerifiedEmailEvent input,CancellationToken ct)
    {
        await using var c=await Database.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using(var gate=Cmd("select pg_advisory_xact_lock(hashtextextended($1,3))",c,input.Id))await gate.ExecuteNonQueryAsync(ct);
        await using(var find=Cmd("select payload_hash from gts_notifications.webhook_events where provider='resend' and provider_event_id=$1",c,input.Id))
        {
            var previous=await find.ExecuteScalarAsync(ct);
            if(previous is not null){if(!Equals(previous,input.Hash))throw new EmailEventConflictException();return false;}
        }
        var supported=IncomingEmailParsing.Supported(input.Type);
        await using var cmd=Cmd("insert into gts_notifications.webhook_events(provider,provider_event_id,event_type,payload,payload_hash,provider_email_id,event_at,status,processed_at,last_error) values('resend',$1,$2,$3::jsonb,$4,$5,$6,$7,case when $8 then null else now() end,case when $8 then null else 'unsupported_event' end)",c,input.Id,input.Type,Protect(input.Payload),input.Hash,input.EmailId,input.At,supported?"pending":"processed",supported);
        await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return true;
    }
    public Task<ClaimedEmailEvent?> Claim(CancellationToken ct)=>Claim(null,ct);
    // Hosted checks only lease their fixture UUID, never real events.
    internal async Task<ClaimedEmailEvent?> Claim(Guid? onlyId,CancellationToken ct)
    {
        await using var c=await Database.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using(var exhausted=Cmd("update gts_notifications.webhook_events set status='failed',locked_until=null,lock_token=null,available_at='infinity',last_error='event_retry_limit_exhausted' where attempts>=8 and (status in ('pending','failed') or(status='processing' and locked_until<now())) and available_at<>'infinity' and ($1::uuid is null or id=$1)",c,onlyId))await exhausted.ExecuteNonQueryAsync(ct);
        Guid? id=null;
        await using(var find=Cmd("select id from gts_notifications.webhook_events where ((status in ('pending','failed') and available_at<=now())or(status='processing' and locked_until<now()))and attempts<8 and ($1::uuid is null or id=$1) order by available_at,received_at,id for update skip locked limit 1",c,onlyId))
            if(await find.ExecuteScalarAsync(ct)is Guid found)id=found;
        if(id is null){await tx.CommitAsync(ct);return null;}
        ClaimedEmailEvent item;
        await using(var claim=Cmd("update gts_notifications.webhook_events set status='processing',attempts=attempts+1,lock_token=gen_random_uuid(),locked_until=now()+interval '2 minutes' where id=$1 returning id,lock_token,attempts,event_type,provider_email_id,coalesce(event_at,received_at),content::text,payload::text",c,id))
        await using(var rows=await claim.ExecuteReaderAsync(ct))
        {
            await rows.ReadAsync(ct);Guid? outboxId=null;string[]? recipients=null;
            if(rows.GetString(3)!="email.received")
            {
                using var payload=JsonDocument.Parse(Unprotect(rows.GetString(7)));
                if(payload.RootElement.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Object)
                {
                    if(data.TryGetProperty("tags",out var tags)&&tags.ValueKind==JsonValueKind.Object&&tags.TryGetProperty("gts_outbox_id",out var tag)&&tag.ValueKind==JsonValueKind.String&&Guid.TryParse(tag.GetString(),out var parsed))outboxId=parsed;
                    if(data.TryGetProperty("to",out var to)&&to.ValueKind==JsonValueKind.Array)recipients=to.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>IncomingEmailParsing.Address(v.GetString()!)).Where(v=>v is not null).Cast<string>().ToArray();
                }
            }
            item=new(rows.GetGuid(0),rows.GetGuid(1),rows.GetInt32(2),rows.GetString(3),rows.IsDBNull(4)?null:rows.GetString(4),rows.GetFieldValue<DateTimeOffset>(5),rows.IsDBNull(6)?null:JsonSerializer.Deserialize<IncomingEmail>(Unprotect(rows.GetString(6))),outboxId,recipients);
        }
        await tx.CommitAsync(ct);return item;
    }
    public async Task<bool> Finish(ClaimedEmailEvent item,IncomingEmail? content,CancellationToken ct)
    {
        await using var c=await Database.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using(var gate=Cmd("select id from gts_notifications.webhook_events where id=$1 and status='processing' and lock_token=$2 for update",c,item.Id,item.LockToken))if(await gate.ExecuteScalarAsync(ct)is null)return false;
        IncomingImport? imported=null;
        if(item.Type=="email.received")
        {
            if(content is null||content.ProviderId!=item.EmailId)throw new ReceivedEmailException("received_id_mismatch",false);
            imported=await conversations.Import(c,tx,content,options.Value.Domain,ct);
        }
        else if(IncomingEmailParsing.Supported(item.Type))
        {
            var status=item.Type switch {"email.delivered"=>"delivered","email.bounced"=>"bounced","email.complained"=>"complained","email.failed" or "email.suppressed"=>"failed",_=>"delayed"};
            Guid? target=null;
            await using(var find=Cmd("select distinct outbox_id from gts_notifications.delivery_attempts where provider='resend' and provider_message_id=$1",c,item.EmailId))
            await using(var rows=await find.ExecuteReaderAsync(ct))
            {if(await rows.ReadAsync(ct))target=rows.GetGuid(0);if(await rows.ReadAsync(ct))throw new ReceivedEmailException("ambiguous_outbound_email",false);}
            if(target is not null&&item.OutboxId is {} tagged&&tagged!=target)throw new ReceivedEmailException("outbound_tag_mismatch",false);
            if(target is null&&item.OutboxId is {} correlated&&item.DeliveryRecipients is {Length:>0})
            {
                await using var correlation=Cmd("select id from gts_notifications.email_outbox where id=$1 and attempts>0 and recipient_email=any($2)",c,correlated,item.DeliveryRecipients);
                if(await correlation.ExecuteScalarAsync(ct)is Guid found)target=found;
            }
            if(target is null)throw new ReceivedEmailException("outbound_email_not_recorded",true);
            const string rank="case delivery_status when 'complained' then 5 when 'bounced' then 4 when 'failed' then 3 when 'delivered' then 2 when 'delayed' then 1 else 0 end";
            var severity=status switch {"complained"=>5,"bounced"=>4,"failed"=>3,"delivered"=>2,_=>1};
            await using var update=Cmd($"update gts_notifications.email_outbox set delivery_status=$2,delivery_event_at=$3 where id=$1 and (({rank})<$4 or (({rank})=$4 and (delivery_event_at is null or delivery_event_at<$3)))",c,target,status,item.At,severity);await update.ExecuteNonQueryAsync(ct);
        }
        var reason=imported?.ReviewReason;
        await using(var finish=Cmd("update gts_notifications.webhook_events set status=$3,processed_at=case when $4 then now() else null end,locked_until=null,lock_token=null,last_error=$5,content=$6::jsonb,thread_id=$7,message_id=$8 where id=$1 and lock_token=$2",c,item.Id,item.LockToken,reason is null?"processed":"unmatched",reason is null,reason,content is null?null:Protect(JsonSerializer.Serialize(content)),imported?.ThreadId,imported?.MessageId))await finish.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);return true;
    }
    public async Task Fail(ClaimedEmailEvent item,string code,bool retryable,CancellationToken ct)
    {
        await using var c=await Database.OpenConnectionAsync(ct);
        await using var cmd=Cmd("update gts_notifications.webhook_events set status='failed',locked_until=null,lock_token=null,last_error=$3,available_at=case when $4 then now()+$5::interval else 'infinity'::timestamptz end where id=$1 and lock_token=$2 and status='processing'",c,item.Id,item.LockToken,code,retryable&&item.Attempts<8,TimeSpan.FromSeconds(Math.Min(3600,30*Math.Pow(2,item.Attempts-1))));await cmd.ExecuteNonQueryAsync(ct);
    }
    private static EmailEventSummary Summary(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetInt32(3),r.IsDBNull(4)?null:r.GetString(4),r.GetFieldValue<DateTimeOffset>(5),r.GetFieldValue<DateTimeOffset>(6),r.IsDBNull(7)?null:r.GetGuid(7),r.IsDBNull(8)?null:r.GetGuid(8));
    private const string Columns="id,event_type,status,attempts,last_error,received_at,updated_at,thread_id,message_id";
    public async Task<EmailEventPage> List(string? status,int page,int pageSize,CancellationToken ct)
    {
        await using var c=await Database.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await using var count=Cmd("select count(*) from gts_notifications.webhook_events where ($1::text is null or status=$1)",c,status);var total=(long)(await count.ExecuteScalarAsync(ct))!;var items=new List<EmailEventSummary>();
        await using(var cmd=Cmd("select "+Columns+" from gts_notifications.webhook_events where ($1::text is null or status=$1) order by received_at desc,id desc limit $2 offset $3",c,status,pageSize,(long)(page-1)*pageSize))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))while(await rows.ReadAsync(ct))items.Add(Summary(rows));
        await tx.CommitAsync(ct);return new(items,total,page,pageSize);
    }
    public async Task<EmailReviewDetail?> Detail(Guid id,CancellationToken ct)
    {
        await using var c=await Database.OpenConnectionAsync(ct);await using var cmd=Cmd("select "+Columns+",content::text from gts_notifications.webhook_events where id=$1",c,id);await using var rows=await cmd.ExecuteReaderAsync(ct);
        if(!await rows.ReadAsync(ct))return null;var item=Summary(rows);var content=rows.IsDBNull(9)?null:JsonSerializer.Deserialize<IncomingEmail>(Unprotect(rows.GetString(9)));
        return new(item,content?.Sender,content?.Subject,content?.Text is {Length:>20000} text?text[..20000]:content?.Text,content?.HasHtml??false,content?.AttachmentCount??0,content?.Authenticated??false,content?.Text?.Length>20000);
    }
    public async Task<bool> Review(Guid id,ReviewEmailInput input,Guid staffId,CancellationToken ct)
    {
        if(!await services.GetRequiredService<IStaffDirectory>().IsActiveStaff(staffId,ct))throw new EmailEventForbiddenException();
        await using var c=await Database.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        string status,type;IncomingEmail? content;DateTimeOffset updated;
        await using(var cmd=Cmd("select status,event_type,content::text,updated_at from gts_notifications.webhook_events where id=$1 for update",c,id))
        await using(var r=await cmd.ExecuteReaderAsync(ct))
        {if(!await r.ReadAsync(ct))return false;status=r.GetString(0);type=r.GetString(1);content=r.IsDBNull(2)?null:JsonSerializer.Deserialize<IncomingEmail>(Unprotect(r.GetString(2)));updated=r.GetFieldValue<DateTimeOffset>(3);}
        if(updated!=input.ExpectedUpdatedAt||status is not("unmatched" or "failed"))throw new EmailEventConflictException();
        IncomingImport? result=null;
        if(input.Action=="attach")
        {
            if(type!="email.received"||content is null||input.ThreadId is null)throw new EmailEventConflictException();
            result=await conversations.AttachReviewed(c,tx,input.ThreadId.Value,content,input.EditedBody,ct);
            if(result.ReviewReason is not null)throw new EmailEventConflictException();
        }
        else if(input.Action is not("ignore" or "retry"))throw new EmailEventConflictException();
        var retry=input.Action=="retry";
        await using var save=Cmd("update gts_notifications.webhook_events set status=$2,processed_at=case when $3 then null else now() end,available_at=now(),attempts=case when $3 then 0 else attempts end,reviewed_by=$4,review_reason=$5,last_error=$6,thread_id=$7,message_id=$8 where id=$1",c,id,retry?"pending":"processed",retry,staffId,input.Reason!.Trim(),retry?null:input.Action=="ignore"?"staff_ignored":null,result?.ThreadId,result?.MessageId);
        await save.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return true;
    }
}
