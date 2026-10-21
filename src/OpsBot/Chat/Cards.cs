using System.Text;
using OpsBot.Services;
using Telegram.Bot.Types.ReplyMarkups;

namespace OpsBot.Chat;

/// <summary>A message of the bot: HTML text and optional buttons.</summary>
internal sealed record Card(string Text, InlineKeyboardMarkup? Keyboard = null);

/// <summary>
/// The messages of the bot. The callback data: "o" overview, "s:id" service, "d:id:v" and "D:id:v" deploy (ask, confirm),
/// "r:id:v" and "R:id:v" rollback, "j:id" journal, "lang" other language. Telegram accepts up to 64 bytes.
/// </summary>
internal static class Cards
{
    private const int MaxChanges = 15;

    public static Card Overview(Texts t, IReadOnlyList<ServiceState> states, string servicesFile)
    {
        if (states.Count == 0)
        {
            return new Card(t.NoServices(Html.Escape(servicesFile)), new InlineKeyboardMarkup([[Button(t.Refresh, "o"), Button(t.OtherLanguage, "lang")]]));
        }

        var text = new StringBuilder($"<b>{t.Services}</b>\n");
        foreach (var state in states)
        {
            text.Append('\n').Append(state.Ok ? "🟢 " : "🔴 ").Append($"<b>{Html.Escape(state.Service.Title)}</b> · ")
                .Append(state.Active ?? t.NoRelease);
            if (state.NewArtifact is { } artifact)
            {
                text.Append(" · ").Append(t.NewArtifactShort(artifact));
            }
        }

        var rows = states.Select(s => new[] { Button(t.ServiceButton(s.Service.Title), "s:" + s.Service.Id) }).ToList();
        rows.Add([Button(t.Refresh, "o"), Button(t.OtherLanguage, "lang")]);
        return new Card(text.ToString(), new InlineKeyboardMarkup(rows));
    }

    public static Card Service(Texts t, ServiceState state, PendingRequest? pending, TimeZoneInfo zone)
    {
        var service = state.Service;
        var text = new StringBuilder()
            .Append(state.Ok ? "🟢 " : "🔴 ").Append($"<b>{Html.Escape(service.Title)}</b> · <code>{service.Id}</code>\n")
            .Append(t.Unit(state.Unit, zone)).Append('\n')
            .Append(t.Health(state.Health)).Append('\n')
            .Append(t.ActiveVersion(state.Active));
        var previous = state.Releases.Where(r => ReleaseVersion.Compare(r, state.Active) < 0).ToList();
        if (previous.Count > 0)
        {
            text.Append('\n').Append(t.PreviousReleases(previous));
        }

        if (state.NewArtifact is { } artifact)
        {
            text.Append('\n').Append(t.NewArtifact(artifact));
        }

        if (pending is not null)
        {
            text.Append("\n\n").Append(t.Waiting(pending));
        }

        var rows = new List<InlineKeyboardButton[]>();
        if (pending is null && state.NewArtifact is { } candidate)
        {
            rows.Add([Button(t.DeployButton(candidate), $"d:{service.Id}:{candidate}")]);
        }

        if (pending is null && state.Previous is { } target)
        {
            rows.Add([Button(t.RollbackButton(target), $"r:{service.Id}:{target}")]);
        }

        rows.Add([Button(t.JournalButton, "j:" + service.Id), Button(t.Refresh, "s:" + service.Id)]);
        rows.Add([Button(t.AllServices, "o")]);
        return new Card(text.ToString(), new InlineKeyboardMarkup(rows));
    }

    public static Card AskDeploy(Texts t, ServiceState state, string version, IReadOnlyList<Change>? changes)
    {
        var text = new StringBuilder(t.AskDeploy(Html.Escape(state.Service.Title), version, state.Active));
        AppendChanges(text, t, t.ChangesToDeploy, changes);
        return new Card(text.ToString(), new InlineKeyboardMarkup(
        [
            [Button(t.ConfirmDeploy, $"D:{state.Service.Id}:{version}")],
            [Button(t.Cancel, "s:" + state.Service.Id)],
        ]));
    }

