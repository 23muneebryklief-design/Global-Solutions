using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

static class HostedChecks
{
 public static async Task Run()
 {
  var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json");
  using var secret=JsonDocument.Parse(await File.ReadAllTextAsync(path));
  var config=new NpgsqlConnectionStringBuilder(secret.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
  if(config.Host!="aws-0-eu-west-1.pooler.supabase.com"||config.Username!="postgres.aqoiglalbsqprvngmqbf")throw new Exception("Development database required.");
  config.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");config.MaxPoolSize=10;
  await using var db=NpgsqlDataSource.Create(config.ConnectionString);
  var collection=new ServiceCollection().AddSingleton(db).AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>();
  collection.AddSingleton<IdentityStore>(s=>new(s,Options.Create(new SupabaseAuthOptions())));
  collection.AddSingleton<IStaffDirectory>(s=>s.GetRequiredService<IdentityStore>());
  collection.AddSingleton<ICompanyAccess>(s=>s.GetRequiredService<IdentityStore>());
  collection.AddSingleton<EnquiryStore>();collection.AddSingleton<IEnquiryConversationAccess>(s=>s.GetRequiredService<EnquiryStore>());
  collection.AddSingleton<IConversationStore,ConversationStore>();
  await using var services=collection.BuildServiceProvider();
  var enquiries=services.GetRequiredService<EnquiryStore>();var store=services.GetRequiredService<IConversationStore>();
  var ids=new List<Guid>();var keys=new List<Guid>();var staffId=Guid.NewGuid();var userId=Guid.NewGuid();var otherId=Guid.NewGuid();var companyId=Guid.NewGuid();
  string prefix="be05-"+Guid.NewGuid().ToString("N");int passed=0;
  async Task Exec(string sql,params object[] values){await using var cmd=db.CreateCommand(sql);foreach(var v in values)cmd.Parameters.AddWithValue(v);await cmd.ExecuteNonQueryAsync();}
  void Assert(bool condition){if(!condition)throw new Exception("Conversation integration assertion failed.");}
  async Task Check(string name,Func<Task> run){await run();passed++;Console.WriteLine("PASS (hosted DB): "+name);}
  ClaimsPrincipal User(Guid id,string role)=>new(new ClaimsIdentity([new("app_user_id",id.ToString()),new(ClaimTypes.Role,role)],"test"));
  var staff=User(staffId,"staff");var user=User(userId,"client");var other=User(otherId,"client");
  NormalizedEnquiry Input(){var key=Guid.NewGuid();keys.Add(key);return new("business","Test person",prefix+"@example.invalid",null,"Test company","GB","Staffing","Original enquiry message.",key,"v1");}
  try
  {
   foreach(var id in new[]{staffId,userId,otherId})await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'Disposable test',$2,$3)",id,prefix+id+"@example.invalid",id==staffId?"staff":"client");
   await Exec("insert into gts_identity.staff(user_id,role) values($1,'staff')",staffId);
   var input=Input();var receipt=await enquiries.Submit(input,default);
   var enq=(await enquiries.List(new(Search:receipt.Reference),default)).Items.Single();ids.Add(enq.Id);
   var thread=(await store.EnsureForEnquiry(enq.Id,default))!;
   await Check("Submission creates original visitor message",async()=>{var p=await store.Messages(thread.Id,1,50,default);Assert(p.TotalCount==1&&p.Items[0].Body==input.Message&&p.Items[0].SenderKind=="visitor"&&p.Items[0].Channel=="form"&&p.Items[0].CreatedAt==enq.CreatedAt);});
   await Check("Submission retry keeps one thread and original",async()=>{Assert(!(await enquiries.Submit(input,default)).Created);Assert((await store.Messages(thread.Id,1,50,default)).TotalCount==1);});
   await Check("Staff can access enquiry conversation",async()=>Assert(await store.CanAccess(thread.Id,staff,default)));
   await Check("Unlinked users cannot access",async()=>Assert(!await store.CanAccess(thread.Id,user,default)));
   await Exec("update gts_identity.users set email=$2 where id=$1",userId,input.Email);
   await Check("Matching email grants no access",async()=>Assert(!await store.CanAccess(thread.Id,user,default)));
   await Exec("update gts_enquiries.enquiries set user_id=$2 where id=$1",enq.Id,userId);
   await Check("Explicit account ownership grants access",async()=>Assert(await store.CanAccess(thread.Id,user,default)));
   await Check("Other account remains denied",async()=>Assert(!await store.CanAccess(thread.Id,other,default)));
   var reply=new MessageInput("  Client reply  ",Guid.NewGuid());var sent=(await store.Send(thread.Id,reply,user,default))!;
   await Check("Reply author and channel come from server",()=>{Assert(sent.Created&&sent.Message.SenderUserId==userId&&sent.Message.SenderKind=="client"&&sent.Message.Channel=="portal"&&sent.Message.Body=="Client reply");return Task.CompletedTask;});
   await Check("Exact message retry returns original",async()=>{var r=(await store.Send(thread.Id,reply,user,default))!;Assert(!r.Created&&r.Message.Id==sent.Message.Id);});
   await Check("Changed retry content conflicts",async()=>{try{await store.Send(thread.Id,reply with {Body="Changed"},user,default);throw new Exception("Expected conflict");}catch(ConversationConflictException){}});
   await Check("Unauthorized reply adds nothing",async()=>Assert(await store.Send(thread.Id,new("Unauthorized",Guid.NewGuid()),other,default)is null));
   await Check("Concurrent retries create one message",async()=>{var r=new MessageInput("Concurrent reply",Guid.NewGuid());var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>store.Send(thread.Id,r,user,default)));Assert(results.Count(i=>i!.Created)==1&&results.Select(i=>i!.Message.Id).Distinct().Count()==1);});
   await store.Send(thread.Id,new("Staff reply",Guid.NewGuid()),staff,default);
   await Check("Original stays first with paginated total",async()=>{var p=await store.Messages(thread.Id,1,1,default);Assert(p.TotalCount==4&&p.Items.Count==1&&p.Items[0].Channel=="form");var second=await store.Messages(thread.Id,2,1,default);Assert(second.Items[0].Id==sent.Message.Id);});
   await Check("Notes are separate from public messages",async()=>{var n=await store.AddNote(thread.Id,new("Staff-only note"),staff,default);Assert(n?.AuthorStaffId==staffId);Assert((await store.Notes(thread.Id,1,50,default)).TotalCount==1);Assert((await store.Messages(thread.Id,1,50,default)).Items.All(m=>m.Body!="Staff-only note"));});
   await Check("Client cannot add internal note",async()=>Assert(await store.AddNote(thread.Id,new("Invalid note"),user,default)is null));
   await Exec("insert into gts_conversations.participants(thread_id,user_id,revoked_at) values($1,$2,now())",thread.Id,userId);
   await Check("Revoked participant denied",async()=>Assert(!await store.CanAccess(thread.Id,user,default)));
   await Exec("delete from gts_conversations.participants where thread_id=$1",thread.Id);
   await Exec("insert into gts_identity.companies(id,name,country_code) values($1,'Disposable company','GB')",companyId);
   await Exec("insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'owner')",companyId,userId);
   await Exec("update gts_enquiries.enquiries set company_id=$2 where id=$1",enq.Id,companyId);
   await Check("Active company member allowed",async()=>Assert(await store.CanAccess(thread.Id,user,default)));
   await Exec("update gts_identity.memberships set status='revoked' where company_id=$1",companyId);
   await Check("Revoked membership overrides original account link",async()=>Assert(!await store.CanAccess(thread.Id,user,default)));
   await Check("Unknown thread denied",async()=>Assert(!await store.CanAccess(Guid.NewGuid(),staff,default)));
   await Exec("update gts_identity.users set status='suspended' where id=$1",staffId);
   await Check("Suspended staff denied",async()=>Assert(!await store.CanAccess(thread.Id,staff,default)));
   var legacyId=Guid.NewGuid();ids.Add(legacyId);
   await Exec("insert into gts_enquiries.enquiries(id,enquiry_type,name,email,company_name,service,message,consent_at,privacy_notice_version) values($1,'business','Legacy test',$2,'Company','Staffing','Legacy original message',now(),'v1')",legacyId,prefix+"-legacy@example.invalid");
   await Check("Concurrent legacy backfill produces one original",async()=>{var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>store.EnsureForEnquiry(legacyId,default)));Assert(results.Select(r=>r!.Id).Distinct().Count()==1);var p=await store.Messages(results[0]!.Id,1,50,default);Assert(p.TotalCount==1&&p.Items[0].Body=="Legacy original message");});
   await Check("Thread failure rolls back whole submission",async()=>{
    await using var failing=new ServiceCollection().AddSingleton(db).AddSingleton<IEnquiryThreadInitializer,FailingInitializer>().BuildServiceProvider();
    var bad=Input();try{await new EnquiryStore(failing).Submit(bad,default);throw new Exception("Expected failure");}catch(InjectedFailure){}
    await using var cmd=db.CreateCommand("select count(*) from gts_enquiries.enquiries where submission_key=$1");cmd.Parameters.AddWithValue(bad.SubmissionKey);Assert((long)(await cmd.ExecuteScalarAsync())! ==0);
   });
   Console.WriteLine($"{passed} conversation hosted checks passed.");
  }
  finally
  {
   foreach(var id in ids){foreach(var table in new[]{"reply_routes","internal_notes","messages","participants"})await Exec($"delete from gts_conversations.{table} where thread_id in(select id from gts_conversations.threads where enquiry_id=$1)",id);await Exec("delete from gts_conversations.threads where enquiry_id=$1",id);await Exec("delete from gts_enquiries.enquiries where id=$1",id);}
   await Exec("delete from gts_identity.memberships where company_id=$1",companyId);await Exec("delete from gts_identity.companies where id=$1",companyId);
   await Exec("delete from gts_identity.staff where user_id=$1",staffId);
   foreach(var id in new[]{staffId,userId,otherId})await Exec("delete from gts_identity.users where id=$1",id);
  }
 }
}
sealed class InjectedFailure:Exception;
sealed class FailingInitializer:IEnquiryThreadInitializer
{
 public async Task Initialize(DbConnection c,DbTransaction t,EnquiryConversationSource s,CancellationToken ct){await new ThreadInitializer().Initialize(c,t,s,ct);throw new InjectedFailure();}
}
