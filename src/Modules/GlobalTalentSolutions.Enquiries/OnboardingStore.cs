using System.Data;
using System.Data.Common;
using System.Security.Claims;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GlobalTalentSolutions.Enquiries;

public sealed record ApproveEnquiry(Guid? CompanyId,string? MembershipRole,Guid? ReplaceInvitationId);
public sealed record EnquiryOnboarding(Guid EnquiryId,Guid InvitationId,Guid ApprovedBy,DateTimeOffset CreatedAt,OnboardingInvitationState? Invitation);
public interface IEnquiryOnboarding
{
    Task<OnboardingInvitation?> Approve(Guid enquiryId,ApproveEnquiry input,Guid actorId,CancellationToken ct);
    Task<EnquiryOnboarding?> Get(Guid enquiryId,CancellationToken ct);
}
public sealed class OnboardingStore(IServiceProvider services,IOnboardingInvitations invitations,ICompanyMemberships memberships):IEnquiryOnboarding,IInvitationAcceptanceLinker,IPortalEnquiries
{
    private NpgsqlDataSource Database=>services.GetService<NpgsqlDataSource>()??throw new EnquiryUnavailableException();
    private static NpgsqlCommand Cmd(string sql,NpgsqlConnection connection,params object?[] values)
    {
        var cmd=new NpgsqlCommand(sql,connection);foreach(var value in values)cmd.Parameters.AddWithValue(value??DBNull.Value);return cmd;
    }
    public async Task<EnquiryOnboarding?> Get(Guid enquiryId,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        Guid id,actor;DateTimeOffset created;
        await using(var cmd=Cmd("select invitation_id,approved_by,created_at from gts_enquiries.onboarding where enquiry_id=$1",connection,enquiryId))
        await using(var row=await cmd.ExecuteReaderAsync(ct))
        {if(!await row.ReadAsync(ct))return null;id=row.GetGuid(0);actor=row.GetGuid(1);created=row.GetFieldValue<DateTimeOffset>(2);}
        return new(enquiryId,id,actor,created,await invitations.State(id,ct));
    }
    public async Task<OnboardingInvitation?> Approve(Guid enquiryId,ApproveEnquiry input,Guid actorId,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        // Lock an old invitation before locking the enquiry, matching acceptance's order.
        if(input.ReplaceInvitationId is {} previous)
        {
            await using var matching=Cmd("select exists(select 1 from gts_enquiries.onboarding where enquiry_id=$1 and invitation_id=$2)",connection,enquiryId,previous);
            if(!(bool)(await matching.ExecuteScalarAsync(ct))!||!await invitations.RevokePending(connection,tx,previous,ct))throw new OnboardingConflictException();
        }
        string email,type;Guid? owner,company;
        await using(var cmd=Cmd("select email,enquiry_type,user_id,company_id from gts_enquiries.enquiries where id=$1 for update",connection,enquiryId))
        await using(var row=await cmd.ExecuteReaderAsync(ct))
        {
            if(!await row.ReadAsync(ct))return null;
            email=row.GetString(0);type=row.GetString(1);owner=row.IsDBNull(2)?null:row.GetGuid(2);company=row.IsDBNull(3)?null:row.GetGuid(3);
        }
        if(owner.HasValue||company.HasValue)throw new OnboardingConflictException();
        if(type=="business"?input.CompanyId is null||input.MembershipRole is not ("owner" or "member"):input.CompanyId is not null||input.MembershipRole is not null)
            throw new OnboardingConflictException();
        await using(var existing=Cmd("select invitation_id from gts_enquiries.onboarding where enquiry_id=$1",connection,enquiryId))
        {
            var found=await existing.ExecuteScalarAsync(ct);
            if(found is Guid id && input.ReplaceInvitationId!=id || found is not Guid && input.ReplaceInvitationId.HasValue)throw new OnboardingConflictException();
        }
        var invite=await invitations.Create(connection,tx,email,type=="business"?"client":"candidate",input.CompanyId,input.MembershipRole,actorId,ct);
        await using(var save=Cmd("insert into gts_enquiries.onboarding(enquiry_id,invitation_id,approved_by) values($1,$2,$3) on conflict(enquiry_id) do update set invitation_id=excluded.invitation_id,approved_by=excluded.approved_by,created_at=now()",connection,enquiryId,invite.Id,actorId))await save.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);return invite;
    }
    public async Task Link(DbConnection connection,DbTransaction transaction,Guid invitationId,Guid accountId,Guid? companyId,CancellationToken ct)
    {
        var db=(NpgsqlConnection)connection;
        Guid enquiryId;
        await using(var find=Cmd("select enquiry_id from gts_enquiries.onboarding where invitation_id=$1",db,invitationId))
        {if(await find.ExecuteScalarAsync(ct)is not Guid id)return;enquiryId=id;}
        await using var update=Cmd("update gts_enquiries.enquiries set user_id=$1,company_id=$2 where id=$3 and user_id is null and company_id is null returning id",db,accountId,companyId,enquiryId);
        if(await update.ExecuteScalarAsync(ct)is null)throw new OnboardingConflictException();
    }
    public async Task<PortalEnquiryPage> List(ClaimsPrincipal user,Guid[] excludedEnquiryIds,int page,int pageSize,CancellationToken ct)
    {
        var userId=user.AppId();var companies=await memberships.ActiveCompanyIds(userId,ct);
        await using var connection=await Database.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        const string filter=" where ((company_id is null and user_id=$1) or company_id=any($2)) and not(id=any($3))";
        await using var count=Cmd("select count(*) from gts_enquiries.enquiries"+filter,connection,userId,companies,excludedEnquiryIds);
        var total=(long)(await count.ExecuteScalarAsync(ct))!;var items=new List<PortalEnquiry>();
        await using(var cmd=Cmd("select id,reference,enquiry_type,service,status,company_name,created_at from gts_enquiries.enquiries"+filter+" order by created_at desc,id desc limit $4 offset $5",connection,userId,companies,excludedEnquiryIds,pageSize,(long)(page-1)*pageSize))
        await using(var rows=await cmd.ExecuteReaderAsync(ct))while(await rows.ReadAsync(ct))items.Add(new(rows.GetGuid(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.IsDBNull(5)?null:rows.GetString(5),rows.GetFieldValue<DateTimeOffset>(6)));
        await tx.CommitAsync(ct);return new(items,total,page,pageSize);
    }
}
