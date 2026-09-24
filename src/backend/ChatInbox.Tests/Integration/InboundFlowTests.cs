using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Xunit;

namespace ChatInbox.Tests.Integration;

public sealed class InboundFlowTests(StackFixture stack) : IClassFixture<StackFixture>
{
    [Fact]
    public async Task ConfirmedWebhookBecomesVisibleOnceAcrossDuplicateAndHostRestart()
    {
        var updateId = UniqueId();
        var chatId = updateId + 1000;
        var messageId = updateId + 2000;
        using var client = stack.Factory.CreateClient();

        using (var first = await SendUpdateAsync(client, updateId, chatId, messageId))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await WaitUntilAsync(async () => await VisibleMessageCountAsync(client, chatId, messageId) == 1,
            TimeSpan.FromSeconds(20), "confirmed update not visible in local GET routes");

        using (var duplicate = await SendUpdateAsync(client, updateId, chatId, messageId))
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await WaitForQueueEmptyAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, await VisibleMessageCountAsync(client, chatId, messageId));

        await stack.RestartHostAsync();
        using var restartedClient = stack.Factory.CreateClient();
        using (var repeated = await SendUpdateAsync(restartedClient, updateId, chatId, messageId))
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        await WaitForQueueEmptyAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, await VisibleMessageCountAsync(restartedClient, chatId, messageId));
        Assert.Equal(0, await QueueCountAsync(RabbitTopology.DeadQueue, "messages"));
    }

    [Fact]
    public async Task BrokerOutageDoesNotReportAcceptedWebhookAndRequiresHostRestart()
    {
        var updateId = UniqueId();
        using var client = stack.Factory.CreateClient();
        try
        {
            await stack.Broker.Container.StopAsync();
            // The consumer deliberately fails the host on a terminal broker shutdown.
            // Depending on scheduling, the test server rejects the request or the
            // publisher returns 503 before shutdown; neither is an accepted webhook.
            var failure = await Record.ExceptionAsync(async () =>
            {
                using var response = await SendUpdateAsync(client, updateId, updateId + 1000,
                    updateId + 2000);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            });
            Assert.True(failure is null or ObjectDisposedException,
                $"Expected a 503 response or fail-stopped listener, got {failure?.GetType().Name}");
        }
        finally
        {
            await stack.Broker.Container.StartAsync();
            await stack.RestartHostAsync();
        }
    }

    [Fact]
    public async Task DatabaseOutageExhaustsRetriesIntoDeadLetterWithoutVisibleMessage()
    {
        var updateId = UniqueId();
        var chatId = updateId + 1000;
        var messageId = updateId + 2000;
        using var client = stack.Factory.CreateClient();
        try
        {
            await stack.Postgres.Container.StopAsync();
            using var response = await SendUpdateAsync(client, updateId, chatId, messageId);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await WaitUntilAsync(async () => await QueueCountAsync(RabbitTopology.DeadQueue,
                "messages") == 1, TimeSpan.FromSeconds(160),
                "database outage did not dead-letter after the configured retry limit");
            Assert.Equal(0, await QueueCountAsync(RabbitTopology.InboundQueue,
                "messages_unacknowledged"));
        }
        finally
        {
            await stack.Postgres.Container.StartAsync();
            await stack.RestartHostAsync();
        }

        using var recoveredClient = stack.Factory.CreateClient();
        Assert.Equal(0, await VisibleMessageCountAsync(recoveredClient, chatId, messageId));
        Assert.Equal(1, await QueueCountAsync(RabbitTopology.DeadQueue, "messages"));
    }

    private static long UniqueId() => Random.Shared.NextInt64(10_000_000, 1_000_000_000);

    private static async Task<HttpResponseMessage> SendUpdateAsync(HttpClient client,
        long updateId, long chatId, long messageId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/telegram")
        {
            Content = JsonContent.Create(new
            {
                update_id = updateId,
                message = new
                {
                    message_id = messageId,
                    date = 1780000000L,
                    chat = new { id = chatId },
                    text = "integration test message"
                }
            })
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", StackFixture.WebhookSecret);
        return await client.SendAsync(request);
    }

    private async Task<int> VisibleMessageCountAsync(HttpClient client, long chatId, long messageId)
    {
        using var conversations = await GetJsonAsync(client, "/api/conversations?limit=100");
        var conversation = conversations.RootElement.GetProperty("items").EnumerateArray()
            .SingleOrDefault(item => item.GetProperty("telegramChatId").GetInt64() == chatId);
        if (conversation.ValueKind == JsonValueKind.Undefined) return 0;
        var id = conversation.GetProperty("id").GetGuid();
        using var messages = await GetJsonAsync(client,
            $"/api/conversations/{id}/messages?limit=100");
        return messages.RootElement.GetProperty("items").EnumerateArray()
            .Count(item => item.GetProperty("telegramMessageId").GetInt64() == messageId);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task WaitForQueueEmptyAsync(TimeSpan timeout) =>
        await WaitUntilAsync(async () =>
                await QueueCountAsync(RabbitTopology.InboundQueue, "messages") == 0 &&
                await QueueCountAsync(RabbitTopology.InboundQueue, "messages_unacknowledged") == 0,
            timeout, "inbound queue did not drain");

    private async Task<int> QueueCountAsync(string queue, string field)
    {
        using var client = new HttpClient { BaseAddress = stack.Broker.ManagementUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(
                new Uri(stack.Broker.AmqpUri).UserInfo))));
        using var response = await client.GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty(field, out var count) ? count.GetInt32() : 0;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout,
        string failure)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!await predicate())
            {
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(failure);
        }
    }
}
