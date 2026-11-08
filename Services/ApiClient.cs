using LabTrackAgent.Models;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace LabTrackAgent.Services;

/// <summary>The outcome of one API call. Unreachable means the server could not be contacted at all.</summary>
public class ApiResult<T>
{
    public bool Success { get; init; }
    public bool Unreachable { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
}

public class ApiClient
{
    private readonly HttpClient _http;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan DelayBetweenAttempts = TimeSpan.FromSeconds(1);

    public ApiClient(AgentConfig config)
    {
        // The trailing slash matters: without it, "agent/options" would replace the last part
        // of the base address (".../api") instead of being added after it.
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    /// <summary>Checks the credentials through the same endpoint the dashboard uses, then keeps the token.</summary>
    public async Task<ApiResult<AuthResponse>> LoginAsync(string identifier, string password)
    {
        var result = await SendAsync<AuthResponse>(() => _http.PostAsJsonAsync(
            "auth/login", new LoginRequest { Identifier = identifier, Password = password }));

        if (result.Success && result.Data is not null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Data.Token);

        return result;
    }

    public Task<ApiResult<AttendanceOptions>> GetOptionsAsync(string pcIdentifier) =>
        SendAsync<AttendanceOptions>(() =>
            _http.GetAsync($"agent/options?pcIdentifier={Uri.EscapeDataString(pcIdentifier)}"));

    public Task<ApiResult<ConfirmResponse>> ConfirmAsync(string pcIdentifier, int sectionId) =>
        SendAsync<ConfirmResponse>(() => _http.PostAsJsonAsync(
            "agent/confirm", new ConfirmRequest { PcIdentifier = pcIdentifier, SectionId = sectionId }));

    /// <summary>Forgets the token, for example when the student cancels or the session ends.</summary>
    public void ClearSession() => _http.DefaultRequestHeaders.Authorization = null;

    public async Task<bool> IsApiReachableAsync()
    {
        try
        {
            using var response = await _http.GetAsync("agent/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a request and retries up to 3 times, but only when the server can't be reached
    /// (network error or timeout). A real answer from the server, even an error, is returned
    /// immediately. Login and confirm are safe to repeat, because the server ignores duplicates.
    /// </summary>
    private async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var response = await send();

                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<T>();
                    return new ApiResult<T> { Success = true, Data = data };
                }

                var body = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
                return new ApiResult<T> { Error = string.IsNullOrWhiteSpace(body) ? DescribeStatus(response.StatusCode) : body };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < MaxAttempts)
                    await Task.Delay(DelayBetweenAttempts);
            }
        }

        return new ApiResult<T> { Unreachable = true, Error = "Can't reach the server right now." };
    }

    private static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Your login expired. Please log in again.",
        HttpStatusCode.Forbidden => "Only student accounts can log in on lab computers.",
        _ => $"The server returned an error ({(int)status})."
    };
}