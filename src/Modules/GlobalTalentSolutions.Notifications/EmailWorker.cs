using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Notifications;

public sealed class EmailProcessor(IEmailOutbox outbox,IEmailSender sender,IOptions<EmailOptions> options)
{
    public async Task<bool> ProcessOne(CancellationToken ct)
    {
        if(!options.Value.Ready)return false;
        var item=await outbox.Claim(ct);if(item is null)return false;
        var result=await sender.Send(item,ct);
        await outbox.Complete(item,result,ct);return true;
    }
}
public sealed class EmailWorker(IServiceScopeFactory scopes,IOptions<EmailOptions> options,ILogger<EmailWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Value.Enabled){logger.LogInformation("Email delivery is disabled; notifications remain queued.");return;}
        if(!options.Value.Ready){logger.LogError("Email delivery is enabled but configuration is incomplete; notifications remain queued.");return;}
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();
                var worked=await scope.ServiceProvider.GetRequiredService<EmailProcessor>().ProcessOne(stoppingToken);
                await Task.Delay(worked?TimeSpan.FromSeconds(1):TimeSpan.FromSeconds(5),stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception error)
            {
                logger.LogError("Email worker operation failed with {ExceptionType}; it will retry after a delay.",error.GetType().Name);
                try{await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);}catch(OperationCanceledException){break;}
            }
        }
    }
}
