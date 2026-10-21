using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpsBot.Chat;

/// <summary>Sends the alerts and the results to all administrators, each in the language of the administrator.</summary>
internal sealed partial class Notifier(
    ITelegramBotClient bot,
    IOptions<BotOptions> options,
    AdminLanguages languages,
    ILogger<Notifier> logger)
{
    public static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    public async Task BroadcastAsync(Func<Texts, Card> message, CancellationToken cancellationToken)
    {
        foreach (var admin in options.Value.AdminIds)
        {
            var card = message(Texts.For(languages.Get(admin)));
            try
            {
                await bot.SendMessage(admin, card.Text, ParseMode.Html, replyMarkup: card.Keyboard, linkPreviewOptions: NoPreview,
                    cancellationToken: cancellationToken);
            }
            catch (ApiRequestException e)
            {
                // An administrator who did not start the bot cannot get messages.
                LogFailed(admin, e.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot send a message to the administrator {AdminId}: {Error}")]
    private partial void LogFailed(long adminId, string error);
}
