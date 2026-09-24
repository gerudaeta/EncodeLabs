using System.Text;
using System.Text.Json;

namespace ChatInbox.Application.Queries;

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public sealed record ConversationDto(Guid Id, long TelegramChatId, string? DisplayName,
    DateTimeOffset? LastMessageAt, string? LastMessagePreview);

public sealed record MessageDto(Guid Id, long TelegramMessageId, string Direction,
    string Text, DateTimeOffset SentAt);

public interface IInboxQueries
{
    Task<Page<ConversationDto>> ListConversationsAsync(int limit, PageCursor? before,
        CancellationToken cancellationToken);
    Task<Page<MessageDto>> ListMessagesAsync(Guid conversationId, int limit, PageCursor? before,
        CancellationToken cancellationToken);
}

public sealed class ConversationNotFoundException : Exception;

public readonly record struct CursorParseResult(bool IsValid, PageCursor? Value);

public sealed record PageCursor(DateTimeOffset Timestamp, Guid Id)
{
    private sealed record Payload(int V, string T, string Id);

    public static string Encode(DateTimeOffset timestamp, Guid id)
    {
        var json = JsonSerializer.Serialize(new Payload(1, timestamp.ToUniversalTime().ToString("O"),
            id.ToString("D")));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static CursorParseResult Parse(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 512 ||
            encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and '_'))
            return new(false, null);

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("V", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v != 1 ||
                !root.TryGetProperty("T", out var time) || time.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("Id", out var identity) || identity.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParseExact(time.GetString(), "O", null,
                    System.Globalization.DateTimeStyles.None, out var timestamp) ||
                !Guid.TryParseExact(identity.GetString(), "D", out var id) || id == Guid.Empty)
                return new(false, null);

            var cursor = new PageCursor(timestamp, id);
            return Encode(timestamp, id) == encoded
                ? new(true, cursor) : new(false, null);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return new(false, null);
        }
    }
}
