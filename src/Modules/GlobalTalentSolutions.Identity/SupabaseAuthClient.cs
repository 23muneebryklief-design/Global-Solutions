using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace GlobalTalentSolutions.Identity;

public interface ISupabaseAuthClient
{
    Task<VerifiedIdentity?> Verify(string token, CancellationToken ct);
    Task<ProviderSession?> Login(LoginInput input, CancellationToken ct);
    Task<ProviderSession?> Refresh(string refreshToken, CancellationToken ct);
    Task Logout(string accessToken, CancellationToken ct);
    Task Register(RegisterInput input, CancellationToken ct);
    Task Recover(string email, CancellationToken ct);
    Task<bool> ChangePassword(string token, string password, CancellationToken ct);
    Task<bool> ChangeEmail(string token, string email, CancellationToken ct);
}
public sealed class SupabaseAuthClient(HttpClient http, IOptions<SupabaseAuthOptions> options) : ISupabaseAuthClient
{
    private readonly SupabaseAuthOptions settings = options.Value;
    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null, string? token = null)
    {
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || string.IsNullOrWhiteSpace(settings.PublishableKey))
            throw new IdentityProviderException();
        var request = new HttpRequestMessage(method, new Uri(url, "/auth/v1/" + path));
        request.Headers.Add("apikey", settings.PublishableKey);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            { response.Dispose(); throw new IdentityProviderException(); }
            return response;
        }
        catch (HttpRequestException) { throw new IdentityProviderException(); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new IdentityProviderException(); }
    }
    public async Task<VerifiedIdentity?> Verify(string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "user", token: token);
        using var response = await Send(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var data = user.RootElement;
        if (!data.TryGetProperty("email_confirmed_at", out var confirmed) || confirmed.ValueKind == JsonValueKind.Null) return null;
        if (!data.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(),out var authId)) return null;
        var email = data.GetProperty("email").GetString();
        if (!IdentityValidation.Email(email)) return null;
        // Claims are read only AFTER Supabase has authenticated this token.
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-','+').Replace('_','/');
            payload = payload.PadRight((payload.Length+3)/4*4,'=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (claims.RootElement.GetProperty("sub").GetString() != authId.ToString() ||
                claims.RootElement.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                !Guid.TryParse(claims.RootElement.GetProperty("session_id").GetString(),out var sessionId)) return null;
            return new(authId,email!.Trim().ToLowerInvariant(),sessionId);
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    public async Task<ProviderSession?> Login(LoginInput input, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post,"token?grant_type=password", new { email=input.Email.Trim().ToLowerInvariant(),password=input.Password });
        using var response = await Send(request,ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProviderSession>(ct) : null;
    }
    public async Task<ProviderSession?> Refresh(string refreshToken, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post,"token?grant_type=refresh_token",new { refresh_token=refreshToken });
        using var response = await Send(request,ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProviderSession>(ct) : null;
    }
    public async Task Logout(string accessToken, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post,"logout?scope=local",token:accessToken);
        using var response = await Send(request,ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized) throw new IdentityProviderException();
    }
    public async Task Register(RegisterInput input, CancellationToken ct)
    {
        var redirect = Uri.EscapeDataString(settings.FrontendUrl.TrimEnd('/') + "/auth/confirm");
        using var request = Request(HttpMethod.Post,"signup?redirect_to="+redirect,new { email=input.Email.Trim().ToLowerInvariant(),password=input.Password });
        using var response = await Send(request,ct);
        // Intentionally indistinguishable for existing/unavailable accounts.
    }
    public async Task Recover(string email, CancellationToken ct)
    {
        var redirect = Uri.EscapeDataString(settings.FrontendUrl.TrimEnd('/') + "/auth/reset-password");
        using var request = Request(HttpMethod.Post,"recover?redirect_to="+redirect,new { email=email.Trim().ToLowerInvariant() });
        using var response = await Send(request,ct);
        // Never reveal whether the email is registered.
    }
    public async Task<bool> ChangePassword(string token, string password, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put,"user",new { password },token);
        using var response = await Send(request,ct);
        return response.IsSuccessStatusCode;
    }
    public async Task<bool> ChangeEmail(string token, string email, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put,"user",new { email=email.Trim().ToLowerInvariant() },token);
        using var response = await Send(request,ct);
        return response.IsSuccessStatusCode;
    }
}
