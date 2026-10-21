using Microsoft.Extensions.Options;
using OpsBot.Services;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace OpsBot.Chat;

/// <summary>The commands and the buttons of the administrators. Other users get no access.</summary>
internal sealed class BotHandler(
    ITelegramBotClient bot,
    IOptions<BotOptions> botOptions,
    IOptions<OpsOptions> opsOptions,
    ServiceCatalog catalog,
    ServiceInspector inspector,
    ChangeReader changes,
    DeployRequests requests,
    AdminLanguages languages)
{
    private readonly TimeZoneInfo zone = FindZone(opsOptions.Value.TimeZone);

    public Task HandleAsync(Update update, CancellationToken cancellationToken) => update switch
    {
        { Message: { } message } => OnMessageAsync(message, cancellationToken),
        { CallbackQuery: { } query } => OnCallbackAsync(query, cancellationToken),
        _ => Task.CompletedTask,
    };

    private bool IsAdmin(long userId) => botOptions.Value.AdminIds.Contains(userId);

    private async Task OnMessageAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.Chat.Type != ChatType.Private || message.From is not { } user || message.Text is not { } text)
        {
            return;
        }

        var command = text.Split(' ', 2)[0].Split('@')[0];
        if (!IsAdmin(user.Id))
        {
            // The ID helps the owner to add an administrator to Bot:AdminIds.
            if (command == "/start")
            {
                await SendAsync(message.Chat.Id, new Card(Texts.NoAccess(user.Id)), cancellationToken);
            }

            return;
        }

        var t = Texts.For(languages.Get(user.Id, user.LanguageCode));
        var card = command switch
        {
            "/start" or "/status" => await OverviewAsync(t, cancellationToken),
            _ => new Card(t.Help),
        };
        await SendAsync(message.Chat.Id, card, cancellationToken);
    }

    private async Task OnCallbackAsync(CallbackQuery query, CancellationToken cancellationToken)
    {
        if (!IsAdmin(query.From.Id) || query.Message is not { } message || query.Data is not { } data)
        {
            await bot.AnswerCallbackQuery(query.Id, Texts.Uk.NoAccessShort, cancellationToken: cancellationToken);
            return;
        }

        var t = Texts.For(languages.Get(query.From.Id, query.From.LanguageCode));
        string? notice = null;
        switch (data.Split(':'))
        {
            case ["o"]:
                await EditAsync(message, await OverviewAsync(t, cancellationToken), cancellationToken);
                break;
            case ["lang"]:
                t = Texts.For(t.Language == Language.Uk ? Language.En : Language.Uk);
                languages.Set(query.From.Id, t.Language);
                await EditAsync(message, await OverviewAsync(t, cancellationToken), cancellationToken);
                break;
            case ["s", var id]:
                notice = await ShowServiceAsync(t, message, id, cancellationToken);
                break;
            case ["j", var id]:
                if (catalog.Find(id) is { } service)
                {
                    await SendAsync(message.Chat.Id, Cards.Journal(t, service, await inspector.JournalAsync(service, cancellationToken)), cancellationToken);
                }

                break;
            case ["d" or "D" or "r" or "R", var id, var version]:
                notice = await DeployOrRollbackAsync(t, message, query.From.Id, data[0], id, version, cancellationToken);
                break;
        }

        await bot.AnswerCallbackQuery(query.Id, notice, cancellationToken: cancellationToken);
    }

    private async Task<string?> ShowServiceAsync(Texts t, Message message, string id, CancellationToken cancellationToken)
    {
        if (catalog.Find(id) is not { } service)
        {
            return t.UnknownService;
        }

        var state = await inspector.InspectAsync(service, cancellationToken);
        await EditAsync(message, Cards.Service(t, state, Pending(id), zone), cancellationToken);
        return null;
    }

    /// <summary>"d"/"r" ask with the list of changes; "D"/"R" write the request for ops-runner.</summary>
    private async Task<string?> DeployOrRollbackAsync(
        Texts t, Message message, long userId, char step, string id, string version, CancellationToken cancellationToken)
    {
        if (catalog.Find(id) is not { } service)
        {
            return t.UnknownService;
        }

        if (Pending(id) is not null)
        {
            return t.Busy;
        }

        var state = await inspector.InspectAsync(service, cancellationToken);
        var deploy = step is 'd' or 'D';
        // The state can change after the card: the version must still be a correct target.
        var valid = ReleaseVersion.IsValid(version) && (deploy
            ? state.Artifacts.Contains(version) && ReleaseVersion.IsNewer(version, state.Active)
            : state.Active is not null && version != state.Active && state.Releases.Contains(version));
        if (!valid)
        {
            await EditAsync(message, Cards.Service(t, state, null, zone), cancellationToken);
            return t.Outdated;
        }

        var list = deploy
            ? changes.ForDeploy(service, state.Active, version)
            : changes.ForRollback(service, state.Active!, version);
        if (step is 'd' or 'r')
        {
            await EditAsync(message, deploy ? Cards.AskDeploy(t, state, version, list) : Cards.AskRollback(t, state, version, list), cancellationToken);
            return null;
        }

        var action = deploy ? DeployAction.Deploy : DeployAction.Rollback;
        if (requests.Create(action, id, version, state.Active, list, userId) is not { } request)
        {
            return t.Busy;
        }

        await EditAsync(message, Cards.Queued(t, request, service.Title), cancellationToken);
        return null;
    }

    private async Task<Card> OverviewAsync(Texts t, CancellationToken cancellationToken)
    {
        var states = new List<ServiceState>();
        foreach (var service in catalog.All())
        {
            states.Add(await inspector.InspectAsync(service, cancellationToken));
        }

        return Cards.Overview(t, states, opsOptions.Value.ServicesFile);
    }

    private PendingRequest? Pending(string id) => requests.Pending().FirstOrDefault(p => p.Service == id);

    private Task SendAsync(long chatId, Card card, CancellationToken cancellationToken) =>
        bot.SendMessage(chatId, card.Text, ParseMode.Html, replyMarkup: card.Keyboard, linkPreviewOptions: Notifier.NoPreview,
            cancellationToken: cancellationToken);

    private async Task EditAsync(Message message, Card card, CancellationToken cancellationToken)
    {
        try
        {
            await bot.EditMessageText(message.Chat.Id, message.MessageId, card.Text, ParseMode.Html, replyMarkup: card.Keyboard,
                linkPreviewOptions: Notifier.NoPreview, cancellationToken: cancellationToken);
        }
        catch (ApiRequestException e) when (e.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            // The card did not change after "Refresh".
        }
    }

    internal static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
