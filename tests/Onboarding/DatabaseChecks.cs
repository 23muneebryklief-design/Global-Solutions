using System.Security.Claims;
using System.Text.Json;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

static class DatabaseChecks
{
 public static async Task Run()
 {
  var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json");
  using var secret=JsonDocument.Parse(await File.ReadAllTextAsync(path));var config=new NpgsqlConnectionStringBuilder(secret.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
  if(config.Host!="aws-0-eu-west-1.pooler.supabase.com"||config.Username!="postgres.aqoiglalbsqprvngmqbf")throw new Exception("Approved development database required.");
  config.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");config.MaxPoolSize=10;
  await using var db=NpgsqlDataSource.Create(config.ConnectionString);
  var collection=new ServiceCollection().AddSingleton(db).AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>();
  collection.AddSingleton<IdentityStore>(s=>new(s,Options.Create(new SupabaseAuthOptions())));
  collection.AddSingleton<IOnboardingInvitations>(s=>s.GetRequiredService<IdentityStore>());collection.AddSingleton<ICompanyMemberships>(s=>s.GetRequiredService<IdentityStore>());
  collection.AddSingleton<ICompanyAccess>(s=>s.GetRequiredService<IdentityStore>());collection.AddSingleton<IStaffDirectory>(s=>s.GetRequiredService<IdentityStore>());
  collection.AddSingleton<OnboardingStore>();collection.AddSingleton<IInvitationAcceptanceLinker>(s=>s.GetRequiredService<OnboardingStore>());
  collection.AddSingleton<IPortalEnquiries>(s=>s.GetRequiredService<OnboardingStore>());collection.AddSingleton<EnquiryStore>();collection.AddSingleton<IEnquiryConversationAccess>(s=>s.GetRequiredService<EnquiryStore>());
  collection.AddSingleton<IConversationStore,ConversationStore>();collection.AddSingleton<IPortalConversationList,PortalConversationList>();
  await using var services=collection.BuildServiceProvider();var identity=services.GetRequiredService<IdentityStore>();var onboarding=services.GetRequiredService<OnboardingStore>();
  var enquiries=services.GetRequiredService<EnquiryStore>();var conversations=services.GetRequiredService<IConversationStore>();var portal=services.GetRequiredService<IPortalConversationList>();
  var staffId=Guid.NewGuid();var clientId=Guid.NewGuid();var candidateId=Guid.NewGuid();var outsiderId=Guid.NewGuid();var companyId=Guid.NewGuid();var otherCompanyId=Guid.NewGuid();
  var userIds=new[]{staffId,clientId,candidateId,outsiderId};var enqIds=new List<Guid>();var authIds=new List<Guid>();var prefix="be06-"+Guid.NewGuid().ToString("N");int passed=0;
  async Task Exec(string sql,params object[] values){await using var cmd=db.CreateCommand(sql);foreach(var v in values)cmd.Parameters.AddWithValue(v);await cmd.ExecuteNonQueryAsync();}
  void Assert(bool value){if(!value)throw new Exception("Onboarding database assertion failed.");}
  async Task Check(string name,Func<Task> run){await run();passed++;Console.WriteLine("PASS (hosted DB): "+name);}
  ClaimsPrincipal User(Guid id)=>new(new ClaimsIdentity([new("app_user_id",id.ToString())],"test"));
  async Task<EnquiryRecord> Enquiry(string type,string label)
  {
   var receipt=await enquiries.Submit(new(type,"Test person",prefix+label+"@example.invalid",null,type=="business"?"Test company":null,"GB","Staffing","Original onboarding enquiry.",Guid.NewGuid(),"v1"),default);
   var item=(await enquiries.List(new(Search:receipt.Reference),default)).Items.Single();enqIds.Add(item.Id);return item;
  }
  async Task Conflict(Func<Task> run){try{await run();throw new Exception("Expected conflict");}catch(OnboardingConflictException){}catch(IdentityConflictException){}}
  async Task AcceptRollback(OnboardingInvitation invitation,string email,Guid enquiryId,Guid? companyId)
  {
   // An uncommitted Auth fixture lets the real acceptance transaction be exercised.
   // Always rollback: no Auth account or session is ever persisted or emailed.
   var authId=Guid.NewGuid();authIds.Add(authId);
   await using var connection=await db.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync();
   await using(var auth=new NpgsqlCommand("insert into auth.users(id) values($1)",connection,tx)){auth.Parameters.AddWithValue(authId);await auth.ExecuteNonQueryAsync();}
   var accepted=await identity.AcceptInTransaction(connection,tx,new(invitation.Token,"Invited person"),authId,email,default);
   await using(var cmd=new NpgsqlCommand("select user_id,company_id from gts_enquiries.enquiries where id=$1",connection,tx))
   {cmd.Parameters.AddWithValue(enquiryId);await using var rows=await cmd.ExecuteReaderAsync();await rows.ReadAsync();Assert(rows.GetGuid(0)==accepted.Id&&(companyId.HasValue?rows.GetGuid(1)==companyId:rows.IsDBNull(1)));}
   await using(var cmd=new NpgsqlCommand("select accepted_by from gts_identity.invitations where id=$1",connection,tx)){cmd.Parameters.AddWithValue(invitation.Id);Assert((Guid)(await cmd.ExecuteScalarAsync())! ==accepted.Id);}
   if(companyId.HasValue)
   {await using var cmd=new NpgsqlCommand("select role from gts_identity.memberships where user_id=$1 and company_id=$2",connection,tx);cmd.Parameters.AddWithValue(accepted.Id);cmd.Parameters.AddWithValue(companyId.Value);Assert((string)(await cmd.ExecuteScalarAsync())! =="owner");}
   await Conflict(()=>identity.AcceptInTransaction(connection,tx,new(invitation.Token,"Repeat"),authId,email,default));
   await tx.RollbackAsync();
   Assert((await enquiries.Get(enquiryId,default))is {} e&&await enquiries.CanAccess(e.Id,User(accepted.Id),default)==false);
   Assert((await identity.FindInvitation(invitation.Token,default))is not null);
  }
  try
  {
   foreach(var id in userIds)await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'Disposable test',$2,$3)",id,prefix+id+"@example.invalid",id==staffId?"staff":id==candidateId?"candidate":"client");
   await Exec("insert into gts_identity.staff(user_id,role) values($1,'staff')",staffId);
   foreach(var id in new[]{companyId,otherCompanyId})await Exec("insert into gts_identity.companies(id,name,country_code) values($1,'Disposable company','GB')",id);
   var business=await Enquiry("business","-business");var candidate=await Enquiry("candidate","-candidate");var other=await Enquiry("business","-other");
   var thread=(await conversations.EnsureForEnquiry(business.Id,default))!;
   await Check("Unknown enquiry is not approved",async()=>Assert(await onboarding.Approve(Guid.NewGuid(),new(companyId,"owner",null),staffId,default)is null));
   await Check("Business requires company and role",()=>Conflict(()=>onboarding.Approve(business.Id,new(null,null,null),staffId,default)));
   await Check("Candidate cannot be attached to company",()=>Conflict(()=>onboarding.Approve(candidate.Id,new(companyId,"owner",null),staffId,default)));
   await Check("Archived company cannot be invited",async()=>{await Exec("update gts_identity.companies set status='archived' where id=$1",otherCompanyId);await Conflict(()=>onboarding.Approve(other.Id,new(otherCompanyId,"owner",null),staffId,default));await Exec("update gts_identity.companies set status='active' where id=$1",otherCompanyId);});
   await Check("Suspended staff cannot approve",async()=>{await Exec("update gts_identity.users set status='suspended' where id=$1",staffId);try{await onboarding.Approve(business.Id,new(companyId,"owner",null),staffId,default);throw new Exception("Expected forbidden");}catch(IdentityForbiddenException){}await Exec("update gts_identity.users set status='active' where id=$1",staffId);Assert(await onboarding.Get(business.Id,default)is null);});
   var first=(await onboarding.Approve(business.Id,new(companyId,"owner",null),staffId,default))!;
   await Check("Approval invitation uses enquiry email and client company",async()=>{var i=await identity.FindInvitation(first.Token,default);Assert(i?.Email==business.Email&&i.AccountType=="client"&&i.CompanyId==companyId);});
   await Check("Approval alone grants no portal ownership",async()=>Assert((await portal.List(User(clientId),1,25,default)).TotalCount==0));
   await Check("Duplicate approval requires explicit replacement",()=>Conflict(()=>onboarding.Approve(business.Id,new(companyId,"owner",null),staffId,default)));
   await Check("Wrong replacement cannot revoke an unrelated invite",()=>Conflict(()=>onboarding.Approve(business.Id,new(companyId,"owner",Guid.NewGuid()),staffId,default)));
   var replacement=(await onboarding.Approve(business.Id,new(companyId,"owner",first.Id),staffId,default))!;
   await Check("Replacing invitation revokes old token",async()=>Assert(await identity.FindInvitation(first.Token,default)is null&&(await identity.FindInvitation(replacement.Token,default))is not null));
   await Check("Admin can inspect latest invitation lifecycle",async()=>{var state=await onboarding.Get(business.Id,default);Assert(state?.InvitationId==replacement.Id&&state.Invitation?.AcceptedAt is null&&state.Invitation?.RevokedAt is null);});
   await Check("Wrong verified email cannot accept",async()=>{try{await identity.Accept(new(replacement.Token,"Wrong email"),Guid.NewGuid(),"wrong@example.invalid",default);throw new Exception("Expected rejection");}catch(IdentityConflictException){}});
   await Check("Client acceptance links account and membership atomically",()=>AcceptRollback(replacement,business.Email,business.Id,companyId));
   var candidateInvite=(await onboarding.Approve(candidate.Id,new(null,null,null),staffId,default))!;
   await Check("Candidate acceptance links individual account atomically",()=>AcceptRollback(candidateInvite,candidate.Email,candidate.Id,null));
   await Check("Replacement race allows only one winner",async()=>{var results=await Task.WhenAll(Enumerable.Range(0,2).Select(async _=>{try{return await onboarding.Approve(candidate.Id,new(null,null,candidateInvite.Id),staffId,default);}catch(OnboardingConflictException){return null;}}));Assert(results.Count(r=>r is not null)==1);});
   // Committed application-only fixtures exercise portal queries without Auth users.
   await Exec("update gts_enquiries.enquiries set user_id=$2,company_id=$3 where id=$1",business.Id,clientId,companyId);
   await Exec("update gts_enquiries.enquiries set user_id=$2 where id=$1",candidate.Id,candidateId);
   await Exec("update gts_enquiries.enquiries set company_id=$2 where id=$1",other.Id,otherCompanyId);
   await Exec("insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'owner'),($3,$4,'owner')",companyId,clientId,otherCompanyId,outsiderId);
   await Check("Already linked enquiry cannot be reapproved",()=>Conflict(()=>onboarding.Approve(business.Id,new(companyId,"owner",replacement.Id),staffId,default)));
   await Check("Company portal contains only own conversation",async()=>{var page=await portal.List(User(clientId),1,25,default);Assert(page.TotalCount==1&&page.Items.Single().Thread.Id==thread.Id&&page.Items[0].Enquiry.Id==business.Id);});
   await Check("Candidate portal contains only own conversation",async()=>{var page=await portal.List(User(candidateId),1,25,default);Assert(page.TotalCount==1&&page.Items.Single().Enquiry.Id==candidate.Id);});
   await Check("Outsider portal cannot list other company history",async()=>{var page=await portal.List(User(outsiderId),1,25,default);Assert(page.TotalCount==1&&page.Items.Single().Enquiry.Id==other.Id);});
   await Check("Pagination keeps total beyond last page",async()=>{var page=await portal.List(User(clientId),2,1,default);Assert(page.TotalCount==1&&page.Items.Count==0);});
   await Check("Conversation retains original submission",async()=>{var p=await conversations.Messages(thread.Id,1,50,default);Assert(p.TotalCount==1&&p.Items[0].Body==business.Message&&p.Items[0].CreatedAt==business.CreatedAt);});
   await Exec("insert into gts_conversations.participants(thread_id,user_id,revoked_at) values($1,$2,now())",thread.Id,clientId);
   await Check("Participant revocation removes portal list entry",async()=>Assert((await portal.List(User(clientId),1,25,default)).TotalCount==0));
   await Exec("delete from gts_conversations.participants where thread_id=$1",thread.Id);
   await Exec("update gts_identity.memberships set status='revoked' where user_id=$1",clientId);
   await Check("Membership revocation removes portal list entry",async()=>Assert((await portal.List(User(clientId),1,25,default)).TotalCount==0));
   await Exec("update gts_identity.memberships set status='active' where user_id=$1",clientId);
   await Exec("update gts_identity.companies set status='archived' where id=$1",companyId);
   await Check("Archived company removes portal list entry",async()=>Assert((await portal.List(User(clientId),1,25,default)).TotalCount==0));
   await Check("Acceptance tests persist no Auth fixtures",async()=>{await using var cmd=db.CreateCommand("select count(*) from auth.users where id=any($1)");cmd.Parameters.AddWithValue(authIds.ToArray());Assert((long)(await cmd.ExecuteScalarAsync())! ==0);});
   Console.WriteLine($"{passed} onboarding hosted checks passed.");
  }
  finally
  {
   foreach(var id in enqIds){await Exec("delete from gts_enquiries.onboarding where enquiry_id=$1",id);foreach(var table in new[]{"reply_routes","internal_notes","messages","participants"})await Exec($"delete from gts_conversations.{table} where thread_id in(select id from gts_conversations.threads where enquiry_id=$1)",id);await Exec("delete from gts_conversations.threads where enquiry_id=$1",id);await Exec("delete from gts_enquiries.enquiries where id=$1",id);}
   await Exec("delete from gts_identity.invitations where invited_by=$1",staffId);foreach(var id in new[]{companyId,otherCompanyId}){await Exec("delete from gts_identity.memberships where company_id=$1",id);await Exec("delete from gts_identity.companies where id=$1",id);}
   await Exec("delete from gts_identity.staff where user_id=$1",staffId);foreach(var id in userIds)await Exec("delete from gts_identity.users where id=$1",id);
   Console.WriteLine("Disposable onboarding fixtures removed; no Auth accounts retained.");
  }
 }
}
