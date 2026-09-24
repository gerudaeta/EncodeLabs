using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatInbox.Application.Inbound;
using ChatInbox.Infrastructure.Telegram;
using Microsoft.AspNetCore.Mvc;

namespace ChatInbox.Api;

public static class TelegramWebhook
{
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxTextScalars = 4096;
    private static readonly Meter Meter = new("ChatInbox.Api.Telegram");
    private static readonly Counter<long> IgnoredUpdates = Meter.CreateCounter<long>("telegram_updates_ignored_total");

    public static void MapTelegramWebhook(this WebApplication app)
    {
        app.MapPost("/webhooks/telegram", HandleAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        TelegramOptions options,
        IInboundPublisher publisher)
    {
        if (!HasValidSecret(request, options.WebhookSecret))
            return Results.Unauthorized();

        if (!request.HasJsonContentType())
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        if (request.ContentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        await using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = MaxBodyBytes + 1 - (int)body.Length;
            var count = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                request.HttpContext.RequestAborted);
            if (count == 0)
                break;
            body.Write(buffer, 0, count);
            if (body.Length > MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length));
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        using (document)
        {
            var root = document.RootElement;
            if (!TryInt64(root, "update_id", out var updateId))
                return Results.BadRequest();

            if (!TryProperty(root, "message", out var message) ||
                !TryProperty(message, "text", out var textElement))
            {
                IgnoredUpdates.Add(1);
                return Results.Ok();
            }

            if (textElement.ValueKind != JsonValueKind.String)
                return Results.BadRequest();

            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.EnumerateRunes().Count() > MaxTextScalars ||
                !TryInt64(message, "message_id", out var messageId) ||
                !TryInt64(message, "date", out var sentAtSeconds) ||
                !TryProperty(message, "chat", out var chat) ||
                !TryInt64(chat, "id", out var chatId))
                return Results.BadRequest();

            DateTimeOffset sentAt;
            try
            {
                sentAt = DateTimeOffset.FromUnixTimeSeconds(sentAtSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest();
            }

            var update = new InboundTelegramUpdate(1, updateId, chatId, messageId, sentAt,
                text, OptionalInt64(message, "from", "id"), OptionalString(message, "from", "first_name"),
                OptionalString(message, "from", "last_name"), OptionalString(message, "chat", "title"));

            try
            {
                await publisher.PublishConfirmedAsync(update, request.HttpContext.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok();
        }
    }

    private static bool HasValidSecret(HttpRequest request, string expected)
    {
        if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var supplied))
            return false;

        var left = Encoding.UTF8.GetBytes(supplied.ToString());
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool TryProperty(JsonElement parent, string name, out JsonElement element)
    {
        element = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out element);
    }

    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        value = default;
        return TryProperty(parent, name, out var element) && element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out value);
    }

    private static long? OptionalInt64(JsonElement parent, string container, string name) =>
        TryProperty(parent, container, out var nested) && TryInt64(nested, name, out var value) ? value : null;

    private static string? OptionalString(JsonElement parent, string container, string name) =>
        TryProperty(parent, container, out var nested) && TryProperty(nested, name, out var element) &&
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}

internal sealed class UnavailableInboundPublisher : IInboundPublisher
{
    public Task PublishConfirmedAsync(InboundTelegramUpdate update, CancellationToken cancellationToken) =>
        throw new IOException("Inbound broker publisher is not configured.");
}
