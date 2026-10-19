using System.Security.Cryptography;
using System.Text;
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
  using var secrets=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json")));
  var config=new NpgsqlConnectionStringBuilder(secrets.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
  if(config.Host!="aws-0-eu-west-1.pooler.supabase.com"||config.Username!="postgres.aqoiglalbsqprvngmqbf")throw new Exception("Approved development database required.");
  config.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");config.MaxPoolSize=10;
  await using var db=NpgsqlDataSource.Create(config.ConnectionString);var prefix="be08-"+Guid.NewGuid().ToString("N");var staffId=Guid.NewGuid();var contactId=Guid.NewGuid();var enqIds=new List<Guid>();var eventIds=new List<Guid>();
  var incoming=new IncomingEmailOptions{Enabled=true,Domain="incoming.example.invalid"};var email=new EmailOptions{Enabled=true,ApiKey="fake-no-network",FromEmail="sender@example.invalid",ReplyTo="reply@example.invalid",TeamEmail=prefix+"-team@example.invalid"};
  var services=new ServiceCollection().AddSingleton(db).AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider()).AddSingleton<IOptions<EmailOptions>>(Options.Create(email)).AddSingleton<IOptions<IncomingEmailOptions>>(Options.Create(incoming));
  services.AddSingleton<IdentityStore>(s=>new(s,Options.Create(new SupabaseAuthOptions())));services.AddSingleton<IStaffDirectory>(s=>s.GetRequiredService<IdentityStore>());services.AddSingleton<IEmailContact>(s=>s.GetRequiredService<IdentityStore>());
  services.AddSingleton<EnquiryStore>();services.AddSingleton<IEnquiryMailDetails>(s=>s.GetRequiredService<EnquiryStore>());services.AddSingleton<IEnquiryConversationAccess>(s=>s.GetRequiredService<EnquiryStore>());
  services.AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>();services.AddSingleton<IIncomingConversations,IncomingConversations>();services.AddSingleton<IConversationStore,ConversationStore>();
  services.AddSingleton<EmailQueue>();services.AddSingleton<IEmailNotifications>(s=>s.GetRequiredService<EmailQueue>());services.AddSingleton<IncomingEmailStore>();
  await using var provider=services.BuildServiceProvider();var store=provider.GetRequiredService<IncomingEmailStore>();var queue=provider.GetRequiredService<EmailQueue>();var enquiries=provider.GetRequiredService<EnquiryStore>();var conversations=provider.GetRequiredService<IConversationStore>();int passed=0;
  async Task<object?> Scalar(string sql,params object[] values){await using var cmd=db.CreateCommand(sql);foreach(var v in values)cmd.Parameters.AddWithValue(v);return await cmd.ExecuteScalarAsync();}
  async Task Exec(string sql,params object[] values){await Scalar(sql,values);}
  void Assert(bool value){if(!value)throw new Exception("Incoming email database assertion failed.");}
  async Task Check(string name,Func<Task> action){await action();passed++;Console.WriteLine("PASS (hosted DB): "+name);}
  async Task Conflict(Func<Task> action){try{await action();throw new Exception("Expected conflict");}catch(EmailEventConflictException){}}
  async Task<EnquiryRecord> Enquiry(string suffix)
  {
   var receipt=await enquiries.Submit(new("business","Test contact",prefix+suffix+"@example.invalid",null,"Test company","GB","Staffing","Original email integration enquiry.",Guid.NewGuid(),"v1"),default);
   var item=(await enquiries.List(new(Search:receipt.Reference),default)).Items.Single();enqIds.Add(item.Id);return item;
  }
  VerifiedEmailEvent Event(string type,string emailId,DateTimeOffset? at=null,Guid? outbox=null,string? recipient=null)=>IncomingEmailParsing.Parse(prefix+"-"+Guid.NewGuid().ToString("N"),Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {type,created_at=at??DateTimeOffset.UtcNow,data=new {email_id=emailId,to=new[]{recipient??prefix+"-first@example.invalid"},tags=outbox.HasValue?new Dictionary<string,string>{{"gts_outbox_id",outbox.Value.ToString()}}:new()}})));
  async Task<Guid> Receive(VerifiedEmailEvent input){await store.Receive(input,default);var id=(Guid)(await Scalar("select id from gts_notifications.webhook_events where provider='resend' and provider_event_id=$1",input.Id))!;eventIds.Add(id);return id;}
  async Task<ClaimedEmailEvent> Claim(Guid id)=>(await store.Claim(id,default))??throw new Exception("Fixture event not claimable.");
  async Task<(Guid Id,EmailReviewDetail Detail)> Process(IncomingEmail content)
  {var id=await Receive(Event("email.received",content.ProviderId));var item=await Claim(id);Assert(await store.Finish(item,content,default));return (id,(await store.Detail(id,default))!);}
  try
  {
   await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'Disposable inbound staff',$2,'staff')",staffId,prefix+"-staff@example.invalid");await Exec("insert into gts_identity.staff(user_id,role) values($1,'staff')",staffId);
   var first=await Enquiry("-first");var other=await Enquiry("-other");var firstThread=(await conversations.EnsureForEnquiry(first.Id,default))!;var otherThread=(await conversations.EnsureForEnquiry(other.Id,default))!;
   var confirmation=(Guid)(await Scalar("select id from gts_notifications.email_outbox where enquiry_id=$1 and kind='enquiry_confirmation'",first.Id))!;
   var otherConfirmation=(Guid)(await Scalar("select id from gts_notifications.email_outbox where enquiry_id=$1 and kind='enquiry_confirmation'",other.Id))!;
   var outgoing=(await queue.Claim(confirmation,default))!;var otherOutgoing=(await queue.Claim(otherConfirmation,default))!;var route=outgoing.ReplyTo;var otherRoute=otherOutgoing.ReplyTo;
   await Check("Confirmation uses a private conversation reply address",()=>{Assert(route.StartsWith("r-")&&route.EndsWith("@"+incoming.Domain)&&route!=otherRoute);return Task.CompletedTask;});
   await Check("Reply capability stored only hashed/encrypted",async()=>{var stored=(string)(await Scalar("select reply_to from gts_notifications.email_outbox where id=$1",confirmation))!;Assert(stored.StartsWith("dp:v1:")&&!stored.Contains(route));var token=route.Split('@')[0][2..];var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));Assert((long)(await Scalar("select count(*) from gts_conversations.reply_routes where thread_id=$1 and token_hash=$2",firstThread.Id,hash))! ==1);});
   var providerId=Guid.NewGuid().ToString();await queue.Complete(outgoing,new(true,providerId,"accepted",false),default);await queue.Complete(otherOutgoing,new(true,Guid.NewGuid().ToString(),"accepted",false),default);
   var content=new IncomingEmail(Guid.NewGuid().ToString(),first.Email,[route],"Re: any subject","A genuine email reply.",false,0,true);
   var original=Event("email.received",content.ProviderId);var originalId=await Receive(original);
   await Check("Same webhook redelivery is acknowledged once",async()=>Assert(!await store.Receive(original,default)));
   await Check("Changed payload under same event ID conflicts",()=>Conflict(()=>store.Receive(original with {Hash=new string('f',64)},default)));
   var claimed=await Claim(originalId);await store.Finish(claimed,content,default);
   await Check("Verified reply joins designated thread",async()=>{var detail=(await store.Detail(originalId,default))!;Assert(detail.Event.Status=="processed"&&detail.Event.ThreadId==firstThread.Id);var history=await conversations.Messages(firstThread.Id,1,50,default);Assert(history.TotalCount==2&&history.Items[0].Channel=="form"&&history.Items[1].Body==content.Text&&history.Items[1].Channel=="email");});
   await Check("Other conversation remains untouched",async()=>Assert((await conversations.Messages(otherThread.Id,1,50,default)).TotalCount==1));
   await Check("Separate webhook IDs cannot duplicate one provider email",async()=>{await Process(content);Assert((await conversations.Messages(firstThread.Id,1,50,default)).TotalCount==2);});
   await Check("Webhook metadata and received content are encrypted",async()=>{var value=(string)(await Scalar("select payload::text||content::text from gts_notifications.webhook_events where id=$1",originalId))!;Assert(!value.Contains(route)&&!value.Contains(content.Text!));});
   IncomingEmail Fresh()=>content with {ProviderId=Guid.NewGuid().ToString()};
   await Check("Unexpected sender is held for review",async()=>Assert((await Process(Fresh() with {Sender="stranger@example.invalid"})).Detail.Event.Reason=="unexpected_sender"));
   var unauth=await Process(Fresh() with {Authenticated=false});
   await Check("Missing authentication never auto-attaches",()=>{Assert(unauth.Detail.Event.Status=="unmatched"&&unauth.Detail.Event.Reason=="sender_authentication_unverified");return Task.CompletedTask;});
   await Check("Subject/reference guessing cannot route email",async()=>Assert((await Process(Fresh() with {Recipients=["inbox@"+incoming.Domain],Subject=first.Reference})).Detail.Event.Reason=="reply_route_missing"));
   await Check("Different receiving domain cannot reuse token",async()=>Assert((await Process(Fresh() with {Recipients=[route.Replace(incoming.Domain,"wrong.example.invalid")]})).Detail.Event.Reason=="reply_route_missing"));
   await Check("Multiple reply routes require review",async()=>Assert((await Process(Fresh() with {Recipients=[route,otherRoute]})).Detail.Event.Reason=="ambiguous_reply_routes"));
   await Check("HTML-only email is held without rendering HTML",async()=>{var result=await Process(Fresh() with {Text=null,HasHtml=true});Assert(result.Detail.Event.Reason=="plain_text_missing"&&result.Detail.HasHtml&&result.Detail.Text is null);});
   await Check("Attachments are held instead of silently dropped",async()=>Assert((await Process(Fresh() with {AttachmentCount=1})).Detail.Event.Reason=="attachments_require_review"));
   await Check("Long bodies are retained privately and preview is bounded",async()=>{var result=await Process(Fresh() with {Text=new string('x',20001)});Assert(result.Detail.Event.Reason=="body_requires_review"&&result.Detail.BodyTruncated&&result.Detail.Text!.Length==20000);});
   await Exec("update gts_conversations.reply_routes set revoked_at=now() where thread_id=$1",firstThread.Id);
   await Check("Revoked route cannot receive messages",async()=>Assert((await Process(Fresh())).Detail.Event.Reason=="reply_route_unavailable"));
   await Exec("update gts_conversations.reply_routes set revoked_at=null,created_at=now()-interval '2 days',expires_at=now()-interval '1 day' where thread_id=$1",firstThread.Id);
   await Check("Expired route cannot receive messages",async()=>Assert((await Process(Fresh())).Detail.Event.Reason=="reply_route_unavailable"));
   await Exec("update gts_conversations.reply_routes set expires_at=now()+interval '90 days' where thread_id=$1",firstThread.Id);
   await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'Disposable contact',$2,'client')",contactId,first.Email);await Exec("update gts_enquiries.enquiries set user_id=$2 where id=$1",first.Id,contactId);
   await Exec("insert into gts_conversations.participants(thread_id,user_id,revoked_at) values($1,$2,now())",firstThread.Id,contactId);
   await Check("Revoked portal participant cannot bypass access by email",async()=>Assert((await Process(Fresh())).Detail.Event.Reason=="participant_revoked"));
   await Exec("delete from gts_conversations.participants where thread_id=$1",firstThread.Id);await Exec("update gts_identity.users set status='suspended' where id=$1",contactId);
   await Check("Suspended contact cannot route a reply",async()=>Assert((await Process(Fresh())).Detail.Event.Reason=="sender_no_longer_authorized"));
   await Exec("update gts_identity.users set status='active',email=$2 where id=$1",contactId,prefix+"-changed@example.invalid");
   await Check("Old mailbox loses routing after verified address changes",async()=>Assert((await Process(Fresh())).Detail.Event.Reason=="sender_no_longer_authorized"));
   await Exec("update gts_identity.users set email=$2 where id=$1",contactId,first.Email);
   var review=new ReviewEmailInput(unauth.Detail.Event.UpdatedAt,"attach",firstThread.Id,"Contact verified independently by staff",null);
   await Check("Staff can explicitly attach a reviewed email",async()=>{Assert(await store.Review(unauth.Id,review,staffId,default));Assert((await store.Detail(unauth.Id,default))!.Event.Status=="processed");});
   await Check("Stale review cannot attach twice",()=>Conflict(()=>store.Review(unauth.Id,review,staffId,default)));
   var ignored=await Process(Fresh() with {Recipients=[]});
   await Check("Staff can ignore unmatched email without creating a message",async()=>{var before=(await conversations.Messages(firstThread.Id,1,50,default)).TotalCount;Assert(await store.Review(ignored.Id,new(ignored.Detail.Event.UpdatedAt,"ignore",null,"Unrelated email confirmed",null),staffId,default));Assert((await conversations.Messages(firstThread.Id,1,50,default)).TotalCount==before);});
   var forbidden=await Process(Fresh() with {Authenticated=false});
   await Check("Nonstaff cannot bypass review endpoint authorization",async()=>{try{await store.Review(forbidden.Id,new(forbidden.Detail.Event.UpdatedAt,"ignore",null,"Invalid actor",null),contactId,default);throw new Exception("Expected denial");}catch(EmailEventForbiddenException){}});
   var wrongTarget=await Process(Fresh() with {Authenticated=false});
   await Check("Invalid review target rolls back review",async()=>{await Conflict(()=>store.Review(wrongTarget.Id,new(wrongTarget.Detail.Event.UpdatedAt,"attach",Guid.NewGuid(),"Invalid target check",null),staffId,default));Assert((await store.Detail(wrongTarget.Id,default))!.Event.Status=="unmatched");});
   await Check("Reviewed duplicate cannot be copied into another conversation",async()=>{var duplicate=await Process(content with {Authenticated=false});await Conflict(()=>store.Review(duplicate.Id,new(duplicate.Detail.Event.UpdatedAt,"attach",otherThread.Id,"Verify cross-thread duplicate protection",null),staffId,default));Assert((await conversations.Messages(otherThread.Id,1,50,default)).TotalCount==1);});
   var crashContent=Fresh();var crashId=await Receive(Event("email.received",crashContent.ProviderId));ClaimedEmailEvent? winner=null;
   await Check("Concurrent workers claim one event once",async()=>{var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>store.Claim(crashId,default)));Assert(results.Count(i=>i is not null)==1);winner=results.Single(i=>i is not null);});
   await Exec("update gts_notifications.webhook_events set locked_until=now()-interval '1 second' where id=$1",crashId);var recovered=await Claim(crashId);
   await Check("Expired event lease is recovered; stale worker cannot finish",async()=>{Assert(recovered.Attempts==2&&recovered.LockToken!=winner!.LockToken);Assert(!await store.Finish(winner!,crashContent,default));Assert(await store.Finish(recovered,crashContent,default));});
   var retryContent=Fresh();var retryId=await Receive(Event("email.received",retryContent.ProviderId));var restricted=new RestrictedStore(store,retryId);var fakeProvider=new FakeReceivedProvider(retryContent){Failure=new ReceivedEmailException("receive_http_429",true)};
   await Check("Provider failure is retried after durable webhook receipt",async()=>{Assert(await new IncomingEmailProcessor(restricted,fakeProvider).ProcessOne(default));Assert((await store.Detail(retryId,default))!.Event.Status=="failed"&&await store.Claim(retryId,default)is null);});
   var retryDetail=(await store.Detail(retryId,default))!;await store.Review(retryId,new(retryDetail.Event.UpdatedAt,"retry",null,"Provider recovered; reprocess safely",null),staffId,default);fakeProvider.Failure=null;
   await Check("Explicit reprocessing safely completes failed inbound event",async()=>{Assert(await new IncomingEmailProcessor(restricted,fakeProvider).ProcessOne(default));Assert((await store.Detail(retryId,default))!.Event.Status=="processed");});
   async Task Delivery(string type,DateTimeOffset? at=null){var id=await Receive(Event(type,providerId,at));await store.Finish(await Claim(id),null,default);}
   await Check("Delivery callback updates acceptance separately",async()=>{await Delivery("email.delivered");Assert((string)(await Scalar("select delivery_status from gts_notifications.email_outbox where id=$1",confirmation))! =="delivered");Assert((string)(await Scalar("select status from gts_notifications.email_outbox where id=$1",confirmation))! =="sent");});
   await Check("Bounce takes precedence despite out-of-order timestamps",async()=>{await Delivery("email.bounced",DateTimeOffset.UtcNow.AddMinutes(-10));await Delivery("email.delivered");Assert((string)(await Scalar("select delivery_status from gts_notifications.email_outbox where id=$1",confirmation))! =="bounced");});
   await Check("Complaint cannot be hidden by a later delivery event",async()=>{await Delivery("email.complained");await Delivery("email.delivered");Assert((string)(await Scalar("select delivery_status from gts_notifications.email_outbox where id=$1",confirmation))! =="complained");});
   var tagged=Guid.NewGuid();await Exec("insert into gts_notifications.email_outbox(id,idempotency_key,kind,recipient_email,subject,body,enquiry_id,status,attempts) values($1,$2,'team_alert',$3,'Fixture','Fixture',$4,'failed',1)",tagged,prefix+"-tagged",first.Email,first.Id);
   await Check("Signed outbox tag correlates unknown provider response",async()=>{var id=await Receive(Event("email.delivered",Guid.NewGuid().ToString(),outbox:tagged,recipient:first.Email));await store.Finish(await Claim(id),null,default);Assert((string)(await Scalar("select delivery_status from gts_notifications.email_outbox where id=$1",tagged))! =="delivered");});
   await Check("Outbox tag with unrelated recipient cannot change delivery state",async()=>{var id=await Receive(Event("email.complained",Guid.NewGuid().ToString(),outbox:tagged,recipient:other.Email));var item=await Claim(id);try{await store.Finish(item,null,default);throw new Exception("Expected mismatch");}catch(ReceivedEmailException e){await store.Fail(item,e.Code,e.Retryable,default);}Assert((string)(await Scalar("select delivery_status from gts_notifications.email_outbox where id=$1",tagged))! =="delivered");});
   await Check("Unknown outbound ID is preserved for safe retry",async()=>{var id=await Receive(Event("email.delivered",Guid.NewGuid().ToString()));var item=await Claim(id);try{await store.Finish(item,null,default);throw new Exception("Expected unknown outbound error");}catch(ReceivedEmailException e){Assert(e.Retryable);await store.Fail(item,e.Code,e.Retryable,default);}Assert((await store.Detail(id,default))!.Event.Reason=="outbound_email_not_recorded");});
   await Check("Unsupported event is retained but not processed as an email",async()=>{var id=await Receive(Event("email.opened",Guid.NewGuid().ToString()));Assert((await store.Detail(id,default))!.Event.Status=="processed"&&await store.Claim(id,default)is null);});
   await Check("Processed events cannot be leased again",async()=>Assert(await store.Claim(originalId,default)is null));
   Console.WriteLine($"{passed} incoming hosted checks passed; no provider requests made.");
  }
  finally
  {
   if(eventIds.Count>0)await Exec("delete from gts_notifications.webhook_events where id=any($1)",eventIds.ToArray());
   foreach(var id in enqIds)
   {
    await Exec("delete from gts_notifications.delivery_attempts where outbox_id in(select id from gts_notifications.email_outbox where enquiry_id=$1)",id);await Exec("delete from gts_notifications.email_outbox where enquiry_id=$1",id);
    foreach(var table in new[]{"reply_routes","internal_notes","messages","participants"})await Exec($"delete from gts_conversations.{table} where thread_id in(select id from gts_conversations.threads where enquiry_id=$1)",id);
    await Exec("delete from gts_conversations.threads where enquiry_id=$1",id);await Exec("delete from gts_enquiries.enquiries where id=$1",id);
   }
   await Exec("delete from gts_identity.staff where user_id=$1",staffId);await Exec("delete from gts_identity.users where id=$1 or id=$2",staffId,contactId);
   Console.WriteLine("Disposable incoming email fixtures removed.");
  }
 }
}
sealed class FakeReceivedProvider(IncomingEmail content):IReceivedEmailProvider
{public ReceivedEmailException? Failure;public Task<IncomingEmail> Get(string id,CancellationToken ct)=>Failure is {} e?Task.FromException<IncomingEmail>(e):Task.FromResult(content);}
sealed class RestrictedStore(IncomingEmailStore store,Guid fixture):IIncomingEmailStore
{
 public Task<bool> Receive(VerifiedEmailEvent e,CancellationToken ct)=>store.Receive(e,ct);
 public Task<ClaimedEmailEvent?> Claim(CancellationToken ct)=>store.Claim(fixture,ct);
 public Task<bool> Finish(ClaimedEmailEvent e,IncomingEmail? content,CancellationToken ct)=>store.Finish(e,content,ct);
 public Task Fail(ClaimedEmailEvent e,string code,bool retryable,CancellationToken ct)=>store.Fail(e,code,retryable,ct);
 public Task<EmailEventPage> List(string? status,int page,int size,CancellationToken ct)=>store.List(status,page,size,ct);
 public Task<EmailReviewDetail?> Detail(Guid id,CancellationToken ct)=>store.Detail(id,ct);
 public Task<bool> Review(Guid id,ReviewEmailInput input,Guid staffId,CancellationToken ct)=>store.Review(id,input,staffId,ct);
}
