using System.Text.Json;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

// Run from repository root. Credentials remain in local user secrets.
var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json");
using var secret=JsonDocument.Parse(await File.ReadAllTextAsync(path));
var config=new NpgsqlConnectionStringBuilder(secret.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
if(config.Host!="aws-0-eu-west-1.pooler.supabase.com"||config.Username!="postgres.aqoiglalbsqprvngmqbf")throw new Exception("Approved development database required.");
config.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");
await using var db=NpgsqlDataSource.Create(config.ConnectionString);
var collection=new ServiceCollection().AddSingleton(db).AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>();
collection.AddSingleton<IdentityStore>(s=>new(s,Options.Create(new SupabaseAuthOptions())));
collection.AddSingleton<IStaffDirectory>(s=>s.GetRequiredService<IdentityStore>());
collection.AddSingleton<EnquiryStore>();collection.AddSingleton<IEnquiryConversationAccess>(s=>s.GetRequiredService<EnquiryStore>());
collection.AddSingleton<IConversationStore,ConversationStore>();
await using var services=collection.BuildServiceProvider();
var ids=new List<Guid>();
await using(var cmd=db.CreateCommand("select e.id from gts_enquiries.enquiries e left join gts_conversations.threads t on t.enquiry_id=e.id where t.id is null"))
await using(var rows=await cmd.ExecuteReaderAsync())while(await rows.ReadAsync())ids.Add(rows.GetGuid(0));
var store=services.GetRequiredService<IConversationStore>();
foreach(var id in ids)await store.EnsureForEnquiry(id,default);
await using(var check=db.CreateCommand("select count(*) from gts_enquiries.enquiries e where not exists(select 1 from gts_conversations.threads t join gts_conversations.messages m on m.thread_id=t.id where t.enquiry_id=e.id and m.channel='form' and m.sender_kind='visitor' and m.body=e.message and m.created_at=e.created_at)"))
 if((long)(await check.ExecuteScalarAsync())! !=0)throw new Exception("Some enquiries lack their original conversation message.");
Console.WriteLine($"Backfilled {ids.Count} enquiries. All enquiries have an original conversation message.");
