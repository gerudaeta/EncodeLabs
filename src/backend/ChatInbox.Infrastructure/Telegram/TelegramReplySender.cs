using System.Net.Http.Json;
using ChatInbox.Application.Outbound;

namespace ChatInbox.Infrastructure.Telegram;

public sealed class TelegramReplySender(HttpClient client, TelegramOptions options) : IReplySender
{
    public async Task<SentTelegramMessage> SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync($"./bot{options.BotToken}/sendMessage",
            new { chat_id = chatId, text }, cancellationToken);
        using var document = await TelegramResponseReader.ReadResultAsync(response, cancellationToken);
        var result = document.RootElement.GetProperty("result");
        return new SentTelegramMessage(result.GetProperty("message_id").GetInt64(),
            DateTimeOffset.FromUnixTimeSeconds(result.GetProperty("date").GetInt64()));
    }
}
