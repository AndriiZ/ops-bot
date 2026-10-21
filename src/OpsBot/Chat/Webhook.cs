using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpsBot.Chat;

/// <summary>
/// POST /telegram/webhook: examines the secret token, puts the update in the queue and gives 200 immediately.
/// The reverse proxy forwards only this path.
/// </summary>
internal static class WebhookEndpoint
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";
    private const long MaxBodyBytes = 1024 * 1024;

    public static async Task<IResult> HandleAsync(HttpRequest request, IOptions<BotOptions> options, UpdateQueue queue, CancellationToken cancellationToken)
    {
        if (!IsSecretValid(request.Headers[SecretHeader].ToString(), options.Value.WebhookSecret))
        {
            return Results.Unauthorized();
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        Update? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<Update>(request.Body, JsonBotAPI.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (update is null)
        {
            return Results.BadRequest();
        }

        queue.Enqueue(update);
        return Results.Ok();
    }

    internal static bool IsSecretValid(string received, string expected) =>
        expected.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));
}

/// <summary>The updates between the webhook and <see cref="UpdateWorker"/>. When the queue is full, the oldest update goes.</summary>
internal sealed class UpdateQueue
{
    private readonly Channel<Update> channel = Channel.CreateBounded<Update>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(Update update) => channel.Writer.TryWrite(update);

    public IAsyncEnumerable<Update> ReadAllAsync(CancellationToken cancellationToken) => channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Processes the updates one after the other. An error of one update does not stop the next updates.</summary>
internal sealed partial class UpdateWorker(UpdateQueue queue, BotHandler handler, ILogger<UpdateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var update in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await handler.HandleAsync(update, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(e, update.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The update {UpdateId} failed")]
    private partial void LogFailed(Exception exception, int updateId);
}

/// <summary>The state of the webhook registration for /health.</summary>
internal sealed class WebhookStatus
{
    private volatile string text = "pending";

    public string Text
    {
        get => text;
        set => text = value;
    }
}

/// <summary>
/// At start, registers the webhook (with secret_token) and the commands. After an error, it tries again each minute.
/// </summary>
internal sealed partial class WebhookRegistrar(
    ITelegramBotClient bot,
    IOptions<BotOptions> options,
    WebhookStatus status,
    ILogger<WebhookRegistrar> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (config.WebhookUrl.Length == 0)
        {
            status.Text = "disabled";
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await bot.SetWebhook(config.WebhookUrl, allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
                    secretToken: config.WebhookSecret, cancellationToken: stoppingToken);
                foreach (var (t, code) in new[] { (Texts.Uk, (string?)null), (Texts.En, "en") })
                {
                    await bot.SetMyCommands(
                        [new BotCommand("status", t.CommandStatus), new BotCommand("help", t.CommandHelp)],
                        new BotCommandScopeAllPrivateChats(), code, stoppingToken);
                }

                status.Text = "ok";
                LogRegistered(config.WebhookUrl);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(e);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The webhook is registered: {Url}")]
    private partial void LogRegistered(string url);

    [LoggerMessage(Level = LogLevel.Error, Message = "The webhook registration failed; the next attempt in a minute")]
    private partial void LogFailed(Exception exception);
}
