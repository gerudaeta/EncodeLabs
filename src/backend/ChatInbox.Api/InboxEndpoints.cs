using ChatInbox.Application.Queries;

namespace ChatInbox.Api;

public static class InboxEndpoints
{
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
