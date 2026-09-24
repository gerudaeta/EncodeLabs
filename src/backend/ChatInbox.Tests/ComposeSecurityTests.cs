using System.Text.RegularExpressions;
using System.Text.Json;
using Xunit;

namespace ChatInbox.Tests;

public sealed class ComposeSecurityTests
{
    [Fact]
    public void ApiPublishesOnlyLoopbackPortAndEnablesRegistration()
    {
        var api = Service("api");
        var ports = Regex.Matches(api, "(?m)^      - \"([^\"]+)\"$")
            .Cast<Match>()
            .Select(match => match.Groups[1].Value);
        Assert.Equal(new[] { "127.0.0.1:8080:8080" }, ports);
        Assert.Contains("Telegram__RegistrationEnabled: \"true\"", api);
        Assert.Contains("Telegram__BotToken:", api);
        Assert.Contains("Telegram__WebhookSecret:", api);
        Assert.Contains("ConnectionStrings__Postgres:", api);
        Assert.Contains("RabbitMQ__Uri:", api);
    }

    [Fact]
    public void AgentApiIsPrivateAndPolicyFileIsMounted()
    {
        var compose = File.ReadAllText(Path.Combine(FindRoot(), "docker-compose.yml"));
        Assert.DoesNotContain("4040:4040", compose);
        var ngrok = Service("ngrok");
        Assert.DoesNotContain("    ports:", ngrok);
        Assert.Contains("/etc/ngrok/ngrok.yml", ngrok);
        Assert.Contains("/etc/ngrok/ngrok-policy.yml", ngrok);
        Assert.Contains("start", ngrok);
        Assert.Contains("telegram-webhook", ngrok);
        Assert.DoesNotContain("http api:8080", ngrok);
        var config = File.ReadAllText(Path.Combine(FindRoot(), "config/ngrok.yml"));
        Assert.Contains("version: 3", config);
        Assert.Contains("web_addr: 0.0.0.0:4040", config);
        Assert.Contains("- ngrok", config);
        Assert.Contains("traffic_policy_file: /etc/ngrok/ngrok-policy.yml", config);
        Assert.Contains("url: http://api:8080", config);
    }

    [Fact]
    public void PublicEndpointDeniesEveryMethodOrPathExceptWebhookPost()
    {
        var policy = File.ReadAllText(Path.Combine(FindRoot(), "config/ngrok-policy.yml"));
        Assert.Contains("on_http_request:", policy);
        Assert.Contains("req.method != 'POST' || req.url.path != '/webhooks/telegram'", policy);
        Assert.Single(Regex.Matches(policy, @"(?m)^\s+- type: deny\s*$").Cast<Match>());
        Assert.DoesNotContain("type: allow", policy);
    }

    [Fact]
    public void BrokerVersionAndSecretInputsArePinnedWithoutValues()
    {
        Assert.Contains("rabbitmq:4.3.6-management", Service("rabbitmq"));
        var example = File.ReadAllText(Path.Combine(FindRoot(), ".env.example"));
        Assert.Contains("TELEGRAM_BOT_TOKEN=123456789:replace_", example);
        Assert.Contains("TELEGRAM_WEBHOOK_SECRET=replace_", example);
        Assert.Contains("NGROK_AUTHTOKEN=replace_", example);
        Assert.DoesNotContain("=123456:", example);
    }

    [Fact]
    public void RegistrationIsDisabledOutsideComposeByDefault()
    {
        var settings = File.ReadAllText(Path.Combine(FindRoot(), "src/backend/ChatInbox.Api/appsettings.json"));
        using var document = JsonDocument.Parse(settings);
        Assert.False(document.RootElement.GetProperty("Telegram")
            .GetProperty("RegistrationEnabled").GetBoolean());
        Assert.DoesNotContain("BotToken", settings);
        Assert.DoesNotContain("WebhookSecret", settings);
    }

    private static string Service(string name)
    {
        var compose = File.ReadAllText(Path.Combine(FindRoot(), "docker-compose.yml"));
        var match = Regex.Match(compose, $@"(?ms)^  {Regex.Escape(name)}:\s*\n(.*?)(?=^  [a-z][a-z0-9_-]*:|\z)");
        Assert.True(match.Success, $"Missing Compose service {name}.");
        return match.Groups[1].Value;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
        throw new FileNotFoundException("docker-compose.yml");
    }
}
