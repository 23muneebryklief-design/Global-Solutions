using System.Data.Common;
using GlobalTalentSolutions.SharedKernel;
using Npgsql;

namespace GlobalTalentSolutions.Conversations;

public sealed class ThreadInitializer : IEnquiryThreadInitializer
{
    public async Task Initialize(DbConnection connection, DbTransaction transaction, EnquiryConversationSource source, CancellationToken ct)
    {
        await using var thread = new NpgsqlCommand("insert into gts_conversations.threads(enquiry_id,created_at,updated_at) values($1,$2,$2) on conflict(enquiry_id) do nothing returning id",(NpgsqlConnection)connection,(NpgsqlTransaction)transaction);
        thread.Parameters.AddWithValue(source.Id); thread.Parameters.AddWithValue(source.CreatedAt.ToUniversalTime());
        var id = await thread.ExecuteScalarAsync(ct);
        if (id is not Guid threadId) return;
        await using var message = new NpgsqlCommand("insert into gts_conversations.messages(thread_id,sender_kind,channel,body,created_at) values($1,'visitor','form',$2,$3)",(NpgsqlConnection)connection,(NpgsqlTransaction)transaction);
        message.Parameters.AddWithValue(threadId);message.Parameters.AddWithValue(source.Message);message.Parameters.AddWithValue(source.CreatedAt.ToUniversalTime());
        await message.ExecuteNonQueryAsync(ct);
    }
}
