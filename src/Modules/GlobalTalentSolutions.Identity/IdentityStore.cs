using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GlobalTalentSolutions.Identity;

public interface IIdentityStore
{
    Task<bool> SessionActive(VerifiedIdentity identity, CancellationToken ct);
    Task<AppAccount?> FindAccount(Guid authId, CancellationToken ct);
    Task SyncEmail(Guid authId, string verifiedEmail, CancellationToken ct);
    Task<InvitationRecord?> FindInvitation(string token, CancellationToken ct);
    Task<InvitationCreated> Invite(InviteInput input, Guid staffId, CancellationToken ct);
    Task<AppAccount> Accept(AcceptInvitationInput input, Guid authId, string email, CancellationToken ct);
    Task<List<CompanyRecord>> Companies(ClaimsPrincipal user, CancellationToken ct);
    Task<CompanyRecord?> Company(Guid companyId, CancellationToken ct);
    Task<CompanyRecord> CreateCompany(CompanyInput input, CancellationToken ct);
    Task<List<MemberRecord>> Members(Guid companyId, CancellationToken ct);
    Task<bool> UpdateMember(Guid companyId, Guid userId, MembershipInput input, Guid actorId, CancellationToken ct);
}
public sealed partial class IdentityStore(IServiceProvider services, IOptions<SupabaseAuthOptions> options) : IIdentityStore, ICompanyAccess, IStaffDirectory, IOnboardingInvitations, ICompanyMemberships
{
    private NpgsqlDataSource Database => services.GetService<NpgsqlDataSource>() ?? throw new IdentityProviderException();
    public async Task<bool> IsActiveStaff(Guid userId, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("select exists(select 1 from gts_identity.staff s join gts_identity.users u on u.id=s.user_id where u.id=$1 and u.status='active' and u.account_type='staff')",connection,userId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<List<StaffMember>> ActiveStaff(CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("select u.id,u.display_name,s.role from gts_identity.staff s join gts_identity.users u on u.id=s.user_id where u.status='active' and u.account_type='staff' order by u.display_name,u.id",connection);
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        var result=new List<StaffMember>();
        while(await rows.ReadAsync(ct)) result.Add(new(rows.GetGuid(0),rows.GetString(1),rows.GetString(2)));
        return result;
    }
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, params object?[] values)
    {
        var command = new NpgsqlCommand(sql,connection);
        foreach(var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        return command;
    }
    private static AppAccount Account(NpgsqlDataReader row) => new(row.GetGuid(0),row.GetGuid(1),row.GetString(2),row.GetString(3),row.GetString(4),row.IsDBNull(5)?null:row.GetString(5));
    private const string AccountSql = "select u.id,u.auth_user_id,u.email,u.account_type,u.status,s.role from gts_identity.users u left join gts_identity.staff s on s.user_id=u.id where u.auth_user_id=$1";
    public async Task<bool> SessionActive(VerifiedIdentity identity, CancellationToken ct)
    {
        await using var connection = await Database.OpenConnectionAsync(ct);
        await using var cmd = Command("select exists(select 1 from auth.sessions where id=$1 and user_id=$2 and (not_after is null or not_after>now()))",connection,identity.SessionId,identity.Id);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<AppAccount?> FindAccount(Guid authId, CancellationToken ct)
    {
        await using var connection = await Database.OpenConnectionAsync(ct);
        await using var cmd = Command(AccountSql,connection,authId);
        await using var rows = await cmd.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? Account(rows) : null;
    }
    public async Task SyncEmail(Guid authId, string verifiedEmail, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("update gts_identity.users set email=$1 where auth_user_id=$2 and email<>$1 and status='active'",connection,verifiedEmail,authId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<bool> CanAccess(Guid companyId, ClaimsPrincipal user, bool ownerRequired, CancellationToken ct)
    {
        await using var connection = await Database.OpenConnectionAsync(ct);
        if(user.IsInRole("admin") || user.IsInRole("staff"))
        {
            await using var staff = Command("select exists(select 1 from gts_identity.companies where id=$1 and status='active')",connection,companyId);
            return (bool)(await staff.ExecuteScalarAsync(ct))!;
        }
        if(!Guid.TryParse(user.FindFirstValue("app_user_id"),out var userId)) return false;
        await using var cmd = Command("select exists(select 1 from gts_identity.memberships m join gts_identity.companies c on c.id=m.company_id where m.company_id=$1 and m.user_id=$2 and m.status='active' and c.status='active' and (not $3 or m.role='owner'))",connection,companyId,userId,ownerRequired);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<InvitationRecord?> FindInvitation(string token, CancellationToken ct)
    {
        await using var connection = await Database.OpenConnectionAsync(ct);
        await using var cmd = Command("select id,email,account_type,company_id,membership_role from gts_identity.invitations where token_hash=$1 and accepted_at is null and revoked_at is null and expires_at>now()",connection,Hash(token));
        await using var row = await cmd.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) ? new(row.GetGuid(0),row.GetString(1),row.GetString(2),row.IsDBNull(3)?null:row.GetGuid(3),row.IsDBNull(4)?null:row.GetString(4)) : null;
    }
    public async Task<InvitationCreated> Invite(InviteInput input, Guid staffId, CancellationToken ct)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddDays(3);
        await using var connection = await Database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if(input.CompanyId is {} companyId)
        {
            await using var company = Command("select id from gts_identity.companies where id=$1 and status='active' for share",connection,companyId);
            if(await company.ExecuteScalarAsync(ct) is null) throw new IdentityConflictException("Company is unavailable.");
        }
        await using var cmd = Command("insert into gts_identity.invitations(email,account_type,company_id,membership_role,token_hash,invited_by,expires_at) values($1,$2,$3,$4,$5,$6,$7) returning id",connection,input.Email.Trim().ToLowerInvariant(),input.AccountType,input.CompanyId,input.MembershipRole,Hash(token),staffId,expires);
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return new(id,token,expires,options.Value.FrontendUrl.TrimEnd('/')+"/accept-invitation#token="+token);
    }
    public async Task<AppAccount> Accept(AcceptInvitationInput input, Guid authId, string email, CancellationToken ct)
    {
        await using var connection = await Database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var account=await AcceptInTransaction(connection,tx,input,authId,email,ct);
        await tx.CommitAsync(ct);
        return account;
    }
    internal async Task<AppAccount> AcceptInTransaction(NpgsqlConnection connection,NpgsqlTransaction tx,AcceptInvitationInput input,Guid authId,string email,CancellationToken ct)
    {
        InvitationRecord invitation;
        await using(var cmd = Command("select id,email,account_type,company_id,membership_role from gts_identity.invitations where token_hash=$1 and accepted_at is null and revoked_at is null and expires_at>now() for update",connection,Hash(input.Token)))
        await using(var row = await cmd.ExecuteReaderAsync(ct))
        {
            if(!await row.ReadAsync(ct) || !row.GetString(1).Equals(email,StringComparison.OrdinalIgnoreCase))
                throw new IdentityConflictException("Invitation is invalid, expired or unavailable.");
            invitation = new(row.GetGuid(0),row.GetString(1),row.GetString(2),row.IsDBNull(3)?null:row.GetGuid(3),row.IsDBNull(4)?null:row.GetString(4));
        }
        AppAccount? account;
        await using(var cmd=Command(AccountSql,connection,authId))
        await using(var rows=await cmd.ExecuteReaderAsync(ct)) account = await rows.ReadAsync(ct)?Account(rows):null;
        if(account is not null && (account.Status!="active" || account.AccountType!=invitation.AccountType))
            throw new IdentityConflictException("Account cannot accept this invitation.");
        if(account is null)
        {
            await using var create=Command("insert into gts_identity.users(auth_user_id,display_name,email,account_type) values($1,$2,$3,$4) returning id",connection,authId,input.DisplayName.Trim(),email,invitation.AccountType);
            var id=(Guid)(await create.ExecuteScalarAsync(ct))!;
            account=new(id,authId,email,invitation.AccountType,"active",null);
        }
        if(invitation.CompanyId is {} companyId)
        {
            await using var company=Command("select id from gts_identity.companies where id=$1 and status='active' for update",connection,companyId);
            if(await company.ExecuteScalarAsync(ct) is null) throw new IdentityConflictException("Company is unavailable.");
            await using var membership=Command("insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,$3) on conflict(company_id,user_id) do update set status='active'",connection,companyId,account.Id,invitation.MembershipRole);
            await membership.ExecuteNonQueryAsync(ct);
        }
        await using var accept=Command("update gts_identity.invitations set accepted_at=now(),accepted_by=$1 where id=$2",connection,account.Id,invitation.Id);
        await accept.ExecuteNonQueryAsync(ct);
        foreach(var linker in services.GetServices<IInvitationAcceptanceLinker>())
            await linker.Link(connection,tx,invitation.Id,account.Id,invitation.CompanyId,ct);
        return account;
    }
    public async Task<List<CompanyRecord>> Companies(ClaimsPrincipal user, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        var staff=user.IsInRole("staff") || user.IsInRole("admin");
        await using var cmd=Command("select c.id,c.name,c.country_code,c.status from gts_identity.companies c where c.status='active' and ($1 or exists(select 1 from gts_identity.memberships m where m.company_id=c.id and m.user_id=$2 and m.status='active')) order by c.name,c.id limit 100",connection,staff,user.AppId());
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        var result=new List<CompanyRecord>();
        while(await rows.ReadAsync(ct)) result.Add(new(rows.GetGuid(0),rows.GetString(1),rows.GetString(2),rows.GetString(3)));
        return result;
    }
    public async Task<CompanyRecord> CreateCompany(CompanyInput input, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("insert into gts_identity.companies(name,country_code) values($1,$2) returning id",connection,input.Name.Trim(),input.CountryCode.ToUpperInvariant());
        return new((Guid)(await cmd.ExecuteScalarAsync(ct))!,input.Name.Trim(),input.CountryCode.ToUpperInvariant(),"active");
    }
    public async Task<CompanyRecord?> Company(Guid companyId, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("select id,name,country_code,status from gts_identity.companies where id=$1 and status='active'",connection,companyId);
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct)?new(rows.GetGuid(0),rows.GetString(1),rows.GetString(2),rows.GetString(3)):null;
    }
    public async Task<List<MemberRecord>> Members(Guid companyId, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("select u.id,u.display_name,u.email,m.role,m.status from gts_identity.memberships m join gts_identity.users u on u.id=m.user_id where m.company_id=$1 order by u.display_name,u.id limit 100",connection,companyId);
        await using var rows=await cmd.ExecuteReaderAsync(ct);
        var result=new List<MemberRecord>();
        while(await rows.ReadAsync(ct)) result.Add(new(rows.GetGuid(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4)));
        return result;
    }
    public async Task<bool> UpdateMember(Guid companyId, Guid userId, MembershipInput input, Guid actorId, CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var company=Command("select id from gts_identity.companies where id=$1 and status='active' for update",connection,companyId))
            if(await company.ExecuteScalarAsync(ct) is null) return false;
        await using(var actor=Command("select exists(select 1 from gts_identity.users u where u.id=$1 and u.status='active' and (exists(select 1 from gts_identity.staff s where s.user_id=u.id and u.account_type='staff') or exists(select 1 from gts_identity.memberships m where m.company_id=$2 and m.user_id=u.id and m.status='active' and m.role='owner')))",connection,actorId,companyId))
            if(!(bool)(await actor.ExecuteScalarAsync(ct))!) throw new IdentityForbiddenException();
        // Serialise owner changes on the company to prevent removing the last owner.
        await using(var owner=Command("select role,status from gts_identity.memberships where company_id=$1 and user_id=$2 for update",connection,companyId,userId))
        await using(var rows=await owner.ExecuteReaderAsync(ct))
        {
            if(!await rows.ReadAsync(ct)) return false;
            var removesOwner=rows.GetString(0)=="owner" && rows.GetString(1)=="active" && (input.Role!="owner" || input.Status!="active");
            await rows.DisposeAsync();
            if(removesOwner)
            {
                await using var count=Command("select count(*) from gts_identity.memberships where company_id=$1 and role='owner' and status='active'",connection,companyId);
                if((long)(await count.ExecuteScalarAsync(ct))!<=1) throw new IdentityConflictException("A company must retain an active owner.");
            }
        }
        await using var update=Command("update gts_identity.memberships set role=$1,status=$2 where company_id=$3 and user_id=$4",connection,input.Role,input.Status,companyId,userId);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
