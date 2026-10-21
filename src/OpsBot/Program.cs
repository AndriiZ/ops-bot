using OpsBot.Chat;

namespace OpsBot;

// The class is not static: the tests use it as the type for WebApplicationFactory<Program>.
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOpsBot(builder.Configuration);

        var app = builder.Build();
        app.MapPost("/telegram/webhook", WebhookEndpoint.HandleAsync);
        // For the smoke test of deploy.sh (service.env: HEALTH_URL and VERSION_URL). Kestrel listens only on localhost.
        app.MapGet("/health", (WebhookStatus webhook) => Results.Ok(new { status = "ok", version = AppVersion.Current, webhook = webhook.Text }));
        await app.RunAsync();
    }
}
