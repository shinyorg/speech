using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Shiny.AiConversation.Maui.GithubCopilot;

public class GitHubCopilotChatClientProvider : IChatClientProvider
{
    const string ClientId = "Iv1.b507a08c87ecfe98";
    const string Scope = "read:user";
    const string DeviceCodeUrl = "https://github.com/login/device/code";
    const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    const string CopilotBaseUrl = "https://api.githubcopilot.com";
    const string DefaultModel = "gpt-4o";
    const string TokenStorageKey = "github_oauth_token";

    static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders =
        {
            { "Accept", "application/json" },
            { "User-Agent", "GitHubCopilotChat/0.26.7" }
        }
    };

    string? cachedCopilotToken;
    DateTimeOffset copilotTokenExpiry = DateTimeOffset.MinValue;
    CancellationTokenSource? authCts;
    TaskCompletionSource? authTcs;
    readonly object authLock = new();

    /// <summary>
    /// Fired when the access token changes.
    /// A non-null value means authentication succeeded; null means signed out.
    /// </summary>
    public event Action<string?>? AccessTokenChanged;

    public bool IsAuthenticated => SecureStorage.Default.GetAsync(TokenStorageKey).GetAwaiter().GetResult() != null;

    public async Task<IChatClient> GetChatClient(CancellationToken cancelToken = default)
    {
        var githubToken = await SecureStorage.Default.GetAsync(TokenStorageKey).ConfigureAwait(false);
        if (githubToken == null)
            await this.StartAuthentication(cancelToken).ConfigureAwait(false);

        string copilotToken;
        try
        {
            copilotToken = await this.GetCopilotToken(cancelToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            this.SignOut();
            await this.StartAuthentication(cancelToken).ConfigureAwait(false);
            copilotToken = await this.GetCopilotToken(cancelToken).ConfigureAwait(false);
        }

        var options = new OpenAIClientOptions { Endpoint = new Uri(CopilotBaseUrl) };
        options.AddPolicy(new CopilotHeadersPolicy(), PipelinePosition.PerCall);

        var client = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(copilotToken),
            options
        );

        return client
            .GetChatClient(DefaultModel)
            .AsIChatClient();
    }

    /// <summary>
    /// Starts the GitHub device code authentication flow.
    /// If authentication is already in progress, returns the existing task.
    /// Shows a popup with the device code, copies it to clipboard, opens the browser,
    /// and polls until the user completes authorization.
    /// </summary>
    public Task StartAuthentication(CancellationToken ct = default)
    {
        lock (this.authLock)
        {
            if (this.authTcs != null)
                return this.authTcs.Task;

            this.authTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this.authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        // Run on a thread pool thread to avoid deadlocking the main thread
        // when GetChatClient is called from the UI thread — the auth flow
        // needs MainThread.InvokeOnMainThreadAsync to show the device code popup.
        _ = Task.Run(() => this.RunAuthenticationFlow(this.authCts, this.authTcs));
        return this.authTcs.Task;
    }

    /// <summary>
    /// Cancels any in-progress authentication flow.
    /// </summary>
    public void CancelAuthentication()
    {
        lock (this.authLock)
        {
            this.authCts?.Cancel();
        }
    }

    public void SignOut()
    {
        SecureStorage.Default.Remove(TokenStorageKey);
        this.cachedCopilotToken = null;
        this.copilotTokenExpiry = DateTimeOffset.MinValue;
        this.AccessTokenChanged?.Invoke(null);
    }

    async Task RunAuthenticationFlow(CancellationTokenSource cts, TaskCompletionSource tcs)
    {
        try
        {
            var deviceCode = await this.StartDeviceFlow(cts.Token).ConfigureAwait(false);

            // Show the device code to the user via a popup on the main page
            var page = Application.Current?.Windows.FirstOrDefault()?.Page
                ?? throw new InvalidOperationException("No active page found to display the authentication dialog.");

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                await Clipboard.Default.SetTextAsync(deviceCode.UserCode);
                await page.DisplayAlertAsync(
                    "GitHub Sign In",
                    $"Your device code is:\n\n{deviceCode.UserCode}\n\nIt has been copied to your clipboard. Press OK to open GitHub and paste the code.",
                    "OK"
                );
                await Browser.Default.OpenAsync(deviceCode.VerificationUri, BrowserLaunchMode.SystemPreferred);
            }).ConfigureAwait(false);

            cts.CancelAfter(TimeSpan.FromSeconds(deviceCode.ExpiresIn));
            var interval = TimeSpan.FromSeconds(deviceCode.Interval);

            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(false);

                if (await this.PollForToken(deviceCode, cts.Token).ConfigureAwait(false))
                {
                    var token = await SecureStorage.Default.GetAsync(TokenStorageKey).ConfigureAwait(false);
                    this.AccessTokenChanged?.Invoke(token);
                    tcs.TrySetResult();
                    return;
                }
            }

            cts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException ex)
        {
            tcs.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            lock (this.authLock)
            {
                this.authCts?.Dispose();
                this.authCts = null;
                this.authTcs = null;
            }
        }
    }

    async Task<DeviceCodeResponse> StartDeviceFlow(CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope
        });

        var response = await Http.PostAsync(DeviceCodeUrl, content, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(ct))!;
    }

    async Task<bool> PollForToken(DeviceCodeResponse deviceCode, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_code"] = deviceCode.DeviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        });

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(AccessTokenUrl, content, ct);
        }
        catch (HttpRequestException)
        {
            // Transient network failures (DNS, connection refused, etc.) — keep polling until
            // the device-code expiry CancelAfter fires.
            return false;
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(ct);

            if (!String.IsNullOrEmpty(result?.AccessToken))
            {
                await SecureStorage.Default.SetAsync(TokenStorageKey, result.AccessToken);
                return true;
            }
        }

        return false;
    }

    async Task<string> GetCopilotToken(CancellationToken ct)
    {
        if (this.cachedCopilotToken != null && DateTimeOffset.UtcNow.AddSeconds(60) < this.copilotTokenExpiry)
            return this.cachedCopilotToken;

        var githubToken = await SecureStorage.Default.GetAsync(TokenStorageKey)
            ?? throw new InvalidOperationException("Not authenticated. Please sign in with GitHub first.");

        using var request = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", githubToken);

        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CopilotTokenResponse>(ct);

        this.cachedCopilotToken = result!.Token;
        this.copilotTokenExpiry = DateTimeOffset.FromUnixTimeSeconds(result.ExpiresAt);

        return this.cachedCopilotToken;
    }
}

record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval
);

record AccessTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription
);

record CopilotTokenResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] long ExpiresAt
);

file class CopilotHeadersPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddHeaders(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    static void AddHeaders(PipelineMessage message)
    {
        message.Request.Headers.Set("Editor-Version", "vscode/1.96.2");
        message.Request.Headers.Set("Editor-Plugin-Version", "copilot-chat/0.26.7");
        message.Request.Headers.Set("Copilot-Integration-Id", "vscode-chat");
        message.Request.Headers.Set("Openai-Intent", "conversation-panel");
    }
}