    public static Card AskRollback(Texts t, ServiceState state, string version, IReadOnlyList<Change>? changes)
    {
        var text = new StringBuilder(t.AskRollback(Html.Escape(state.Service.Title), version, state.Active!));
        AppendChanges(text, t, t.ChangesToRemove, changes);
        return new Card(text.ToString(), new InlineKeyboardMarkup(
        [
            [Button(t.ConfirmRollback, $"R:{state.Service.Id}:{version}")],
            [Button(t.Cancel, "s:" + state.Service.Id)],
        ]));
    }

    public static Card Queued(Texts t, PendingRequest request, string title) =>
        new(t.Queued(request, Html.Escape(title)), new InlineKeyboardMarkup([[Button(t.ServiceButton(title), "s:" + request.Service)]]));

    /// <summary>The result of ops-runner: what happened, the cause of a failure, the changes, the journal.</summary>
    public static Card Result(Texts t, PendingRequest request, string title, int exitCode, DeployLog log)
    {
        var text = new StringBuilder(t.Headline(request, Html.Escape(title), exitCode));
        var done = exitCode == 0;
        if (!done)
        {
            text.Append("\n\n").Append(t.ReasonLine(t.Reason(log.Code), log.Detail));
        }

        var heading = request.Action == DeployAction.Deploy
            ? (done ? t.ChangesDeployed : t.ChangesNotDeployed)
            : (done ? t.ChangesRemoved : t.ChangesNotRemoved);
        AppendChanges(text, t, heading, request.Changes);

        if (!done && log.Journal.Length > 0)
        {
            text.Append("\n\n").Append(t.ServiceJournal).Append($"\n<pre>{Html.Escape(Html.Tail(log.Journal, 1500))}</pre>");
        }
        else if (!done && log.Output.Length > 0)
        {
            text.Append("\n\n").Append(t.DeployOutput).Append($"\n<pre>{Html.Escape(Html.Tail(log.Output, 1200))}</pre>");
        }

        return new Card(text.ToString(), new InlineKeyboardMarkup([[Button(t.ServiceButton(title), "s:" + request.Service)]]));
    }

    public static Card ArtifactArrived(Texts t, ServiceState state, string version, IReadOnlyList<Change>? changes)
    {
        var text = new StringBuilder(t.ArtifactArrived(Html.Escape(state.Service.Title), version, state.Active));
        AppendChanges(text, t, t.ChangesToDeploy, changes);
        return new Card(text.ToString(), new InlineKeyboardMarkup(
        [
            [Button(t.DeployButton(version), $"d:{state.Service.Id}:{version}")],
            [Button(t.ServiceButton(state.Service.Title), "s:" + state.Service.Id)],
        ]));
    }

    public static Card Journal(Texts t, ServiceDefinition service, string? journal) =>
        new(t.JournalTitle(Html.Escape(service.Title)) + "\n" + (journal is null
            ? t.NoJournal
            : $"<pre>{Html.Escape(Html.Tail(journal.Trim(), 3500))}</pre>"));

    /// <summary>"Heading (N):" and the newest changes. Null: the release has no CHANGES.json.</summary>
    internal static void AppendChanges(StringBuilder text, Texts t, string heading, IReadOnlyList<Change>? changes)
    {
        text.Append("\n\n");
        if (changes is null)
        {
            text.Append(t.NoChangeList);
            return;
        }

        if (changes.Count == 0)
        {
            text.Append(t.NoChanges);
            return;
        }

        text.Append($"<b>{heading}</b> ({changes.Count}):");
        foreach (var change in changes.Take(MaxChanges))
        {
            text.Append("\n• ").Append(Html.Escape(Html.Cut(t.ChangeText(change), 200)));
        }

        if (changes.Count > MaxChanges)
        {
            text.Append('\n').Append(t.More(changes.Count - MaxChanges));
        }
    }

    private static InlineKeyboardButton Button(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);
}
