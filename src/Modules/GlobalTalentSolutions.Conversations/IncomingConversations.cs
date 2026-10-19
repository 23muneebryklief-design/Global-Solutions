using System.Data.Common;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using GlobalTalentSolutions.SharedKernel;
using Npgsql;

namespace GlobalTalentSolutions.Conversations;

public sealed class IncomingConversations(IEnquiryMailDetails enquiries):IIncomingConversations
{
    private static NpgsqlCommand Cmd(string sql,DbConnection c,params object?[] values)
    {var cmd=new NpgsqlCommand(sql,(NpgsqlConnection)c);foreach(var v in values)cmd.Parameters.AddWithValue(v??DBNull.Value);return cmd;}
    private static string Hash(string token)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public async Task<string?> CreateRoute(DbConnection c,DbTransaction tx,Guid enquiryId,string recipient,string domain,CancellationToken ct)
    {
        // 240 random bits; the 62-character local part stays within SMTP's limit.
        var token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(30));
        await using var cmd=Cmd("insert into gts_conversations.reply_routes(thread_id,token_hash,allowed_sender_email,expires_at) select id,$2,$3,now()+interval '90 days' from gts_conversations.threads where enquiry_id=$1 returning id",c,enquiryId,Hash(token),recipient);
        return await cmd.ExecuteScalarAsync(ct)is Guid?"r-"+token+"@"+domain.ToLowerInvariant():null;
    }
    public async Task<IncomingImport> Import(DbConnection c,DbTransaction tx,IncomingEmail email,string domain,CancellationToken ct)
    {
        if(!email.Authenticated)return new(null,null,"sender_authentication_unverified");
        if(email.AttachmentCount>0)return new(null,null,"attachments_require_review");
        if(string.IsNullOrWhiteSpace(email.Text))return new(null,null,"plain_text_missing");
        if(email.Text.Trim().Length>20000||email.Text.Contains('\0'))return new(null,null,"body_requires_review");
        var hashes=new HashSet<string>();
        foreach(var recipient in email.Recipients)
        {
            if(!MailAddress.TryCreate(recipient,out var address)||!address.Host.Equals(domain,StringComparison.OrdinalIgnoreCase))continue;
            var local=address.User.ToLowerInvariant();
            if(local.Length==62&&local.StartsWith("r-",StringComparison.Ordinal)&&local[2..].All(Uri.IsHexDigit))hashes.Add(Hash(local[2..]));
        }
        if(hashes.Count!=1)return new(null,null,hashes.Count==0?"reply_route_missing":"ambiguous_reply_routes");
        Guid threadId,enquiryId;string allowed;
        await using(var cmd=Cmd("select r.thread_id,t.enquiry_id,r.allowed_sender_email from gts_conversations.reply_routes r join gts_conversations.threads t on t.id=r.thread_id where r.token_hash=$1 and r.revoked_at is null and (r.expires_at is null or r.expires_at>now()) for share of r",c,hashes.Single()))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))
        {if(!await rows.ReadAsync(ct))return new(null,null,"reply_route_unavailable");threadId=rows.GetGuid(0);enquiryId=rows.GetGuid(1);allowed=rows.GetString(2);}
        if(!allowed.Equals(email.Sender,StringComparison.OrdinalIgnoreCase))return new(null,null,"unexpected_sender");
        var contact=await enquiries.MailDetails(c,tx,enquiryId,ct);
        if(contact is null||!contact.Email.Equals(email.Sender,StringComparison.OrdinalIgnoreCase))return new(null,null,"sender_no_longer_authorized");
        if(contact.AccountId is {} account)
        {
            await using var revoked=Cmd("select exists(select 1 from gts_conversations.participants where thread_id=$1 and user_id=$2 and revoked_at is not null)",c,threadId,account);
            if((bool)(await revoked.ExecuteScalarAsync(ct))!)return new(null,null,"participant_revoked");
        }
        return await Save(c,threadId,email,email.Text.Trim(),ct);
    }
    public async Task<IncomingImport> AttachReviewed(DbConnection c,DbTransaction tx,Guid threadId,IncomingEmail email,string? editedBody,CancellationToken ct)
    {
        var text=(editedBody??email.Text)?.Trim();
        if(string.IsNullOrWhiteSpace(text)||text.Length>20000||text.Contains('\0'))return new(null,null,"body_requires_review");
        await using var exists=Cmd("select id from gts_conversations.threads where id=$1",c,threadId);
        if(await exists.ExecuteScalarAsync(ct)is null)return new(null,null,"thread_missing");
        return await Save(c,threadId,email,text,ct);
    }
    private static async Task<IncomingImport> Save(DbConnection c,Guid threadId,IncomingEmail email,string body,CancellationToken ct)
    {
        // Provider email ID deduplicates even when separate webhook IDs refer to one email.
        await using(var gate=Cmd("select pg_advisory_xact_lock(hashtextextended($1,2))",c,email.ProviderId))await gate.ExecuteNonQueryAsync(ct);
        await using(var existing=Cmd("select id,thread_id from gts_conversations.messages where provider='resend' and provider_message_id=$1",c,email.ProviderId))
        await using(var rows=await existing.ExecuteReaderAsync(ct))
            if(await rows.ReadAsync(ct))return rows.GetGuid(1)==threadId?new(threadId,rows.GetGuid(0),null):new(null,null,"email_already_in_another_thread");
        Guid id;
        await using(var cmd=Cmd("insert into gts_conversations.messages(thread_id,sender_kind,channel,body,provider,provider_message_id) values($1,'visitor','email',$2,'resend',$3) returning id",c,threadId,body,email.ProviderId))id=(Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await using(var touch=Cmd("update gts_conversations.threads set updated_at=now() where id=$1",c,threadId))await touch.ExecuteNonQueryAsync(ct);
        return new(threadId,id,null);
    }
}
