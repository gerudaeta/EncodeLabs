using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ChatInbox.Infrastructure.Messaging;

namespace ChatInbox.Infrastructure.Telegram;

public static class NgrokTunnelSelector
{
    public static Uri SelectHttpsApiTunnel(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("endpoints", out var endpoints) ||
            endpoints.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Ngrok endpoint response is invalid.");

        var matches = new List<Uri>();
        foreach (var endpoint in endpoints.EnumerateArray())
        {
            if (endpoint.ValueKind != JsonValueKind.Object ||
                !endpoint.TryGetProperty("url", out var publicUrl) ||
                publicUrl.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(publicUrl.GetString(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !endpoint.TryGetProperty("upstream", out var upstream) ||
                upstream.ValueKind != JsonValueKind.Object ||
                !upstream.TryGetProperty("url", out var upstreamUrl) ||
                upstreamUrl.ValueKind != JsonValueKind.String ||
                upstreamUrl.GetString() != "http://api:8080") continue;
            matches.Add(uri);
        }
        if (matches.Count != 1)
            throw new InvalidOperationException("Ngrok must expose exactly one HTTPS API endpoint.");
        return matches[0];
    }
}

public interface INgrokTunnelClient
{
    Task<JsonDocument> GetTunnelsAsync(CancellationToken cancellationToken);
}

public sealed class NgrokTunnelClient(HttpClient client) : INgrokTunnelClient
{
    public async Task<JsonDocument> GetTunnelsAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/endpoints", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    }
}

public sealed record WebhookInfo(string? Url, int PendingCount, string? LastError);

public interface IRegistrationClient
{
    Task SetWebhookAsync(Uri url, string secret, CancellationToken cancellationToken);
    Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken cancellationToken);
}

public sealed class TelegramApiClient(HttpClient client, TelegramOptions options) : IRegistrationClient
{
    public async Task SetWebhookAsync(Uri url, string secret, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync($"./bot{options.BotToken}/setWebhook",
            new { url = url.ToString(), secret_token = secret,
                allowed_updates = new[] { "message" }, drop_pending_updates = false }, cancellationToken);
        using var document = await ReadResultAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.True)
            throw new HttpRequestException("Telegram did not accept webhook registration.");
    }

    public async Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"./bot{options.BotToken}/getWebhookInfo", cancellationToken);
        using var document = await ReadResultAsync(response, cancellationToken);
        var info = document.RootElement.GetProperty("result");
        return new WebhookInfo(
            info.TryGetProperty("url", out var url) ? url.GetString() : null,
            info.TryGetProperty("pending_update_count", out var pending) ? pending.GetInt32() : 0,
            info.TryGetProperty("last_error_message", out var error) ? error.GetString() : null);
    }

    private static Task<JsonDocument> ReadResultAsync(HttpResponseMessage response,
        CancellationToken cancellationToken) => TelegramResponseReader.ReadResultAsync(response, cancellationToken);
}

internal static class TelegramResponseReader
{
    public static async Task<JsonDocument> ReadResultAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Telegram API request failed.");
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken); }
        catch (JsonException) { throw new HttpRequestException("Telegram API response was invalid."); }
        if (!document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            document.Dispose();
            throw new HttpRequestException("Telegram API rejected the request.");
        }
        return document;
    }
}

public interface IRegistrationStatus
{
    bool IsReady { get; }
    string? CurrentUrl { get; }
    string? LastErrorCategory { get; }
    int? PendingCount { get; }
    string? LastTelegramDeliveryError { get; }
}

public sealed class RegistrationStatus(IInboundConsumerReadiness? consumerReadiness = null) : IRegistrationStatus
{
    private readonly object _gate = new();
    private bool _ready;
    private string? _currentUrl;
    private string? _error;
    private int? _pending;
    private string? _deliveryError;

    public bool IsReady { get { lock (_gate) return _ready && (consumerReadiness?.IsReady ?? true); } }
    public string? CurrentUrl { get { lock (_gate) return _currentUrl; } }
    public string? LastErrorCategory { get { lock (_gate) return consumerReadiness?.IsReady == false
        ? "consumer_unavailable" : _error; } }
    public int? PendingCount { get { lock (_gate) return _pending; } }
    public string? LastTelegramDeliveryError { get { lock (_gate) return _deliveryError; } }

    internal void MarkUnready(string category)
    {
        lock (_gate) { _ready = false; _currentUrl = null; _error = category; }
    }

    internal void Observe(Uri expected, WebhookInfo info)
    {
        lock (_gate)
        {
            _pending = info.PendingCount;
            // Telegram controls this text. Do not expose untrusted diagnostics that may contain credentials.
            _deliveryError = info.LastError is null ? null : "telegram_delivery_error";
            _ready = string.Equals(info.Url, expected.ToString(), StringComparison.Ordinal);
            _currentUrl = _ready ? expected.ToString() : null;
            _error = _ready ? null : "url_mismatch";
        }
    }
}

public sealed class TelegramRegistration(
    INgrokTunnelClient ngrok,
    IRegistrationClient telegram,
    RegistrationStatus status,
    ILogger<TelegramRegistration> logger,
    TelegramOptions options,
    IInboundConsumerReadiness consumerReadiness,
    IHostApplicationLifetime hostLifetime) : BackgroundService
{
    private Uri? _registeredUrl;
    private int _failureCount;

    internal async Task<TimeSpan> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var category = "listener_unavailable";
        try
        {
            await WaitForApplicationStartedAsync(hostLifetime.ApplicationStarted, cancellationToken);
            category = "consumer_unavailable";
            await consumerReadiness.WaitReadyAsync(cancellationToken);
            if (!consumerReadiness.IsReady)
                throw new IOException("Inbound consumer is not subscribed.");
            category = "ngrok_unavailable";
            using var endpoints = await ngrok.GetTunnelsAsync(cancellationToken);
            category = "ngrok_endpoint_selection";
            var publicUrl = NgrokTunnelSelector.SelectHttpsApiTunnel(endpoints.RootElement);
            var expected = new Uri(publicUrl, "/webhooks/telegram");
            if (_registeredUrl != expected)
            {
                category = "telegram_registration_failure";
                status.MarkUnready("registration_pending");
                await telegram.SetWebhookAsync(expected, options.WebhookSecret, cancellationToken);
                _registeredUrl = expected;
            }
            category = "telegram_verification_failure";
            var info = await telegram.GetWebhookInfoAsync(cancellationToken);
            status.Observe(expected, info);
            if (!status.IsReady)
            {
                _registeredUrl = null;
                return FailureDelay();
            }
            _failureCount = 0;
            return TimeSpan.FromSeconds(10);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            status.MarkUnready(category);
            logger.LogWarning("Telegram registration unavailable: {Category}", category);
            return FailureDelay();
        }
    }

    private static async Task WaitForApplicationStartedAsync(
        CancellationToken applicationStarted, CancellationToken cancellationToken)
    {
        if (applicationStarted.IsCancellationRequested) return;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = applicationStarted.Register(static state =>
            ((TaskCompletionSource)state!).TrySetResult(), signal);
        await signal.Task.WaitAsync(cancellationToken);
    }

    private TimeSpan FailureDelay()
    {
        var seconds = Math.Min(30, 1 << Math.Min(_failureCount, 5));
        _failureCount = Math.Min(_failureCount + 1, 5);
        return TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await ReconcileOnceAsync(stoppingToken);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
