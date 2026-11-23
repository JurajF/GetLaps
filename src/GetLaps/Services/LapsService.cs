using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;

namespace GetLaps.Services;

/// <summary>
/// Reads Windows LAPS credentials from Microsoft Graph
/// (<c>/directory/deviceLocalCredentials</c>) on behalf of the signed-in user.
/// </summary>
public sealed class LapsService
{
    public const string Scope = "https://graph.microsoft.com/DeviceLocalCredential.Read.All";
    public static readonly string[] Scopes = [Scope];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IAuthorizationHeaderProvider _authHeaders;
    private readonly ILogger<LapsService> _logger;

    public LapsService(
        HttpClient http,
        IAuthorizationHeaderProvider authHeaders,
        IOptions<GraphOptions> options,
        ILogger<LapsService> logger)
    {
        _http = http;
        _authHeaders = authHeaders;
        _logger = logger;

        var baseUrl = options.Value.BaseUrl;
        _http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        // Graph rejects deviceLocalCredentials requests (400) without a User-Agent,
        // and requires the ocp-client-name/version headers as well.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GetLaps", "1.0"));
        _http.DefaultRequestHeaders.Add("ocp-client-name", "GetLaps");
        _http.DefaultRequestHeaders.Add("ocp-client-version", "1.0");
    }

    /// <summary>Finds devices with a LAPS backup whose name matches exactly.</summary>
    public async Task<IReadOnlyList<LapsDevice>> FindDevicesAsync(string deviceName, CancellationToken ct)
    {
        var filter = $"deviceName eq '{deviceName.Replace("'", "''")}'";
        var url = "directory/deviceLocalCredentials?$filter=" + Uri.EscapeDataString(filter);

        var page = await SendAsync<GraphCollection<DeviceLocalCredentialInfo>>(url, ct);
        return (page?.Value ?? [])
            .Select(ToDevice)
            .OrderByDescending(d => d.LastBackupDateTime)
            .ToList();
    }

    /// <summary>Retrieves the backed-up credentials (including passwords) for one device.</summary>
    public async Task<LapsDeviceCredentials> GetCredentialsAsync(string deviceId, CancellationToken ct)
    {
        var url = $"directory/deviceLocalCredentials/{Uri.EscapeDataString(deviceId)}" +
                  "?$select=id,deviceName,lastBackupDateTime,refreshDateTime,credentials";

        var info = await SendAsync<DeviceLocalCredentialInfo>(url, ct)
                   ?? throw new LapsException("No LAPS credentials were returned for this device.");

        var credentials = (info.Credentials ?? [])
            .OrderByDescending(c => c.BackupDateTime)
            .Select(c => new LapsCredential(
                c.AccountName ?? "(unknown)",
                c.AccountSid,
                c.BackupDateTime,
                DecodePassword(c.PasswordBase64)))
            .ToList();

        return new LapsDeviceCredentials(ToDevice(info), credentials);
    }

    private async Task<T?> SendAsync<T>(string relativeUrl, CancellationToken ct)
    {
        // Throws MicrosoftIdentityWebChallengeUserException when consent or a fresh
        // sign-in is needed; [AuthorizeForScopes] on the page turns that into a redirect.
        var authorization = await _authHeaders.CreateAuthorizationHeaderForUserAsync(Scopes, cancellationToken: ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);

        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }

        var error = await ReadGraphErrorAsync(response, ct);
        _logger.LogWarning("Graph request {Url} failed with {Status}: {Error}",
            relativeUrl.Split('?')[0], (int)response.StatusCode, error);

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new LapsException("No LAPS password is stored in Entra ID for this device."),
            HttpStatusCode.Forbidden => new LapsException(
                "Access denied. Your account needs an Entra role that can read LAPS passwords " +
                "(for example Cloud Device Administrator or Intune Administrator), and the app " +
                "needs admin consent for DeviceLocalCredential.Read.All."),
            HttpStatusCode.Unauthorized => new LapsException("Microsoft Graph rejected the access token. Please sign out and sign in again."),
            _ => new LapsException($"Microsoft Graph returned an error ({(int)response.StatusCode}): {error}"),
        };
    }

    private static async Task<string> ReadGraphErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<GraphErrorResponse>(Json, ct);
            return body?.Error?.Message ?? response.ReasonPhrase ?? "unknown error";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "unknown error";
        }
    }

    private static string DecodePassword(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return base64;
        }
    }

    private static LapsDevice ToDevice(DeviceLocalCredentialInfo i) =>
        new(i.Id ?? string.Empty, i.DeviceName ?? string.Empty, i.LastBackupDateTime, i.RefreshDateTime);

    private sealed record GraphCollection<T>([property: JsonPropertyName("value")] List<T>? Value);

    private sealed record DeviceLocalCredentialInfo(
        string? Id,
        string? DeviceName,
        DateTimeOffset? LastBackupDateTime,
        DateTimeOffset? RefreshDateTime,
        List<DeviceLocalCredential>? Credentials);

    private sealed record DeviceLocalCredential(
        string? AccountName,
        string? AccountSid,
        DateTimeOffset? BackupDateTime,
        string? PasswordBase64);

    private sealed record GraphErrorResponse(GraphError? Error);

    private sealed record GraphError(string? Code, string? Message);
}
