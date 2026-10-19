using System.Security.Claims;
using System.Text.Json;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

static class DatabaseChecks
{
    public static async Task Run()
    {
        var secretPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json");
        using var secrets=JsonDocument.Parse(await File.ReadAllTextAsync(secretPath));
        var settings=new NpgsqlConnectionStringBuilder(secrets.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
        if(settings.Username!="postgres.aqoiglalbsqprvngmqbf" || settings.Host!="aws-0-eu-west-1.pooler.supabase.com")
            throw new Exception("Database checks are restricted to the approved development project.");
        settings.RootCertificate=Path.GetFullPath("src/GlobalTalentSolutions.Api/Certificates/supabase-ca.crt");
        settings.MaxPoolSize=3;
        await using var source=NpgsqlDataSource.Create(settings.ConnectionString);
        var services=new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        var store=new IdentityStore(services,Options.Create(new SupabaseAuthOptions {FrontendUrl="http://localhost:5173"}));
        var staff=Guid.NewGuid(); var owner=Guid.NewGuid(); var member=Guid.NewGuid(); var outsider=Guid.NewGuid();
        var company=Guid.NewGuid(); var otherCompany=Guid.NewGuid();
        var ids=new[]{staff,owner,member,outsider}; var companies=new[]{company,otherCompany};
        var prefix="be03-"+Guid.NewGuid().ToString("N");
        var passed=0;
        async Task Exec(string sql,params object[] values)
        {
            await using var cmd=source.CreateCommand(sql);
            foreach(var value in values) cmd.Parameters.AddWithValue(value);
            await cmd.ExecuteNonQueryAsync();
        }
        ClaimsPrincipal User(Guid id)=>new(new ClaimsIdentity(new[]{new Claim("app_user_id",id.ToString()),new Claim(ClaimTypes.NameIdentifier,Guid.NewGuid().ToString())},"test"));
        async Task Check(string name,Func<Task> action) {await action();passed++;Console.WriteLine("PASS (hosted DB): "+name);}
        void Assert(bool value) {if(!value) throw new Exception("Database assertion failed.");}
        try
        {
            foreach(var id in ids)
                await Exec("insert into gts_identity.users(id,display_name,email,account_type) values($1,$2,$3,$4)",id,"BE03 disposable fixture",prefix+id.ToString("N")+"@example.invalid",id==staff?"staff":"client");
            await Exec("insert into gts_identity.staff(user_id,role) values($1,'admin')",staff);
            foreach(var id in companies)
                await Exec("insert into gts_identity.companies(id,name,country_code) values($1,$2,'US')",id,prefix);
            await Exec("insert into gts_identity.memberships(company_id,user_id,role) values($1,$2,'owner'),($1,$3,'member'),($4,$5,'owner')",company,owner,member,otherCompany,outsider);
            await Check("Owner has company management access",async()=>Assert(await store.CanAccess(company,User(owner),true,default)));
            await Check("Member cannot manage company access",async()=>Assert(!await store.CanAccess(company,User(member),true,default)));
            await Check("Member can read own company",async()=>Assert(await store.CanAccess(company,User(member),false,default)));
            await Check("Other company is inaccessible",async()=>Assert(!await store.CanAccess(otherCompany,User(owner),false,default)));
            await Check("Company listing isolates memberships",async()=>
            {
                var result=await store.Companies(User(owner),default);Assert(result.Count==1&&result[0].Id==company);
            });
            await Check("Last owner cannot be revoked",async()=>
            {
                try {await store.UpdateMember(company,owner,new("owner","revoked"),owner,default);throw new Exception("Owner removal succeeded.");}
                catch(IdentityConflictException) {}
            });
            await Check("Member cannot bypass the endpoint and update roles",async()=>
            {
                try {await store.UpdateMember(company,member,new("owner","active"),member,default);throw new Exception("Privilege escalation succeeded.");}
                catch(IdentityForbiddenException) {}
            });
            await Check("Owner can promote a member",async()=>Assert(await store.UpdateMember(company,member,new("owner","active"),owner,default)));
            await Check("Former owner can be revoked when another owner remains",async()=>Assert(await store.UpdateMember(company,owner,new("member","revoked"),member,default)));
            await Check("Revoked membership loses read access",async()=>Assert(!await store.CanAccess(company,User(owner),false,default)));
            await Check("Revoked owner cannot update memberships",async()=>
            {
                try {await store.UpdateMember(company,member,new("member","active"),owner,default);throw new Exception("Revoked owner changed roles.");}
                catch(IdentityForbiddenException) {}
            });
            await Check("Revoked session ID fails the actual auth.sessions query",async()=>
                Assert(!await store.SessionActive(new(Guid.NewGuid(),"none@example.invalid",Guid.NewGuid()),default)));
            var invite=await store.Invite(new(prefix+"@example.invalid","client",company,"member"),staff,default);
            await Check("Invitation is retrievable with its secret token",async()=>
                Assert((await store.FindInvitation(invite.Token,default))?.Id==invite.Id));
            await Check("Invitation stores a digest rather than usable token",async()=>
            {
                await using var cmd=source.CreateCommand("select token_hash from gts_identity.invitations where id=$1");
                cmd.Parameters.AddWithValue(invite.Id);
                Assert((string)(await cmd.ExecuteScalarAsync())! != invite.Token);
            });
            await Check("Expired invitation is rejected by real store",async()=>
            {
                await Exec("update gts_identity.invitations set created_at=now()-interval '2 days',expires_at=now()-interval '1 day' where id=$1",invite.Id);
                Assert(await store.FindInvitation(invite.Token,default)is null);
            });
            await Check("Acceptance rejects email mismatch without creating an account",async()=>
            {
                var valid=await store.Invite(new(prefix+"-candidate@example.invalid","candidate",null,null),staff,default);
                try {await store.Accept(new(valid.Token,"Wrong email"),Guid.NewGuid(),"wrong@example.invalid",default);throw new Exception("Mismatched email accepted.");}
                catch(IdentityConflictException) {}
            });
            Console.WriteLine(passed+" hosted database identity checks passed.");
        }
        finally
        {
            // Remove only explicitly created fixtures; never touch Supabase Auth users.
            await Exec("delete from gts_identity.invitations where invited_by=$1",staff);
            await Exec("delete from gts_identity.memberships where company_id=any($1)",companies);
            await Exec("delete from gts_identity.companies where id=any($1)",companies);
            await Exec("delete from gts_identity.staff where user_id=$1",staff);
            await Exec("delete from gts_identity.users where id=any($1)",ids);
            Console.WriteLine("Disposable identity fixtures removed.");
            services.Dispose();
        }
    }
}
