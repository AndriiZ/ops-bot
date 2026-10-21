using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpsBot.Chat;
using OpsBot.Services;
using Telegram.Bot;

namespace OpsBot;

public static partial class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpsBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BotOptions>()
            .Bind(configuration.GetSection(BotOptions.Section))
            .Validate(o => o.Token.Length > 0, "Bot:Token is necessary.")
            .Validate(o => SecretPattern().IsMatch(o.WebhookSecret), "Bot:WebhookSecret: 1–256 characters A-Z, a-z, 0-9, _ and -.")
            .Validate(o => o.AdminIds.Length > 0, "Bot:AdminIds: at least one Telegram user ID.")
            .ValidateOnStart();
        services.AddOptions<OpsOptions>().Bind(configuration.GetSection(OpsOptions.Section));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ITelegramBotClient>(provider => CreateBot(provider.GetRequiredService<IOptions<BotOptions>>().Value));
        services.AddHttpClient(ServiceInspector.HttpClientName, (provider, http) =>
            http.Timeout = provider.GetRequiredService<IOptions<OpsOptions>>().Value.HealthTimeout);

        services.AddSingleton<ISystemCommands, SystemCommands>();
        services.AddSingleton<ServiceCatalog>();
        services.AddSingleton<ServiceInspector>();
        services.AddSingleton<ChangeReader>();
        services.AddSingleton<DeployRequests>();
        services.AddSingleton<AdminLanguages>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<BotHandler>();
        services.AddSingleton<UpdateQueue>();
        services.AddSingleton<WebhookStatus>();

        services.AddHostedService<UpdateWorker>();
        services.AddHostedService<WebhookRegistrar>();
        services.AddHostedService<ResultWatcher>();
        services.AddHostedService<ServiceMonitor>();
        return services;
    }

    private static TelegramBotClient CreateBot(BotOptions options)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
        }

        return new TelegramBotClient(options.Token, new HttpClient(handler));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,256}$")]
    private static partial Regex SecretPattern();
}
