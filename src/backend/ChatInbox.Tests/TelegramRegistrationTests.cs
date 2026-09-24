using System.Net;
using System.Text.Json;
using ChatInbox.Infrastructure.Telegram;
using ChatInbox.Infrastructure.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChatInbox.Tests;

public sealed class TelegramRegistrationTests
{
    private const string Token = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";
    private const string Secret = "test_secret_123";

    [Theory]
    [InlineData("{\"tunnels\":[]}")]
    [InlineData("{\"tunnels\":[{\"public_url\":\"https://wrong.ngrok.app\",\"config\":{\"addr\":\"http://web:80\"}}]}")]
    [InlineData("{\"tunnels\":[{\"public_url\":\"https://one.ngrok.app\",\"config\":{\"addr\":\"http://api:8080\"}},{\"public_url\":\"https://two.ngrok.app\",\"config\":{\"addr\":\"http://api:8080\"}}]}")]
    [InlineData("{\"tunnels\":[{\"public_url\":\"https://user:password@one.ngrok.app\",\"config\":{\"addr\":\"http://api:8080\"}}]}")]
    [InlineData("{\"tunnels\":[{\"public_url\":\"https://one.ngrok.app/other-path\",\"config\":{\"addr\":\"http://api:8080\"}}]}")]
    public void NoUniqueHttpsApiTunnelFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() =>
            NgrokTunnelSelector.SelectHttpsApiTunnel(document.RootElement));
    }

    [Fact]
    public void SelectsOnlyHttpsTunnelForwardingToApi()
    {
        using var document = JsonDocument.Parse("""
            {"tunnels":[
              {"public_url":"http://one.ngrok.app","config":{"addr":"http://api:8080"}},
              {"public_url":"https://wrong.ngrok.app","config":{"addr":"http://web:80"}},
              {"public_url":"https://one.ngrok.app","config":{"addr":"http://api:8080"}}
            ]}
            """);
        Assert.Equal("https://one.ngrok.app/",
            NgrokTunnelSelector.SelectHttpsApiTunnel(document.RootElement).ToString());
    }

    [Fact]
    public async Task ApiClientSendsSecureWebhookPayloadWithoutDroppingBacklog()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var client = new TelegramApiClient(http, new TelegramOptions(Token, Secret));
        await client.SetWebhookAsync(new Uri("https://one.ngrok.app/webhooks/telegram"), Secret, CancellationToken.None);

        Assert.Equal("https", handler.LastUri!.Scheme);
        Assert.EndsWith("/setWebhook", handler.LastUri.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastBody);
        Assert.Equal("https://one.ngrok.app/webhooks/telegram", body.RootElement.GetProperty("url").GetString());
        Assert.Equal(Secret, body.RootElement.GetProperty("secret_token").GetString());
        Assert.Equal("message", body.RootElement.GetProperty("allowed_updates")[0].GetString());
        Assert.False(body.RootElement.GetProperty("drop_pending_updates").GetBoolean());
    }

    [Fact]
    public async Task ApiClientDoesNotTreatFalseSetWebhookResultAsSuccess()
    {
        var handler = new RecordingHandler { ResponseBody = "{\"ok\":true,\"result\":false}" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var client = new TelegramApiClient(http, Options());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SetWebhookAsync(
            new Uri("https://one.ngrok.app/webhooks/telegram"), Secret, CancellationToken.None));
    }

    [Fact]
    public async Task ApiClientParsesWebhookInfoWithoutExposingToken()
    {
        var handler = new RecordingHandler {
            ResponseBody = "{\"ok\":true,\"result\":{\"url\":\"https://one.ngrok.app/webhooks/telegram\",\"pending_update_count\":7,\"last_error_message\":\"delivery failed\"}}" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") };
        var client = new TelegramApiClient(http, Options());

        var info = await client.GetWebhookInfoAsync(CancellationToken.None);

        Assert.Equal("https", handler.LastUri!.Scheme);
        Assert.Equal("https://one.ngrok.app/webhooks/telegram", info.Url);
        Assert.Equal(7, info.PendingCount);
        Assert.Equal("delivery failed", info.LastError);
    }

    [Fact]
    public async Task NgrokClientRequestsPrivateAgentTunnelsEndpoint()
    {
        var handler = new RecordingHandler { ResponseBody = "{\"tunnels\":[]}" };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ngrok:4040/") };
        var client = new NgrokTunnelClient(http);

        using var response = await client.GetTunnelsAsync(CancellationToken.None);

        Assert.Equal("http://ngrok:4040/api/tunnels", handler.LastUri!.ToString());
        Assert.Empty(response.RootElement.GetProperty("tunnels").EnumerateArray());
    }

    [Fact]
    public async Task StartupRegistersEvenWhenExistingUrlMatchesAndObservesDeliveryState()
    {
        var ngrok = new FakeTunnelClient();
        var telegram = new FakeRegistrationClient {
            Current = new WebhookInfo("https://one.ngrok.app/webhooks/telegram", 3, "delivery failed") };
        var status = new RegistrationStatus();
        var registration = NewRegistration(ngrok, telegram, status);

        await registration.ReconcileOnceAsync(CancellationToken.None);

        Assert.Equal(1, telegram.SetCalls);
        Assert.True(status.IsReady);
        Assert.Equal("https://one.ngrok.app/webhooks/telegram", status.CurrentUrl);
        Assert.Equal(3, status.PendingCount);
        Assert.NotNull(status.LastTelegramDeliveryError);
    }

    [Fact]
    public async Task TransientFailureRetriesWithBoundedDelayAndRedactedStatus()
    {
        var ngrok = new FakeTunnelClient();
        var telegram = new FakeRegistrationClient { FailSetCalls = 2 };
        var status = new RegistrationStatus();
        var logger = new CaptureLogger<TelegramRegistration>();
        var registration = NewRegistration(ngrok, telegram, status, logger);

        var first = await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.False(status.IsReady);
        Assert.InRange(first, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        var second = await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.InRange(second, first, TimeSpan.FromSeconds(30));
        await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(3, telegram.SetCalls);
        Assert.True(status.IsReady);
        var diagnostics = string.Join(' ', logger.Messages) + status.LastErrorCategory;
        Assert.DoesNotContain(Token, diagnostics);
        Assert.DoesNotContain(Secret, diagnostics);
    }

    [Fact]
    public async Task ChangedTunnelStaysUnreadyUntilTelegramConfirmsNewUrl()
    {
        var ngrok = new FakeTunnelClient();
        var telegram = new FakeRegistrationClient();
        var status = new RegistrationStatus();
        var registration = NewRegistration(ngrok, telegram, status);
        await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.True(status.IsReady);

        ngrok.PublicUrl = "https://two.ngrok.app";
        telegram.SuppressInfoUpdate = true;
        await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.Equal(2, telegram.SetCalls);
        Assert.False(status.IsReady);
        telegram.SuppressInfoUpdate = false;
        await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.True(status.IsReady);
        Assert.Equal("https://two.ngrok.app/webhooks/telegram", status.CurrentUrl);
    }

    [Fact]
    public void UntrustedTelegramDiagnosticsNeverExposeCredentialsOrClaimReady()
    {
        var status = new RegistrationStatus();
        status.Observe(new Uri("https://one.ngrok.app/webhooks/telegram"),
            new WebhookInfo($"https://evil.example/{Token}", 4, $"delivery failed {Secret}"));

        Assert.False(status.IsReady);
        Assert.Null(status.CurrentUrl);
        Assert.Equal(4, status.PendingCount);
        Assert.DoesNotContain(Secret, status.LastTelegramDeliveryError);
    }

    [Fact]
    public async Task RegistrationWaitsForConsumerSubscriptionAndReturnsUnreadyWhenItStops()
    {
        var ngrok = new FakeTunnelClient();
        var telegram = new FakeRegistrationClient();
        var readiness = new FakeConsumerReadiness();
        var status = new RegistrationStatus(readiness);
        var registration = NewRegistration(ngrok, telegram, status, readiness: readiness);

        var pending = registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, telegram.SetCalls);
        readiness.MarkReady();
        await pending;
        Assert.True(status.IsReady);

        readiness.MarkStopped();
        Assert.False(status.IsReady);
        Assert.Equal("consumer_unavailable", status.LastErrorCategory);
        await registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.False(status.IsReady);
        Assert.Equal(1, telegram.SetCalls);
    }

    [Fact]
    public async Task RegistrationWaitsForApiListenerBeforeDiscoveringOrSettingWebhook()
    {
        var ngrok = new FakeTunnelClient();
        var telegram = new FakeRegistrationClient();
        var lifetime = new FakeHostLifetime();
        var status = new RegistrationStatus();
        var registration = NewRegistration(ngrok, telegram, status, lifetime: lifetime);

        var pending = registration.ReconcileOnceAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, ngrok.GetCalls);
        Assert.Equal(0, telegram.SetCalls);
        Assert.False(status.IsReady);

        lifetime.MarkStarted();
        await pending;
        Assert.Equal(1, ngrok.GetCalls);
        Assert.Equal(1, telegram.SetCalls);
        Assert.True(status.IsReady);
    }

    [Fact]
    public async Task ReadinessEndpointIsUnhealthyAndDoesNotExposeCredentialsBeforeRegistration()
    {
        await using var factory = new NoExternalServicesFactory();
        using var response = await factory.CreateClient().GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(),
            service => service is TelegramRegistration);
        Assert.Null(factory.Services.GetService<INgrokTunnelClient>());
        Assert.Null(factory.Services.GetService<IRegistrationClient>());
        Assert.DoesNotContain(Token, body);
        Assert.DoesNotContain(Secret, body);
    }

    private static TelegramRegistration NewRegistration(FakeTunnelClient ngrok,
        FakeRegistrationClient telegram, RegistrationStatus status,
        CaptureLogger<TelegramRegistration>? logger = null, FakeConsumerReadiness? readiness = null,
        FakeHostLifetime? lifetime = null) =>
        new(ngrok, telegram, status, logger ?? new(), Options(), readiness ?? FakeConsumerReadiness.Ready(),
            lifetime ?? FakeHostLifetime.Started());

    private sealed class FakeTunnelClient : INgrokTunnelClient
    {
        public string PublicUrl { get; set; } = "https://one.ngrok.app";
        public int GetCalls { get; private set; }
        public Task<JsonDocument> GetTunnelsAsync(CancellationToken _)
        {
            GetCalls++;
            return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new {
                tunnels = new[] { new { public_url = PublicUrl, config = new { addr = "http://api:8080" } } }
            })));
        }
    }

    private static TelegramOptions Options() => new(Token, Secret);

    private sealed class FakeConsumerReadiness : IInboundConsumerReadiness
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady { get; private set; }
        public Task WaitReadyAsync(CancellationToken cancellationToken) =>
            _started.Task.WaitAsync(cancellationToken);
        public void MarkReady() { IsReady = true; _started.TrySetResult(); }
        public void MarkStopped() => IsReady = false;
        public static FakeConsumerReadiness Ready()
        {
            var readiness = new FakeConsumerReadiness();
            readiness.MarkReady();
            return readiness;
        }
    }

    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
        public void MarkStarted() => _started.Cancel();
        public static FakeHostLifetime Started()
        {
            var lifetime = new FakeHostLifetime();
            lifetime.MarkStarted();
            return lifetime;
        }
    }

    private sealed class FakeRegistrationClient : IRegistrationClient
    {
        public int SetCalls { get; private set; }
        public int FailSetCalls { get; set; }
        public bool SuppressInfoUpdate { get; set; }
        public WebhookInfo Current { get; set; } = new(null, 0, null);
        public Task SetWebhookAsync(Uri url, string secret, CancellationToken _)
        {
            SetCalls++;
            if (SetCalls <= FailSetCalls) throw new HttpRequestException("synthetic transient");
            if (!SuppressInfoUpdate) Current = Current with { Url = url.ToString() };
            return Task.CompletedTask;
        }
        public Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken _) => Task.FromResult(Current);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = "{\"ok\":true,\"result\":true}";
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(ResponseBody) };
        }
    }

    private sealed class NoExternalServicesFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
            .UseSetting("Telegram:BotToken", Token)
            .UseSetting("Telegram:WebhookSecret", Secret)
            .UseSetting("Telegram:RegistrationEnabled", "false")
            .UseSetting("ConnectionStrings:Postgres", "")
            .UseSetting("RabbitMQ:Uri", "");
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state,
            Exception? error, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, error));
    }
}
