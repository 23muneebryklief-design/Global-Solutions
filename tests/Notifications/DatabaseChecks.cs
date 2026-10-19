using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.Notifications;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

static class DatabaseChecks
{
 public static async Task Run()
 {
  using var secret=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json")));
  var config=new NpgsqlConnectionStringBuilder(secret.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
  if(config.Host!="aws-0-eu-west-1.pooler.supabase.com"||config.Username!="postgres.aqoiglalbsqprvngmqbf")throw new Exception("Approved development database required.");
  config.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");config.MaxPoolSize=10;
  await using var db=NpgsqlDataSource.Create(config.ConnectionString);var prefix="be07-"+Guid.NewGuid().ToString("N");
  var options=new EmailOptions{Enabled=true,ApiKey="test-only-never-sent",FromEmail="sender@example.invalid",ReplyTo="reply@example.invalid",TeamEmail=prefix+"-team@example.invalid"};
  var collection=new ServiceCollection().AddSingleton(db).AddSingleton<IOptions<EmailOptions>>(Options.Create(options)).AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
  collection.AddSingleton<EmailQueue>();collection.AddSingleton<IEmailNotifications>(s=>s.GetRequiredService<EmailQueue>());
  collection.AddSingleton<IdentityStore>(s=>new(s,Options.Create(new SupabaseAuthOptions())));collection.AddSingleton<IInvitationEmailValidity>(s=>s.GetRequiredService<IdentityStore>());
  collection.AddSingleton<IEmailContact>(s=>s.GetRequiredService<IdentityStore>());
  collection.AddSingleton<IStaffDirectory>(s=>s.GetRequiredService<IdentityStore>());collection.AddSingleton<EnquiryStore>();
  collection.AddSingleton<IEnquiryMailDetails>(s=>s.GetRequiredService<EnquiryStore>());collection.AddSingleton<IEnquiryConversationAccess>(s=>s.GetRequiredService<EnquiryStore>());
  collection.AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>();collection.AddSingleton<IConversationStore,ConversationStore>();
  await using var services=collection.BuildServiceProvider();var queue=services.GetRequiredService<EmailQueue>();var enquiries=services.GetRequiredService<EnquiryStore>();
  var identity=services.GetRequiredService<IdentityStore>();var conversations=services.GetRequiredService<IConversationStore>();
  Guid staffId=Guid.NewGuid(),contactId=Guid.NewGuid(),companyId=Guid.NewGuid(),enquiryId=Guid.Empty;var jobs=new List<Guid>();int passed=0;
  async Task<object?> Scalar(string sql,params object[] values){await using var cmd=db.CreateCommand(sql);foreach(var v in values)cmd.Parameters.AddWithValue(v);return await cmd.ExecuteScalarAsync();}
  async Task Exec(string sql,params object[] values){await Scalar(sql,values);}
  void Assert(bool value){if(!value)throw new Exception("Notification database assertion failed.");}
  async Task Check(string name,Func<Task> run){await run();passed++;Console.WriteLine("PASS (hosted DB): "+name);}
  async Task<Guid> Job(){var id=Guid.NewGuid();jobs.Add(id);await Exec("insert into gts_notifications.email_outbox(id,idempotency_key,kind,recipient_email,subject,body,enquiry_id) values($1,$2,'team_alert',$3,'Fixture','Fixture body',$4)",id,prefix+id,prefix+"@example.invalid",enquiryId);return id;}
  async Task<OutboxEmail> Claim(Guid id)=>(await queue.Claim(id,default))??throw new Exception("Fixture not claimable");
  try
  {
   await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'Disposable email staff',$2,'staff')",staffId,prefix+"-staff@example.invalid");await Exec("insert into gts_identity.staff(user_id,role) values($1,'staff')",staffId);
   var input=new NormalizedEnquiry("business","Test person",prefix+"@example.invalid",null,"Company","GB","Staffing","Email queue original enquiry.",Guid.NewGuid(),"v1");
   var receipt=await enquiries.Submit(input,default);var enq=(await enquiries.List(new(Search:receipt.Reference),default)).Items.Single();enquiryId=enq.Id;
   await Check("Enquiry atomically queues confirmation and staff alert",async()=>Assert((long)(await Scalar("select count(*) from gts_notifications.email_outbox where enquiry_id=$1",enquiryId))! ==2));
   await Check("Submission retry does not duplicate emails",async()=>{await enquiries.Submit(input,default);Assert((long)(await Scalar("select count(*) from gts_notifications.email_outbox where enquiry_id=$1",enquiryId))! ==2);});
   var confirmation=(Guid)(await Scalar("select id from gts_notifications.email_outbox where enquiry_id=$1 and kind='enquiry_confirmation'",enquiryId))!;
   await Check("Stored email body is encrypted",async()=>{var body=(string)(await Scalar("select body from gts_notifications.email_outbox where id=$1",confirmation))!;Assert(body.StartsWith("dp:v1:")&&!body.Contains(receipt.Reference));});
   var claimed=await Claim(confirmation);
   await Check("Worker decrypts body and freezes envelope",()=>{Assert(claimed.Body.Contains(receipt.Reference)&&claimed.FromEmail==options.FromEmail&&claimed.Attempt==1);return Task.CompletedTask;});
   await Check("Claimed email cannot be leased twice",async()=>Assert(await queue.Claim(confirmation,default)is null));
   await Check("Acceptance records provider ID and sent state",async()=>{Assert(await queue.Complete(claimed,new(true,"fake-provider-id","accepted",false),default));Assert((string)(await Scalar("select status from gts_notifications.email_outbox where id=$1",confirmation))! =="sent");Assert((await queue.Attempts(confirmation,default))!.Single().ProviderMessageId=="fake-provider-id");});
   await Check("Sent email is not claimable",async()=>Assert(await queue.Claim(confirmation,default)is null));
   await Check("Stale lease completion cannot overwrite",async()=>Assert(!await queue.Complete(claimed,new(false,null,"stale",false),default)));
   var retryId=await Job();var retry=await Claim(retryId);
   await Check("Transient failure schedules delayed retry",async()=>{await queue.Complete(retry,new(false,null,"http_429",true,TimeSpan.FromMinutes(2)),default);Assert(await queue.Claim(retryId,default)is null);Assert((bool)(await Scalar("select status='failed' and available_at>now() from gts_notifications.email_outbox where id=$1",retryId))!);});
   await Exec("update gts_notifications.email_outbox set available_at=now()-interval '1 second' where id=$1",retryId);var second=await Claim(retryId);
   await Check("Retry retains outbox ID and increments attempt",()=>{Assert(second.Id==retry.Id&&second.Attempt==2&&second.FromEmail==retry.FromEmail);return Task.CompletedTask;});
   await queue.Complete(second,new(false,null,"provider_timeout",true,Unknown:true),default);
   await Check("Timeout recorded as unknown rather than delivered",async()=>Assert((await queue.Attempts(retryId,default))!.Last().Outcome=="unknown"));
   var permanentId=await Job();var permanent=await Claim(permanentId);await queue.Complete(permanent,new(false,null,"http_401",false),default);
   await Check("Permanent failure is visible but not retried",async()=>Assert(await queue.Claim(permanentId,default)is null&&(string)(await Scalar("select last_error from gts_notifications.email_outbox where id=$1",permanentId))! =="http_401"));
   var crashId=await Job();var abandoned=await Claim(crashId);await Exec("update gts_notifications.email_outbox set locked_until=now()-interval '1 second' where id=$1",crashId);var recovered=await Claim(crashId);
   await Check("Expired worker lease is recovered with a new token",()=>{Assert(recovered.Attempt==2&&recovered.LockToken!=abandoned.LockToken);return Task.CompletedTask;});
   await Check("Old worker cannot finish recovered lease",async()=>Assert(!await queue.Complete(abandoned,new(true,"late-provider","accepted",false),default)));
   await queue.Complete(recovered,new(true,"recovered-provider","accepted",false),default);
   var concurrentId=await Job();await Check("Concurrent workers lease one fixture once",async()=>{var r=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>queue.Claim(concurrentId,default)));Assert(r.Count(i=>i is not null)==1);});
   var expiredId=await Job();await Exec("update gts_notifications.email_outbox set first_attempt_at=now()-interval '21 hours' where id=$1",expiredId);
   await Check("Expired provider deduplication window stops automatic retries",async()=>Assert(await queue.Claim(expiredId,default)is null&&(string)(await Scalar("select last_error from gts_notifications.email_outbox where id=$1",expiredId))! =="retry_limit_or_window_exhausted"));
   var maxId=await Job();await Exec("update gts_notifications.email_outbox set attempts=8 where id=$1",maxId);
   await Check("Maximum attempts stops retries",async()=>Assert(await queue.Claim(maxId,default)is null));
   var thread=(await conversations.EnsureForEnquiry(enquiryId,default))!;var staff=new ClaimsPrincipal(new ClaimsIdentity([new("app_user_id",staffId.ToString()),new(ClaimTypes.Role,"staff")],"test"));var messageInput=new MessageInput("Staff emailed reply",Guid.NewGuid());
   var message=(await conversations.Send(thread.Id,messageInput,staff,default))!;await conversations.Send(thread.Id,messageInput,staff,default);
   await Check("Staff reply queues one correlated email across retries",async()=>Assert((long)(await Scalar("select count(*) from gts_notifications.email_outbox where message_id=$1",message.Message.Id))! ==1));
   var before=(long)(await Scalar("select count(*) from gts_notifications.email_outbox where enquiry_id=$1",enquiryId))!;await conversations.AddNote(thread.Id,new("Private staff note"),staff,default);
   await Check("Private notes never generate emails",async()=>Assert((long)(await Scalar("select count(*) from gts_notifications.email_outbox where enquiry_id=$1",enquiryId))! ==before));
   await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'Disposable contact',$2,'client')",contactId,prefix+"-current@example.invalid");
   await Exec("insert into gts_identity.companies(id,name,country_code) values($1,'Disposable email company','GB')",companyId);
   await Exec("insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'owner')",companyId,contactId);
   await Exec("update gts_enquiries.enquiries set user_id=$2,company_id=$3 where id=$1",enquiryId,contactId,companyId);
   var currentReply=(await conversations.Send(thread.Id,new("Reply after email change",Guid.NewGuid()),staff,default))!;
   await Check("Linked contact uses current verified account address",async()=>Assert((string)(await Scalar("select recipient_email from gts_notifications.email_outbox where message_id=$1",currentReply.Message.Id))! ==prefix+"-current@example.invalid"));
   await Exec("update gts_identity.memberships set status='revoked' where user_id=$1",contactId);
   var revokedReply=(await conversations.Send(thread.Id,new("Portal-only reply after revocation",Guid.NewGuid()),staff,default))!;
   await Check("Revoked contact is not emailed new replies",async()=>Assert((long)(await Scalar("select count(*) from gts_notifications.email_outbox where message_id=$1",revokedReply.Message.Id))! ==0));
   var invite=await identity.Invite(new(prefix+"-invite@example.invalid","candidate",null,null),staffId,default);
   var inviteEmail=(Guid)(await Scalar("select id from gts_notifications.email_outbox where invitation_id=$1",invite.Id))!;
   await Check("Invitation token is protected at rest",async()=>{var body=(string)(await Scalar("select body from gts_notifications.email_outbox where id=$1",inviteEmail))!;Assert(body.StartsWith("dp:v1:")&&!body.Contains(invite.Token));});
   await using(var c=await db.OpenConnectionAsync())await using(var t=await c.BeginTransactionAsync()){await identity.RevokePending(c,t,invite.Id,default);await t.CommitAsync();}
   await Check("Replacing invitation cancels its pending email",async()=>Assert(await queue.Claim(inviteEmail,default)is null&&(string)(await Scalar("select status from gts_notifications.email_outbox where id=$1",inviteEmail))! =="cancelled"));
   var expiredInvite=await identity.Invite(new(prefix+"-expired@example.invalid","candidate",null,null),staffId,default);var expiredEmail=(Guid)(await Scalar("select id from gts_notifications.email_outbox where invitation_id=$1",expiredInvite.Id))!;
   await Exec("update gts_identity.invitations set created_at=now()-interval '3 days',expires_at=now()-interval '1 day' where id=$1",expiredInvite.Id);
   await Check("Expired invitation is cancelled before sending",async()=>Assert(await queue.Claim(expiredEmail,default)is null&&(string)(await Scalar("select status from gts_notifications.email_outbox where id=$1",expiredEmail))! =="cancelled"));
   await Check("Queue failure rolls back enquiry, thread and notifications",async()=>{
    await using var failing=new ServiceCollection().AddSingleton(db).AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>().AddSingleton<IEmailNotifications>(new ThrowingQueue(queue)).BuildServiceProvider();
    var bad=input with {SubmissionKey=Guid.NewGuid()};try{await new EnquiryStore(failing).Submit(bad,default);throw new Exception("Expected failure");}catch(InjectedFailure){}
    Assert((long)(await Scalar("select count(*) from gts_enquiries.enquiries where submission_key=$1",bad.SubmissionKey))! ==0);
   });
   await Check("Attempts for unknown email return null",async()=>Assert(await queue.Attempts(Guid.NewGuid(),default)is null));
   Console.WriteLine($"{passed} notification hosted checks passed; no real provider requests made.");
  }
  finally
  {
   await Exec("delete from gts_notifications.delivery_attempts where outbox_id in(select id from gts_notifications.email_outbox where enquiry_id=$1 or invitation_id in(select id from gts_identity.invitations where invited_by=$2))",enquiryId,staffId);
   await Exec("delete from gts_notifications.email_outbox where enquiry_id=$1 or invitation_id in(select id from gts_identity.invitations where invited_by=$2)",enquiryId,staffId);
   await Exec("delete from gts_identity.invitations where invited_by=$1",staffId);
   foreach(var table in new[]{"internal_notes","messages","participants"})await Exec($"delete from gts_conversations.{table} where thread_id in(select id from gts_conversations.threads where enquiry_id=$1)",enquiryId);
   await Exec("delete from gts_conversations.threads where enquiry_id=$1",enquiryId);await Exec("delete from gts_enquiries.enquiries where id=$1",enquiryId);
   await Exec("delete from gts_identity.memberships where company_id=$1",companyId);await Exec("delete from gts_identity.companies where id=$1",companyId);await Exec("delete from gts_identity.users where id=$1",contactId);
   await Exec("delete from gts_identity.staff where user_id=$1",staffId);await Exec("delete from gts_identity.users where id=$1",staffId);
   Console.WriteLine("Disposable notification fixtures removed.");
  }
 }
}
sealed class InjectedFailure:Exception;
sealed class ThrowingQueue(IEmailNotifications inner):IEmailNotifications
{
 public async Task EnquiryCreated(DbConnection c,DbTransaction t,Guid id,string reference,string email,CancellationToken ct){await inner.EnquiryCreated(c,t,id,reference,email,ct);throw new InjectedFailure();}
 public Task InvitationCreated(DbConnection c,DbTransaction t,Guid id,string e,string u,CancellationToken ct)=>throw new NotSupportedException();
 public Task CancelInvitation(DbConnection c,DbTransaction t,Guid id,CancellationToken ct)=>throw new NotSupportedException();
 public Task StaffReplied(DbConnection c,DbTransaction t,Guid m,Guid e,string r,string a,string b,CancellationToken ct)=>throw new NotSupportedException();
}
