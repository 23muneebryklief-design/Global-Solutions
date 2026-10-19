using System.Data.Common;
using System.Security.Cryptography;
using GlobalTalentSolutions.SharedKernel;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace GlobalTalentSolutions.Identity;

public sealed partial class IdentityStore
{
    public async Task<string?> Recipient(DbConnection connection,DbTransaction transaction,Guid accountId,Guid? companyId,CancellationToken ct)
    {
        await using var cmd=Command("select u.email from gts_identity.users u where u.id=$1 and u.status='active' and ($2::uuid is null or exists(select 1 from gts_identity.memberships m join gts_identity.companies c on c.id=m.company_id where m.company_id=$2 and m.user_id=u.id and m.status='active' and c.status='active'))",(NpgsqlConnection)connection,accountId,companyId);
        return await cmd.ExecuteScalarAsync(ct)as string;
    }
    public async Task<bool> CanSend(DbConnection connection,DbTransaction transaction,Guid invitationId,CancellationToken ct)
    {
        await using var cmd=Command("select exists(select 1 from gts_identity.invitations where id=$1 and accepted_at is null and revoked_at is null and expires_at>now())",(NpgsqlConnection)connection,invitationId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task<Guid[]> ActiveCompanyIds(Guid userId,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("select m.company_id from gts_identity.memberships m join gts_identity.companies c on c.id=m.company_id join gts_identity.users u on u.id=m.user_id where m.user_id=$1 and m.status='active' and c.status='active' and u.status='active'",connection,userId);
        var ids=new List<Guid>();await using var rows=await cmd.ExecuteReaderAsync(ct);while(await rows.ReadAsync(ct))ids.Add(rows.GetGuid(0));return ids.ToArray();
    }
    public async Task<OnboardingInvitation> Create(DbConnection connection,DbTransaction transaction,string email,string accountType,Guid? companyId,string? role,Guid staffId,CancellationToken ct)
    {
        var db=(NpgsqlConnection)connection;
        // Validate the actor again inside the same transaction as the approval.
        await using(var actor=Command("select u.id from gts_identity.users u join gts_identity.staff s on s.user_id=u.id where u.id=$1 and u.status='active' and u.account_type='staff' for share of u",db,staffId))
            if(await actor.ExecuteScalarAsync(ct)is null)throw new IdentityForbiddenException();
        if(companyId is {} company)
        {
            await using var check=Command("select id from gts_identity.companies where id=$1 and status='active' for share",db,company);
            if(await check.ExecuteScalarAsync(ct)is null)throw new IdentityConflictException("Company is unavailable.");
        }
        var token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));var expires=DateTimeOffset.UtcNow.AddDays(3);
        await using var cmd=Command("insert into gts_identity.invitations(email,account_type,company_id,membership_role,token_hash,invited_by,expires_at) values($1,$2,$3,$4,$5,$6,$7) returning id",db,email,accountType,companyId,role,Hash(token),staffId,expires);
        var id=(Guid)(await cmd.ExecuteScalarAsync(ct))!;
        var url=options.Value.FrontendUrl.TrimEnd('/')+"/accept-invitation#token="+token;
        foreach(var notifications in services.GetServices<IEmailNotifications>())await notifications.InvitationCreated(connection,transaction,id,email,url,ct);
        return new(id,token,expires,url);
    }
    public async Task<bool> RevokePending(DbConnection connection,DbTransaction transaction,Guid invitationId,CancellationToken ct)
    {
        await using var cmd=Command("update gts_identity.invitations set revoked_at=coalesce(revoked_at,now()) where id=$1 and accepted_at is null returning id",(NpgsqlConnection)connection,invitationId);
        var revoked=await cmd.ExecuteScalarAsync(ct)is Guid;
        if(revoked)foreach(var notifications in services.GetServices<IEmailNotifications>())await notifications.CancelInvitation(connection,transaction,invitationId,ct);
        return revoked;
    }
    public async Task<OnboardingInvitationState?> State(Guid invitationId,CancellationToken ct)
    {
        await using var connection=await Database.OpenConnectionAsync(ct);
        await using var cmd=Command("select id,expires_at,accepted_at,revoked_at from gts_identity.invitations where id=$1",connection,invitationId);
        await using var row=await cmd.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct)?new(row.GetGuid(0),row.GetFieldValue<DateTimeOffset>(1),row.IsDBNull(2)?null:row.GetFieldValue<DateTimeOffset>(2),row.IsDBNull(3)?null:row.GetFieldValue<DateTimeOffset>(3)):null;
    }
}
