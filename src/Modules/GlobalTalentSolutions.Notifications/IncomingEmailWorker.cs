using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Notifications;

public sealed class IncomingEmailProcessor(IIncomingEmailStore store,IReceivedEmailProvider provider)
{
    public async Task<bool> ProcessOne(CancellationToken ct)
    {
        var item=await store.Claim(ct);if(item is null)return false;
        try
        {
            var content=item.Content;
            if(item.Type=="email.received"&&content is null)
            {if(item.EmailId is null)throw new ReceivedEmailException("received_email_id_missing",false);content=await provider.Get(item.EmailId,ct);}
            await store.Finish(item,content,ct);
        }
        catch(ReceivedEmailException error){await store.Fail(item,error.Code,error.Retryable,ct);}
        return true;
    }
}
public sealed class IncomingEmailWorker(IServiceScopeFactory scopes,IOptions<IncomingEmailOptions> options,IOptions<EmailOptions> emailOptions,ILogger<IncomingEmailWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config=options.Value;
        if(!config.Enabled){logger.LogInformation("Incoming email processing is disabled.");return;}
        if(!config.DomainValid||!WebhookSignature.Configured(config.WebhookSecret)||string.IsNullOrWhiteSpace(emailOptions.Value.ApiKey))
        {logger.LogError("Incoming email processing is enabled but provider/domain/signing configuration is incomplete; events remain queued.");return;}
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();var worked=await scope.ServiceProvider.GetRequiredService<IncomingEmailProcessor>().ProcessOne(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(worked?1:5),stoppingToken);
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception error)
            {
                logger.LogError("Incoming email operation failed with {ExceptionType}; processing will retry.",error.GetType().Name);
                try{await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);}catch(OperationCanceledException){break;}
            }
        }
    }
}
