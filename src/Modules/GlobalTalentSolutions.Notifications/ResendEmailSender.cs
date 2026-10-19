using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Notifications;

public sealed class ResendEmailSender(HttpClient client,IOptions<EmailOptions> options):IEmailSender
{
    public async Task<EmailSendResult> Send(OutboxEmail email,CancellationToken ct)
    {
        if(!options.Value.Ready)return new(false,null,"delivery_disabled_or_unconfigured",false);
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://api.resend.com/emails");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",options.Value.ApiKey);
        request.Headers.Add("Idempotency-Key","gts-email/"+email.Id);
        request.Content=JsonContent.Create(new {from=email.FromEmail,to=new[]{email.Recipient},subject=email.Subject,text=email.Body,reply_to=email.ReplyTo,tags=new[]{new {name="gts_outbox_id",value=email.Id.ToString()}}});
        try
        {
            using var response=await client.SendAsync(request,ct);
            if(response.IsSuccessStatusCode)
            {
                using var body=await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
                if(body is not null&&body.RootElement.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String&&id.GetString() is {Length:>0} value)
                    return new(true,value,"accepted",false);
                return new(false,null,"invalid_success_response",true,Unknown:true);
            }
            var status=(int)response.StatusCode;
            var retry=status is 408 or 409 or 429 || status>=500;
            var after=response.Headers.RetryAfter?.Delta;
            if(after is null&&response.Headers.RetryAfter?.Date is {} date)after=date-DateTimeOffset.UtcNow;
            if(after<TimeSpan.Zero)after=TimeSpan.Zero;
            return new(false,null,"http_"+status,retry,after,Unknown:status>=500||status==408);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(false,null,"provider_timeout",true,Unknown:true);}
        catch(HttpRequestException){return new(false,null,"provider_network_error",true,Unknown:true);}
        catch(JsonException){return new(false,null,"invalid_success_response",true,Unknown:true);}
    }
}
