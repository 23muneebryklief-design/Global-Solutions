using System.Net.Http.Headers;
using System.Text.Json;
using GlobalTalentSolutions.SharedKernel;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Notifications;

public sealed class ReceivedEmailProvider(HttpClient client,IOptions<EmailOptions> emailOptions):IReceivedEmailProvider
{
    public async Task<IncomingEmail> Get(string id,CancellationToken ct)
    {
        if(!Guid.TryParse(id,out var parsed)||string.IsNullOrWhiteSpace(emailOptions.Value.ApiKey))throw new ReceivedEmailException("provider_unconfigured",true);
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.resend.com/emails/receiving/"+parsed+"?html_format=cid");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",emailOptions.Value.ApiKey);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(!response.IsSuccessStatusCode)throw new ReceivedEmailException("receive_http_"+(int)response.StatusCode,(int)response.StatusCode is 404 or 408 or 429 || (int)response.StatusCode>=500);
            // Bound the response even for chunked transfer; do not download raw MIME or attachments.
            if(response.Content.Headers.ContentLength>2*1024*1024)throw new ReceivedEmailException("received_content_too_large",false);
            await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var buffer=new MemoryStream();var bytes=new byte[8192];int read;
            while((read=await stream.ReadAsync(bytes,timeout.Token))>0){if(buffer.Length+read>2*1024*1024)throw new ReceivedEmailException("received_content_too_large",false);buffer.Write(bytes,0,read);}
            using var doc=JsonDocument.Parse(buffer.ToArray());var root=doc.RootElement;
            if(!Guid.TryParse(root.GetProperty("id").GetString(),out var receivedId)||receivedId!=parsed)throw new ReceivedEmailException("received_id_mismatch",false);
            var sender=IncomingEmailParsing.Address(root.GetProperty("from").GetString()??"")??throw new ReceivedEmailException("invalid_received_sender",false);
            var recipients=root.GetProperty("to").EnumerateArray().Take(101).Select(r=>IncomingEmailParsing.Address(r.GetString()??"")).Where(r=>r is not null).Cast<string>().ToArray();
            if(recipients.Length>100)throw new ReceivedEmailException("too_many_recipients",false);
            var subject=root.TryGetProperty("subject",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString()??"":"";
            var text=root.TryGetProperty("text",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null;
            var html=root.TryGetProperty("html",out var h)&&h.ValueKind==JsonValueKind.String&&!string.IsNullOrEmpty(h.GetString());
            var attachments=root.TryGetProperty("attachments",out var a)&&a.ValueKind==JsonValueKind.Array?a.GetArrayLength():0;
            bool authenticated=false;
            if(root.TryGetProperty("authentication",out var auth)&&auth.ValueKind==JsonValueKind.Object)
            {
                string? Field(string name)=>auth.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
                // Only the provider's computed result is trusted, never arbitrary message headers.
                authenticated=Field("dmarc")=="pass"&&(Field("dkim")=="pass"||Field("spf")=="pass");
            }
            return new(id,sender,recipients,subject.Length>500?subject[..500]:subject,text,html,attachments,authenticated);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new ReceivedEmailException("receive_timeout",true);}
        catch(HttpRequestException){throw new ReceivedEmailException("receive_network_error",true);}
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException){throw new ReceivedEmailException("invalid_received_response",false);}
    }
}
