using System.Net;
using System.Text;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatInbox.Tests;

public sealed class WebhookTests
{
    private const string ValidText = """{"update_id":2147483648,"message":{"message_id":2147483649,"date":1700000000,"chat":{"id":-2147483650,"title":"Test"},"from":{"id":2147483651,"first_name":"Ada","last_name":"Lovelace"},"text":"hello"}}""";

    [Fact]
    public async Task ValidTextPublishesFullWidthEnvelopeAfterPublisherReturns()
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), ValidText);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(factory.Publisher.Published);
        Assert.Equal(1, item.Version);
        Assert.Equal(2147483648L, item.UpdateId);
        Assert.Equal(-2147483650L, item.ChatId);
        Assert.Equal(2147483649L, item.MessageId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), item.SentAt);
        Assert.Equal("hello", item.Text);
        Assert.Equal(2147483651L, item.SenderId);
        Assert.Equal("Ada", item.SenderFirstName);
        Assert.Equal("Lovelace", item.SenderLastName);
        Assert.Equal("Test", item.ChatTitle);
        Assert.DoesNotContain("test_secret_123", System.Text.Json.JsonSerializer.Serialize(item));
        Assert.DoesNotContain("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi",
            System.Text.Json.JsonSerializer.Serialize(item));
    }

    [Theory]
    [InlineData("", "test_secret_123")]
    [InlineData("not-a-token", "test_secret_123")]
    [InlineData("123:ABC", "invalid:secret")]
    [InlineData("123:ABC", "")]
    public void InvalidTelegramConfigurationIsRejectedWithoutValueDisclosure(string token, string secret)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new TelegramOptions(token, secret));
        if (token.Length > 0)
            Assert.DoesNotContain(token, error.Message, StringComparison.OrdinalIgnoreCase);
        if (secret.Length > 0)
            Assert.DoesNotContain(secret, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong_secret")]
    public async Task MissingOrWrongSecretNeverPublishes(string? secret)
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), ValidText, secret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Fact]
    public async Task AuthenticatedUnsupportedUpdateIsIgnored()
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), """{"update_id":123,"edited_message":{}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Fact]
    public async Task AuthenticatedNonTextMessageWithRequiredFieldsIsIgnored()
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(),
            """{"update_id":123,"message":{"message_id":456,"date":1700000000,"chat":{"id":789},"photo":[]}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Theory]
    [InlineData("""{"update_id":123,"message":null}""")]
    [InlineData("""{"update_id":123,"message":42}""")]
    [InlineData("""{"update_id":123,"message":[]}""")]
    [InlineData("""{"update_id":123,"message":{}}""")]
    [InlineData("""{"update_id":123,"message":{"message_id":456,"date":1700000000,"chat":{}}}""")]
    public async Task MalformedMessageIsRejectedInsteadOfIgnored(string body)
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"update_id\":\"123\",\"message\":{}}")]
    public async Task MalformedOrMissingUpdateIdIsRejected(string body)
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Fact]
    public async Task WrongContentTypeIsRejected()
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), ValidText, mediaType: "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Fact]
    public async Task TextLengthCountsUnicodeScalars()
    {
        await using var factory = new WebhookFactory();
        using var client = factory.CreateClient();
        using var accepted = await SendAsync(client, ValidText.Replace("hello", "😀" + new string('a', 4095)));
        using var rejected = await SendAsync(client, ValidText.Replace("hello", "😀" + new string('a', 4096)));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Single(factory.Publisher.Published);
    }

    [Fact]
    public async Task OversizeBodyIsRejectedBeforePublish()
    {
        await using var factory = new WebhookFactory();
        using var response = await SendAsync(factory.CreateClient(), ValidText.Replace("hello", new string('a', 65537)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(factory.Publisher.Published);
    }

    [Fact]
    public async Task PublisherFailureIsRetryableHttpFailure()
    {
        await using var factory = new WebhookFactory();
        factory.Publisher.Failure = new IOException("broker unavailable");
        using var response = await SendAsync(factory.CreateClient(), ValidText);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string body,
        string? secret = "test_secret_123", string mediaType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/telegram")
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
        if (secret is not null)
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        return client.SendAsync(request);
    }
}

public sealed class RecordingPublisher : IInboundPublisher
{
    public List<InboundTelegramUpdate> Published { get; } = [];
    public Exception? Failure { get; set; }

    public Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken)
    {
        if (Failure is not null)
            throw Failure;
        Published.Add(update);
        return Task.CompletedTask;
    }
}

public sealed class WebhookFactory : WebApplicationFactory<Program>
{
    public RecordingPublisher Publisher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseSetting("Telegram:BotToken", "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi")
        .UseSetting("Telegram:WebhookSecret", "test_secret_123")
        .UseSetting("Telegram:RegistrationEnabled", "false")
        .ConfigureTestServices(services => services.AddSingleton<IInboundPublisher>(Publisher));
}
