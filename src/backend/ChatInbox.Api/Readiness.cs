using ChatInbox.Infrastructure.Messaging;
using ChatInbox.Infrastructure.Telegram;

namespace ChatInbox.Api;

public static class Readiness
{
    public static IEndpointRouteBuilder MapReadiness(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/ready", (IRegistrationStatus registration,
            IServiceProvider services, IConfiguration configuration) =>
        {
            var consumer = services.GetService<IInboundConsumerReadiness>();
            var ready = registration.IsReady && consumer?.IsReady == true;
            var state = new
            {
                ready,
                currentUrl = registration.CurrentUrl,
                lastErrorCategory = !configuration.GetValue<bool>("Telegram:RegistrationEnabled")
                    ? "registration_disabled" : consumer?.IsReady == true
                        ? registration.LastErrorCategory : "consumer_unavailable",
                pendingCount = registration.PendingCount,
                lastTelegramDeliveryError = registration.LastTelegramDeliveryError
            };
            return ready ? Results.Ok(state) : Results.Json(state, statusCode: 503);
        });
        return endpoints;
    }
}
