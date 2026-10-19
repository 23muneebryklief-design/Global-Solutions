using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Security.Claims;
using GlobalTalentSolutions.SharedKernel;

namespace GlobalTalentSolutions.Enquiries;

public interface IEnquiryStore
{
    Task<SubmissionResult> Submit(NormalizedEnquiry input, CancellationToken ct);
    Task<EnquiryPage> List(EnquiryQuery query, CancellationToken ct);
    Task<EnquiryRecord?> Get(Guid id, CancellationToken ct);
    Task<UpdateResult> Update(Guid id, UpdateEnquiry input, CancellationToken ct);
}
public sealed class EnquiryStore(IServiceProvider services) : IEnquiryStore, IEnquiryConversationAccess
{
    private NpgsqlDataSource Database => services.GetService<NpgsqlDataSource>() ?? throw new EnquiryUnavailableException();
    private const string Columns = "id,reference,enquiry_type,name,email,phone,company_name,country_code,service,message,status,assigned_staff_id,consent_at,privacy_notice_version,created_at,updated_at";
    private static NpgsqlCommand Command(string sql,NpgsqlConnection connection,params object?[] values)
    {
        var command=new NpgsqlCommand(sql,connection);
        foreach(var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        return command;
    }
    private static string? Text(NpgsqlDataReader row,int index) => row.IsDBNull(index)?null:row.GetString(index);
    private static EnquiryRecord Record(NpgsqlDataReader row)=>new(
        row.GetGuid(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),Text(row,5),
        Text(row,6),Text(row,7),row.GetString(8),row.GetString(9),row.GetString(10),
        row.IsDBNull(11)?null:row.GetGuid(11),row.GetFieldValue<DateTimeOffset>(12),row.GetString(13),
        row.GetFieldValue<DateTimeOffset>(14),row.GetFieldValue<DateTimeOffset>(15));
    public async Task<SubmissionResult> Submit(NormalizedEnquiry input,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        // Serialise retries for this UUID. Replays do not update updated_at.
        await using(var gate=Command("select pg_advisory_xact_lock(hashtextextended($1,0))",connection,input.SubmissionKey.ToString()))
            await gate.ExecuteNonQueryAsync(ct);
        await using(var find=Command($"select {Columns} from gts_enquiries.enquiries where submission_key=$1",connection,input.SubmissionKey))
        await using(var rows=await find.ExecuteReaderAsync(ct))
        {
            if(await rows.ReadAsync(ct))
            {
                var item=Record(rows);
                if(item.EnquiryType!=input.EnquiryType || item.Name!=input.Name || item.Email!=input.Email ||
                   item.Phone!=input.Phone || item.CompanyName!=input.CompanyName || item.CountryCode!=input.CountryCode ||
                   item.Service!=input.Service || item.Message!=input.Message || item.PrivacyNoticeVersion!=input.PrivacyNoticeVersion)
                    throw new EnquiryConflictException();
                return new(item.Reference,false);
            }
        }
        await using var create=Command("insert into gts_enquiries.enquiries(enquiry_type,name,email,phone,company_name,country_code,service,message,consent_at,privacy_notice_version,submission_key) values($1,$2,$3,$4,$5,$6,$7,$8,now(),$9,$10) returning id,reference,created_at",
            connection,input.EnquiryType,input.Name,input.Email,input.Phone,input.CompanyName,input.CountryCode,input.Service,input.Message,input.PrivacyNoticeVersion,input.SubmissionKey);
        Guid id; string reference; DateTimeOffset created;
        await using(var rows=await create.ExecuteReaderAsync(ct))
        {await rows.ReadAsync(ct);id=rows.GetGuid(0);reference=rows.GetString(1);created=rows.GetFieldValue<DateTimeOffset>(2);}
        await services.GetRequiredService<IEnquiryThreadInitializer>().Initialize(connection,tx,new(id,input.Message,created),ct);
        await tx.CommitAsync(ct);
        return new(reference,true);
    }
    private const string Filters = """
        where ($1::text is null or status=$1)
          and ($2::text is null or enquiry_type=$2)
          and ($3::uuid is null or assigned_staff_id=$3)
          and (not $4 or assigned_staff_id is null)
          and ($5::text is null or name ilike $5 or email ilike $5 or company_name ilike $5 or reference ilike $5)
        """;
    public async Task<EnquiryPage> List(EnquiryQuery query,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        string? search=string.IsNullOrWhiteSpace(query.Search)?null:"%"+query.Search.Trim().Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%";
        object?[] filters=[query.Status,query.EnquiryType,query.AssignedStaffId,query.Unassigned,search];
        long total;
        await using(var count=Command("select count(*) from gts_enquiries.enquiries "+Filters,connection,filters))
            total=(long)(await count.ExecuteScalarAsync(ct))!;
        var items=new List<EnquiryRecord>();
        await using(var list=Command($"select {Columns} from gts_enquiries.enquiries "+Filters+" order by created_at desc,id desc limit $6 offset $7",connection,[..filters,query.PageSize,(long)(query.Page-1)*query.PageSize]))
        await using(var rows=await list.ExecuteReaderAsync(ct))
            while(await rows.ReadAsync(ct)) items.Add(Record(rows));
        await tx.CommitAsync(ct);
        return new(items,total,query.Page,query.PageSize);
    }
    public async Task<EnquiryRecord?> Get(Guid id,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command($"select {Columns} from gts_enquiries.enquiries where id=$1",connection,id);
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct)?Record(rows):null;
    }
    public async Task<EnquiryConversationSource?> Source(Guid enquiryId,CancellationToken ct)
    {
        var item=await Get(enquiryId,ct);
        return item is null?null:new(item.Id,item.Message,item.CreatedAt);
    }
    public async Task<bool> CanAccess(Guid enquiryId,ClaimsPrincipal user,CancellationToken ct)
    {
        if(!Guid.TryParse(user.FindFirstValue("app_user_id"),out var userId))return false;
        await using var connection=await Database.OpenConnectionAsync(ct);
        Guid? owner,company;
        await using(var cmd=Command("select user_id,company_id from gts_enquiries.enquiries where id=$1",connection,enquiryId))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))
        {
            if(!await rows.ReadAsync(ct))return false;
            owner=rows.IsDBNull(0)?null:rows.GetGuid(0);company=rows.IsDBNull(1)?null:rows.GetGuid(1);
        }
        if(company.HasValue)return await services.GetRequiredService<ICompanyAccess>().CanAccess(company.Value,user,false,ct);
        return owner==userId;
    }
    public async Task<UpdateResult> Update(Guid id,UpdateEnquiry input,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using(var cmd=Command("update gts_enquiries.enquiries set status=coalesce($1::text,status),assigned_staff_id=case when $2 then $3::uuid else assigned_staff_id end where id=$4 and updated_at=$5 returning "+Columns,
            connection,input.Status,input.ClearAssignment||input.AssignedStaffId.HasValue,input.AssignedStaffId,id,input.ExpectedUpdatedAt!.Value.ToUniversalTime()))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))
            if(await rows.ReadAsync(ct)) return new(UpdateOutcome.Updated,Record(rows));
        await using var exists=Command("select exists(select 1 from gts_enquiries.enquiries where id=$1)",connection,id);
        return (bool)(await exists.ExecuteScalarAsync(ct))!?new(UpdateOutcome.Conflict):new(UpdateOutcome.NotFound);
    }
}
