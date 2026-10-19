using System.Net.Mail;
using System.Text.Json;
using Npgsql;

try
{
    var email=args.Length==2 && args[0]=="--email"?args[1].Trim().ToLowerInvariant():null;
    if(email is null || !MailAddress.TryCreate(email,out _)) throw new InvalidOperationException("Usage: --email your-verified-admin-email");
    var root=Directory.GetCurrentDirectory();
    if(!Directory.Exists(Path.Combine(root,"src","GlobalTalentSolutions.Api"))) throw new InvalidOperationException("Run from the repository root.");
    var secretPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft","UserSecrets","global-talent-solutions-backend","secrets.json");
    using var secrets=JsonDocument.Parse(await File.ReadAllTextAsync(secretPath));
    var settings=new NpgsqlConnectionStringBuilder(secrets.RootElement.GetProperty("ConnectionStrings:Supabase").GetString()!);
    settings.RootCertificate=Path.Combine(root,"src","GlobalTalentSolutions.Api","Certificates","supabase-ca.crt");
    settings.SslMode=SslMode.VerifyFull;
    await using var connection=new NpgsqlConnection(settings.ConnectionString);
    await connection.OpenAsync();
    await using var tx=await connection.BeginTransactionAsync();
    await using(var gate=new NpgsqlCommand("select pg_advisory_xact_lock(7340022)",connection,tx)) await gate.ExecuteNonQueryAsync();
    Guid authId;
    await using(var lookup=new NpgsqlCommand("select id,email_confirmed_at from auth.users where lower(email)=$1",connection,tx))
    {
        lookup.Parameters.AddWithValue(email);
        await using var row=await lookup.ExecuteReaderAsync();
        if(!await row.ReadAsync() || row.IsDBNull(1)) throw new InvalidOperationException("Create and confirm the Supabase Auth user first.");
        authId=row.GetGuid(0);
    }
    await using(var existing=new NpgsqlCommand("select u.auth_user_id from gts_identity.staff s join gts_identity.users u on u.id=s.user_id where s.role='admin'",connection,tx))
    await using(var rows=await existing.ExecuteReaderAsync())
    {
        while(await rows.ReadAsync())
            if(rows.IsDBNull(0)||rows.GetGuid(0)!=authId) throw new InvalidOperationException("An administrator already exists. This tool only bootstraps the initial administrator.");
    }
    Guid appId;
    await using(var lookup=new NpgsqlCommand("select id,account_type,status from gts_identity.users where auth_user_id=$1",connection,tx))
    {
        lookup.Parameters.AddWithValue(authId);
        await using var rows=await lookup.ExecuteReaderAsync();
        if(await rows.ReadAsync())
        {
            if(rows.GetString(1)!="staff" || rows.GetString(2)!="active") throw new InvalidOperationException("Existing account cannot be automatically promoted.");
            appId=rows.GetGuid(0);
        }
        else appId=Guid.Empty;
    }
    if(appId==Guid.Empty)
    {
        await using var create=new NpgsqlCommand("insert into gts_identity.users(auth_user_id,display_name,email,account_type) values($1,'Administrator',$2,'staff') returning id",connection,tx);
        create.Parameters.AddWithValue(authId);create.Parameters.AddWithValue(email);
        appId=(Guid)(await create.ExecuteScalarAsync())!;
    }
    await using(var staff=new NpgsqlCommand("insert into gts_identity.staff(user_id,role) values($1,'admin') on conflict(user_id) do update set role='admin'",connection,tx))
    {staff.Parameters.AddWithValue(appId);await staff.ExecuteNonQueryAsync();}
    await tx.CommitAsync();
    Console.WriteLine("Initial administrator provisioned for "+email+". No password was accessed.");
}
catch(InvalidOperationException e) {Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
catch(PostgresException e) {Console.Error.WriteLine("Database setup failed: SQLSTATE "+e.SqlState);Environment.ExitCode=1;}
catch(Exception e) {Console.Error.WriteLine("Admin setup failed ("+e.GetType().Name+"). Credentials were not logged.");Environment.ExitCode=1;}
