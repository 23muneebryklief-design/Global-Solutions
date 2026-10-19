using System.Text.Json;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

static class HostedChecks
{
    public static async Task Run()
    {
        var secretPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json");
        using var secrets=JsonDocument.Parse(await File.ReadAllTextAsync(secretPath));
        var settings=new NpgsqlConnectionStringBuilder(secrets.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
        if(settings.Host!="aws-0-eu-west-1.pooler.supabase.com" || settings.Username!="postgres.aqoiglalbsqprvngmqbf") throw new Exception("Hosted tests are restricted to the approved development project.");
        settings.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");
        settings.MaxPoolSize=5;
        await using var source=NpgsqlDataSource.Create(settings.ConnectionString);
        await using var services=new ServiceCollection().AddSingleton(source).AddSingleton<IEnquiryThreadInitializer,ThreadInitializer>().BuildServiceProvider();
        var store=new EnquiryStore(services);
        var directory=new IdentityStore(services,Options.Create(new SupabaseAuthOptions()));
        var prefix="be04-"+Guid.NewGuid().ToString("N");
        var staffId=Guid.NewGuid();
        var keys=new List<Guid>();
        int passed=0;
        async Task Exec(string sql,params object[] values)
        {
            await using var cmd=source.CreateCommand(sql);foreach(var value in values)cmd.Parameters.AddWithValue(value);await cmd.ExecuteNonQueryAsync();
        }
        void Assert(bool value,string message="Hosted assertion failed"){if(!value)throw new Exception(message);}
        async Task Check(string name,Func<Task> action){await action();passed++;Console.WriteLine("PASS (hosted DB): "+name);}
        NormalizedEnquiry Input(string type,string suffix)
        {
            var key=Guid.NewGuid();keys.Add(key);
            return new(type,prefix+suffix,prefix+"@example.invalid",null,type=="business"?"Test Company":null,"GB",type=="business"?"Remote staffing":"Office assistant","Disposable enquiry integration test.",key,"v1");
        }
        async Task<EnquiryRecord> Find(string reference)
        {
            var page=await store.List(new(Search:reference),default);
            return page.Items.Single(i=>i.Reference==reference);
        }
        try
        {
            await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,'BE04 disposable staff',$2,'staff')",staffId,prefix+"-staff@example.invalid");
            await Exec("insert into gts_identity.staff(user_id,role) values($1,'staff')",staffId);
            var business=Input("business","-business");var candidate=Input("candidate","-candidate");
            var first=await store.Submit(business,default);var second=await store.Submit(candidate,default);
            var a=await Find(first.Reference);var b=await Find(second.Reference);
            await Check("Both enquiry types persist with unique references",()=>{Assert(first.Created&&second.Created&&first.Reference!=second.Reference);return Task.CompletedTask;});
            await Check("New enquiries start unassigned with server timestamps and consent",()=>
            {Assert(a.Status=="new"&&a.AssignedStaffId is null&&a.ConsentAt<=DateTimeOffset.UtcNow&&a.PrivacyNoticeVersion=="v1");return Task.CompletedTask;});
            await Check("Staff can retrieve full enquiry detail",async()=>Assert((await store.Get(a.Id,default))?.Message==business.Message));
            await Check("Unknown enquiry returns null",async()=>Assert(await store.Get(Guid.NewGuid(),default)is null));
            await Check("Exact retry returns original reference without duplicate",async()=>
            {
                var retry=await store.Submit(business,default);Assert(!retry.Created&&retry.Reference==first.Reference);
                Assert((await store.List(new(Search:business.Name),default)).TotalCount==1);
            });
            await Check("Retry does not change the enquiry concurrency timestamp",async()=>Assert((await store.Get(a.Id,default))!.UpdatedAt==a.UpdatedAt));
            await Check("Reusing retry key for different content is rejected",async()=>
            {
                try{await store.Submit(business with {Message="Different message on same key"},default);throw new Exception("Conflicting retry accepted");}
                catch(EnquiryConflictException){}
            });
            await Check("Concurrent retries create exactly one enquiry",async()=>
            {
                var concurrent=Input("business","-concurrent");
                var result=await Task.WhenAll(store.Submit(concurrent,default),store.Submit(concurrent,default));
                Assert(result.Count(r=>r.Created)==1&&result.Select(r=>r.Reference).Distinct().Count()==1);
            });
            await Check("Business/candidate filters separate submissions",async()=>
            {
                var results=await store.List(new(EnquiryType:"candidate",Search:prefix),default);
                Assert(results.TotalCount==1&&results.Items.Single().Id==b.Id);
            });
            await Check("Inbox pagination preserves total on later pages",async()=>
            {
                var firstPage=await store.List(new(Search:prefix,PageSize:1),default);
                var next=await store.List(new(Search:prefix,Page:2,PageSize:1),default);
                Assert(firstPage.TotalCount==next.TotalCount&&firstPage.TotalCount==3&&firstPage.Items.Single().Id!=next.Items.Single().Id);
            });
            await Check("Out-of-range page is empty but retains matching count",async()=>
            {
                var result=await store.List(new(Search:prefix,Page:10,PageSize:1),default);Assert(result.Items.Count==0&&result.TotalCount==3);
            });
            await Check("Search treats SQL and wildcard characters literally",async()=>
            {
                Assert((await store.List(new(Search:prefix+"%"),default)).TotalCount==0);
                Assert((await store.List(new(Search:prefix+"_"),default)).TotalCount==0);
                Assert((await store.List(new(Search:"' OR 1=1 --"),default)).TotalCount==0);
            });
            await Check("Directory recognises active staff",async()=>Assert(await directory.IsActiveStaff(staffId,default)));
            await Check("Status and assignment update together",async()=>
            {
                var result=await store.Update(a.Id,new(a.UpdatedAt,"in_progress",staffId),default);
                Assert(result.Outcome==UpdateOutcome.Updated&&result.Enquiry?.Status=="in_progress"&&result.Enquiry.AssignedStaffId==staffId);
            });
            await Check("Stale updates are rejected without overwriting",async()=>
            {
                Assert((await store.Update(a.Id,new(a.UpdatedAt,"closed"),default)).Outcome==UpdateOutcome.Conflict);
                Assert((await store.Get(a.Id,default))!.Status=="in_progress");
            });
            await Check("Assignee and unassigned filters work",async()=>
            {
                Assert((await store.List(new(AssignedStaffId:staffId,Search:prefix),default)).TotalCount==1);
                Assert((await store.List(new(Unassigned:true,Search:prefix),default)).TotalCount==2);
            });
            await Check("Assignment can be explicitly cleared",async()=>
            {
                var latest=(await store.Get(a.Id,default))!;
                var result=await store.Update(a.Id,new(latest.UpdatedAt,ClearAssignment:true),default);
                Assert(result.Outcome==UpdateOutcome.Updated&&result.Enquiry?.AssignedStaffId is null&&result.Enquiry?.Status=="in_progress");
            });
            await Check("Two simultaneous edits cannot both overwrite the same version",async()=>
            {
                var latest=(await store.Get(a.Id,default))!;
                var result=await Task.WhenAll(store.Update(a.Id,new(latest.UpdatedAt,"closed"),default),store.Update(a.Id,new(latest.UpdatedAt,"new"),default));
                Assert(result.Count(r=>r.Outcome==UpdateOutcome.Updated)==1&&result.Count(r=>r.Outcome==UpdateOutcome.Conflict)==1);
            });
            await Check("Unknown update returns NotFound",async()=>Assert((await store.Update(Guid.NewGuid(),new(a.UpdatedAt,"closed"),default)).Outcome==UpdateOutcome.NotFound));
            await Check("Directory rejects suspended staff",async()=>
            {
                await Exec("update gts_identity.users set status='suspended' where id=$1",staffId);
                Assert(!await directory.IsActiveStaff(staffId,default));
            });
            Console.WriteLine($"{passed} hosted enquiry checks passed.");
        }
        finally
        {
            await Exec("delete from gts_conversations.messages where thread_id in(select t.id from gts_conversations.threads t join gts_enquiries.enquiries e on e.id=t.enquiry_id where e.submission_key=any($1))",keys.ToArray());
            await Exec("delete from gts_conversations.threads where enquiry_id in(select id from gts_enquiries.enquiries where submission_key=any($1))",keys.ToArray());
            await Exec("delete from gts_enquiries.enquiries where submission_key=any($1)",keys.ToArray());
            await Exec("delete from gts_identity.staff where user_id=$1",staffId);
            await Exec("delete from gts_identity.users where id=$1",staffId);
            Console.WriteLine("Disposable enquiry fixtures removed.");
        }
    }
}
