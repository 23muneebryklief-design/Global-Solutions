using System.Security.Claims;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace GlobalTalentSolutions.Conversations;

public sealed record PortalConversation(ThreadRecord Thread,PortalEnquiry Enquiry);
public sealed record PortalConversationPage(List<PortalConversation> Items,long TotalCount,int Page,int PageSize);
public interface IPortalConversationList
{
    Task<PortalConversationPage> List(ClaimsPrincipal user,int page,int pageSize,CancellationToken ct);
}
public sealed class PortalConversationList(IServiceProvider services,IPortalEnquiries enquiries,IConversationStore conversations):IPortalConversationList
{
    public async Task<PortalConversationPage> List(ClaimsPrincipal user,int page,int pageSize,CancellationToken ct)
    {
        var db=services.GetService<NpgsqlDataSource>()??throw new ConversationUnavailableException();
        var excluded=new List<Guid>();
        await using(var cmd=db.CreateCommand("select t.enquiry_id from gts_conversations.participants p join gts_conversations.threads t on t.id=p.thread_id where p.user_id=$1 and p.revoked_at is not null"))
        {
            cmd.Parameters.AddWithValue(user.AppId());await using var rows=await cmd.ExecuteReaderAsync(ct);while(await rows.ReadAsync(ct))excluded.Add(rows.GetGuid(0));
        }
        var records=await enquiries.List(user,excluded.ToArray(),page,pageSize,ct);var items=new List<PortalConversation>();
        foreach(var item in records.Items)
        {
            var thread=await conversations.EnsureForEnquiry(item.Id,ct);
            if(thread is not null&&await conversations.CanAccess(thread.Id,user,ct))items.Add(new(thread,item));
        }
        return new(items,records.TotalCount,page,pageSize);
    }
}
public static class PortalConversationEndpoints
{
    public static void MapPortalConversationList(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/portal/conversations",async(int? page,int? pageSize,ClaimsPrincipal user,IPortalConversationList store,CancellationToken ct)=>
        {
            if((page??1)is <1 or >100000||(pageSize??25)is <1 or >100)return Results.BadRequest(new {message="Use page 1–100000 and pageSize 1–100."});
            return Results.Ok(await store.List(user,page??1,pageSize??25,ct));
        }).RequireAuthorization(AccessPolicies.AppUser).WithTags("Portal conversations");
    }
}
