using System.Text.RegularExpressions;

namespace ChatInbox.Infrastructure.Telegram;

public sealed record TelegramOptions
{
    private static readonly Regex BotTokenPattern = new(
        "^[0-9]+:[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex WebhookSecretPattern = new(
        "^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public string BotToken { get; }
    public string WebhookSecret { get; }

    public TelegramOptions(string? botToken, string? webhookSecret)
    {
        if (botToken is null || !BotTokenPattern.IsMatch(botToken) ||
            webhookSecret is null || !WebhookSecretPattern.IsMatch(webhookSecret) ||
            string.Equals(botToken, webhookSecret, StringComparison.Ordinal))
            throw new InvalidOperationException("Telegram configuration is invalid.");

        BotToken = botToken;
        WebhookSecret = webhookSecret;
    }
}
