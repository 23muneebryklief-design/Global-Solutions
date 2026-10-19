using System.Data;
using System.Security.Claims;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GlobalTalentSolutions.Conversations;

public sealed class ConversationStore(IServiceProvider services,IEnquiryConversationAccess enquiries,IStaffDirectory staff,IEnquiryThreadInitializer initializer) : IConversationStore
{
    private NpgsqlDataSource Database=>services.GetService<NpgsqlDataSource>()??throw new ConversationUnavailableException();
    private static NpgsqlCommand Command(string sql,NpgsqlConnection connection,params object?[] values)
    {
        var cmd=new NpgsqlCommand(sql,connection);
        foreach(var value in values)cmd.Parameters.AddWithValue(value??DBNull.Value);
        return cmd;
    }
    private const string MessageColumns="id,thread_id,sender_kind,sender_user_id,channel,body,created_at";
    private static ConversationMessage Message(NpgsqlDataReader row)=>new(row.GetGuid(0),row.GetGuid(1),row.GetString(2),row.IsDBNull(3)?null:row.GetGuid(3),row.GetString(4),row.GetString(5),row.GetFieldValue<DateTimeOffset>(6));
    private static InternalNote Note(NpgsqlDataReader row)=>new(row.GetGuid(0),row.GetGuid(1),row.GetGuid(2),row.GetString(3),row.GetFieldValue<DateTimeOffset>(4));
    private static bool IsStaff(ClaimsPrincipal user)=>user.IsInRole("staff")||user.IsInRole("admin");
    public async Task<ThreadRecord?> EnsureForEnquiry(Guid enquiryId,CancellationToken ct)
    {
        var source=await enquiries.Source(enquiryId,ct);
        if(source is null)return null;
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        await initializer.Initialize(connection,tx,source,ct);
        await using var cmd=Command("select id,enquiry_id,created_at,updated_at from gts_conversations.threads where enquiry_id=$1",connection,enquiryId);
        ThreadRecord? result;
        await using(var rows=await cmd.ExecuteReaderAsync(ct))
            result=await rows.ReadAsync(ct)?new(rows.GetGuid(0),rows.GetGuid(1),rows.GetFieldValue<DateTimeOffset>(2),rows.GetFieldValue<DateTimeOffset>(3)):null;
        await tx.CommitAsync(ct);
        return result;
    }
    public async Task<bool> CanAccess(Guid threadId,ClaimsPrincipal user,CancellationToken ct)
    {
        if(!Guid.TryParse(user.FindFirstValue("app_user_id"),out var userId))return false;
        await using var connection=await Database.OpenConnectionAsync(ct);
        Guid enquiryId;
        await using(var cmd=Command("select enquiry_id from gts_conversations.threads where id=$1",connection,threadId))
        {
            var found=await cmd.ExecuteScalarAsync(ct);
            if(found is not Guid id)return false;
            enquiryId=id;
        }
        if(IsStaff(user))return await staff.IsActiveStaff(userId,ct);
        await using(var revoked=Command("select exists(select 1 from gts_conversations.participants where thread_id=$1 and user_id=$2 and revoked_at is not null)",connection,threadId,userId))
            if((bool)(await revoked.ExecuteScalarAsync(ct))!)return false;
        return await enquiries.CanAccess(enquiryId,user,ct);
    }
    public async Task<MessagePage> Messages(Guid threadId,int page,int pageSize,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await using var count=Command("select count(*) from gts_conversations.messages where thread_id=$1",connection,threadId);
        var total=(long)(await count.ExecuteScalarAsync(ct))!;
        var items=new List<ConversationMessage>();
        await using(var cmd=Command("select "+MessageColumns+" from gts_conversations.messages where thread_id=$1 order by created_at,id limit $2 offset $3",connection,threadId,pageSize,(long)(page-1)*pageSize))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))
            while(await rows.ReadAsync(ct))items.Add(Message(rows));
        await tx.CommitAsync(ct);
        return new(items,total,page,pageSize);
    }
    public async Task<MessageResult?> Send(Guid threadId,MessageInput input,ClaimsPrincipal user,CancellationToken ct)
    {
        if(!await CanAccess(threadId,user,ct))return null;
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        var userId=user.AppId();var body=input.Body!.Trim();
        await using(var gate=Command("select pg_advisory_xact_lock(hashtextextended($1,1))",connection,input.ClientMessageKey!.Value.ToString()))
            await gate.ExecuteNonQueryAsync(ct);
        await using(var find=Command("select "+MessageColumns+" from gts_conversations.messages where client_message_key=$1",connection,input.ClientMessageKey.Value))
        await using(var rows=await find.ExecuteReaderAsync(ct))
        {
            if(await rows.ReadAsync(ct))
            {
                var item=Message(rows);
                if(item.ThreadId!=threadId||item.SenderUserId!=userId||item.Body!=body)throw new ConversationConflictException();
                return new(item,false);
            }
        }
        ConversationMessage message;
        await using(var cmd=Command("insert into gts_conversations.messages(thread_id,sender_kind,sender_user_id,channel,body,client_message_key) values($1,$2,$3,'portal',$4,$5) returning "+MessageColumns,connection,threadId,IsStaff(user)?"staff":"client",userId,body,input.ClientMessageKey.Value))
        await using(var rows=await cmd.ExecuteReaderAsync(ct)){await rows.ReadAsync(ct);message=Message(rows);}
        await using(var touch=Command("update gts_conversations.threads set updated_at=now() where id=$1",connection,threadId))await touch.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new(message,true);
    }
    public async Task<NotePage> Notes(Guid threadId,int page,int pageSize,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await using var count=Command("select count(*) from gts_conversations.internal_notes where thread_id=$1",connection,threadId);
        var total=(long)(await count.ExecuteScalarAsync(ct))!;
        var items=new List<InternalNote>();
        await using(var cmd=Command("select id,thread_id,author_staff_id,body,created_at from gts_conversations.internal_notes where thread_id=$1 order by created_at,id limit $2 offset $3",connection,threadId,pageSize,(long)(page-1)*pageSize))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))while(await rows.ReadAsync(ct))items.Add(Note(rows));
        await tx.CommitAsync(ct);
        return new(items,total,page,pageSize);
    }
    public async Task<InternalNote?> AddNote(Guid threadId,NoteInput input,ClaimsPrincipal user,CancellationToken ct)
    {
        if(!IsStaff(user)||!await CanAccess(threadId,user,ct))return null;
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("insert into gts_conversations.internal_notes(thread_id,author_staff_id,body) values($1,$2,$3) returning id,thread_id,author_staff_id,body,created_at",connection,threadId,user.AppId(),input.Body!.Trim());
        await using var rows=await cmd.ExecuteReaderAsync(ct);await rows.ReadAsync(ct);
        return Note(rows);
    }
}
