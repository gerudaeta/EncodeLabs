using ChatInbox.Application.Outbound;
using ChatInbox.Application.Queries;
using ChatInbox.Application.Read;

namespace ChatInbox.Api;

public sealed record ReplyRequest(string? Text);

public static class InboxEndpoints
{
    private const int MaxReplyTextScalars = 4096;

    public static void MapInboxReplies(this WebApplication app)
    {
        app.MapPost("/api/conversations/{id:guid}/messages", async (Guid id, ReplyRequest request,
            SendReplyUseCase useCase, CancellationToken cancellationToken) =>
        {
            var text = request.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.EnumerateRunes().Count() > MaxReplyTextScalars)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["text"] = ["Text must be non-empty and at most 4096 characters."]
                });

            try
            {
                var message = await useCase.SendAsync(id, text, cancellationToken);
                return Results.Json(message, statusCode: StatusCodes.Status201Created);
            }
            catch (ConversationNotFoundException)
            {
                return Results.NotFound();
            }
            catch (TelegramSendFailedException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });
    }

    public static void MapInboxQueries(this WebApplication app)
    {
        app.MapGet("/api/conversations", async (string? limit, string? before,
            IInboxQueries queries, CancellationToken cancellationToken) =>
        {
            if (!TryParameters(limit, before, out var count, out var cursor))
                return Results.BadRequest();
            return Results.Ok(await queries.ListConversationsAsync(count, cursor, cancellationToken));
        });

        app.MapGet("/api/conversations/{id:guid}/messages", async (Guid id, string? limit,
            string? before, IInboxQueries queries, CancellationToken cancellationToken) =>
        {
            if (!TryParameters(limit, before, out var count, out var cursor))
                return Results.BadRequest();
            try
            {
                return Results.Ok(await queries.ListMessagesAsync(id, count, cursor,
                    cancellationToken));
            }
            catch (ConversationNotFoundException)
            {
                return Results.NotFound();
            }
        });
    }

    public static void MapInboxReadState(this WebApplication app)
    {
        app.MapPost("/api/conversations/{id:guid}/read", async (Guid id, MarkConversationReadUseCase useCase,
            CancellationToken cancellationToken) =>
        {
            var found = await useCase.MarkReadAsync(id, cancellationToken);
            return found ? Results.NoContent() : Results.NotFound();
        });
    }

    private static bool TryParameters(string? limit, string? before, out int count,
        out PageCursor? cursor)
    {
        count = 50;
        cursor = null;
        if (limit is not null && (!int.TryParse(limit, out count) || count is < 1 or > 100))
            return false;
        if (before is null) return true;
        var parsed = PageCursor.Parse(before);
        cursor = parsed.Value;
        return parsed.IsValid;
    }
}
