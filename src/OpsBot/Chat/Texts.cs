using System.Globalization;
using OpsBot.Services;

namespace OpsBot.Chat;

public enum Language
{
    Uk,
    En,
}

/// <summary>The texts of the bot in Ukrainian and in English. Each administrator has a language (<see cref="AdminLanguages"/>).</summary>
internal sealed class Texts
{
    public static readonly Texts Uk = new(Language.Uk);
    public static readonly Texts En = new(Language.En);

    private readonly bool en;

    private Texts(Language language)
    {
        Language = language;
        en = language == Language.En;
    }

    public Language Language { get; }

    public static Texts For(Language language) => language == Language.En ? En : Uk;

    private string T(string uk, string english) => en ? english : uk;

    // Commands and buttons.
    public string CommandStatus => T("Стан сервісів", "State of the services");
    public string CommandHelp => T("Довідка", "Help");
    public string Refresh => T("🔄 Оновити", "🔄 Refresh");
    public string AllServices => T("◀ Усі сервіси", "◀ All services");
    public string JournalButton => T("📜 Журнал", "📜 Journal");
    public string OtherLanguage => en ? "🌐 Українська" : "🌐 English";
    public string Cancel => T("✖ Ні", "✖ No");
    public string DeployButton(string version) => T($"🚀 Розгорнути {version}", $"🚀 Deploy {version}");
    public string RollbackButton(string version) => T($"↩️ Відкотити на {version}", $"↩️ Roll back to {version}");
    public string ConfirmDeploy => T("✅ Так, розгорнути", "✅ Yes, deploy");
    public string ConfirmRollback => T("✅ Так, відкотити", "✅ Yes, roll back");
    public string ServiceButton(string title) => T($"📊 {title}", $"📊 {title}");

    // Access and help.
    public static string NoAccess(long userId) =>
        $"⛔ Немає доступу. Ваш Telegram ID: <code>{userId}</code>\n⛔ No access. Your Telegram ID: <code>{userId}</code>";

    public string NoAccessShort => T("⛔ Немає доступу", "⛔ No access");

    public string Help => T(
        "Бот керує сервісами цього хоста за стандартом розгортання: показує стан, розгортає нові версії з artifacts/ і відкочує на попередні.\n\n/status — стан сервісів\n/help — довідка",
        "The bot manages the services of this host by the deploy standard: it shows the state, deploys new versions from artifacts/ and rolls back to previous versions.\n\n/status — state of the services\n/help — help");

    // The overview and the card.
    public string Services => T("Сервіси", "Services");
    public string NoServices(string file) => T($"Немає сервісів. Додайте id у {file}.", $"No services. Add the ids to {file}.");
    public string NoRelease => T("немає релізу", "no release");
    public string NewArtifactShort(string version) => T($"⬆️ доступна {version}", $"⬆️ {version} available");
    public string Unit(UnitState? unit, TimeZoneInfo zone)
    {
        if (unit is null)
        {
            return T("Юніт: systemctl не відповідає", "Unit: systemctl gives no answer");
        }

        if (unit.LoadState == "not-found")
        {
            return T("Юніт: не встановлено", "Unit: not installed");
        }

        var since = unit.Since is { } time ? T(" з ", " since ") + Date(time, zone) : "";
        return T("Юніт: ", "Unit: ") + $"{unit.ActiveState} ({unit.SubState}){since}" + T($", перезапусків {unit.Restarts}", $", restarts {unit.Restarts}");
    }

    public string Health(HealthResult health) => "Health: " + health.Issue switch
    {
        HealthIssue.None => T($"✅ HTTP {health.Status} за {health.Milliseconds} мс", $"✅ HTTP {health.Status} in {health.Milliseconds} ms"),
        HealthIssue.HttpStatus => $"❌ HTTP {health.Status}",
        HealthIssue.NoConnection => T("❌ немає з'єднання", "❌ no connection"),
        HealthIssue.NoAnswer => T("❌ немає відповіді", "❌ no answer"),
        HealthIssue.WrongVersion => T($"❌ працює не версія {health.Detail}", $"❌ the running version is not {health.Detail}"),
        _ => "?",
    };

    public string ActiveVersion(string? version) => T("Версія: ", "Version: ") + (version is null ? NoRelease : $"<b>{version}</b>");
    public string PreviousReleases(IEnumerable<string> versions) => T("Попередні: ", "Previous: ") + string.Join(", ", versions);
    public string NewArtifact(string version) => T($"📦 Новий артефакт: <b>{version}</b>", $"📦 New artifact: <b>{version}</b>");
    public string Waiting(PendingRequest request) => request.Action == DeployAction.Deploy
        ? T($"⏳ Виконується розгортання {request.Version}", $"⏳ Deploy of {request.Version} is running")
        : T($"⏳ Виконується відкат на {request.Version}", $"⏳ Rollback to {request.Version} is running");

    public string JournalTitle(string title) => T($"<b>{title}</b> — журнал", $"<b>{title}</b> — journal");
    public string NoJournal => T("Журнал недоступний (journalctl не відповідає).", "The journal is not available (journalctl gives no answer).");

    // Before a deploy or a rollback.
    public string AskDeploy(string title, string version, string? active) => active is null
        ? T($"Розгорнути <b>{title}</b> {version}? Активного релізу немає.", $"Deploy <b>{title}</b> {version}? There is no active release.")
        : T($"Розгорнути <b>{title}</b> {version}? Зараз працює {active}.", $"Deploy <b>{title}</b> {version}? Now {active} runs.");

    public string AskRollback(string title, string version, string active) =>
        T($"Відкотити <b>{title}</b> на {version}? Зараз працює {active}.", $"Roll back <b>{title}</b> to {version}? Now {active} runs.");

    public string ChangesToDeploy => T("Зміни", "Changes");
    public string ChangesToRemove => T("Буде скасовано", "To be removed");
    public string ChangesDeployed => T("Зміни", "Changes");
    public string ChangesNotDeployed => T("Не розгорнуто", "Not deployed");
    public string ChangesRemoved => T("Скасовано", "Removed");
    public string ChangesNotRemoved => T("Не скасовано", "Not removed");
    public string NoChangeList => T("Список змін недоступний.", "The list of changes is not available.");
    public string NoChanges => T("Змін у цьому сервісі немає.", "No changes in this service.");
    public string More(int count) => T($"… і ще {count}", $"… and {count} more");

    public string Queued(PendingRequest request, string title) => request.Action == DeployAction.Deploy
        ? T($"⏳ Розгортання <b>{title}</b> {request.Version} у черзі. Результат прийде окремим повідомленням.",
            $"⏳ Deploy of <b>{title}</b> {request.Version} is in the queue. The result comes in a separate message.")
        : T($"⏳ Відкат <b>{title}</b> на {request.Version} у черзі. Результат прийде окремим повідомленням.",
            $"⏳ Rollback of <b>{title}</b> to {request.Version} is in the queue. The result comes in a separate message.");

    public string Busy => T("Для цього сервісу вже виконується запит.", "A request for this service is already running.");
    public string Outdated => T("Стан змінився, оновіть картку.", "The state changed, refresh the card.");
    public string UnknownService => T("Сервісу немає в списку.", "The service is not in the list.");

    // After a deploy or a rollback.
    public string Headline(PendingRequest request, string title, int exitCode)
    {
        var (v, from) = (request.Version, request.From ?? "—");
        return (request.Action, exitCode) switch
        {
            (DeployAction.Deploy, 0) => T($"✅ <b>{title}</b>: розгорнуто {v} (було {from}).", $"✅ <b>{title}</b>: {v} is deployed (was {from})."),
            (DeployAction.Deploy, 1) => T($"❌ <b>{title}</b>: розгортання {v} не виконано, сервіс не змінено.", $"❌ <b>{title}</b>: deploy of {v} did not run, the service did not change."),
            (DeployAction.Deploy, 2) => T($"⚠️ <b>{title}</b>: розгортання {v} не вдалося, повернуто {from}.", $"⚠️ <b>{title}</b>: deploy of {v} failed, {from} runs again."),
            (DeployAction.Rollback, 0) => T($"↩️ <b>{title}</b>: відкочено на {v} (було {from}).", $"↩️ <b>{title}</b>: rolled back to {v} (was {from})."),
            (DeployAction.Rollback, 1) => T($"❌ <b>{title}</b>: відкат на {v} не виконано, сервіс не змінено.", $"❌ <b>{title}</b>: rollback to {v} did not run, the service did not change."),
            (DeployAction.Rollback, 2) => T($"⚠️ <b>{title}</b>: реліз {v} не працює, повернуто {from}.", $"⚠️ <b>{title}</b>: release {v} does not work, {from} runs again."),
            (_, 3) => T($"🆘 <b>{title}</b>: сервіс не працює після {(request.Action == DeployAction.Deploy ? "розгортання" : "відкату")} {v}. Потрібне втручання.",
                $"🆘 <b>{title}</b>: the service does not work after the {(request.Action == DeployAction.Deploy ? "deploy" : "rollback")} of {v}. Action is necessary."),
            (_, 124 or 137) => T($"⌛ <b>{title}</b>: {v} — час вийшов, перевірте стан.", $"⌛ <b>{title}</b>: {v} — the time limit passed, examine the state."),
            (_, 126) => T($"❌ <b>{title}</b>: ops-runner відхилив запит {v}.", $"❌ <b>{title}</b>: ops-runner rejected the request {v}."),
            _ => T($"❌ <b>{title}</b>: {v} — код завершення {exitCode}.", $"❌ <b>{title}</b>: {v} — exit code {exitCode}."),
        };
    }

    /// <summary>The cause of a failure by the code of "@result" (template/deploy.sh).</summary>
    public string Reason(string? code) => code switch
    {
        "locked" => T("інший деплой цього сервісу ще виконується", "another deploy of this service is running"),
        "no-artifact" => T("немає файлу артефакту", "the artifact file is missing"),
        "no-checksum" => T("немає файлу контрольної суми", "the checksum file is missing"),
        "checksum" => T("контрольна сума не збігається", "the checksum does not match"),
        "extract" => T("архів пошкоджений", "the archive is damaged"),
        "not-a-release" => T("архів не є релізом цього сервісу", "the archive is not a release of this service"),
        "active" => T("ця версія вже активна", "this version is already active"),
        "exists" => T("реліз цієї версії вже є на сервері", "a release of this version is already on the server"),
        "not-newer" => T("версія не новіша за активну", "the version is not newer than the active version"),
        "config-missing" => T("немає файлу конфігурації", "a configuration file is missing"),
        "hook-failed" => T("підготовка релізу (pre-switch) не вдалася", "the preparation of the release (pre-switch) failed"),
        "rolled-back" or "target-failed" => T("смоктест не пройдено", "the smoke test failed"),
        "first-failed" => T("смоктест не пройдено; попереднього релізу немає, сервіс зупинено", "the smoke test failed; there is no previous release, the service is stopped"),
        "both-failed" => T("смоктест не пройдено, і попередній реліз теж не працює", "the smoke test failed, and the previous release also does not work"),
        "no-active" => T("немає активного релізу", "there is no active release"),
        "no-previous" => T("немає попереднього релізу", "there is no previous release"),
        "no-release" => T("немає такого релізу", "there is no such release"),
        "rejected" => T("запит не пройшов перевірку ops-runner", "the request did not pass the checks of ops-runner"),
        "timeout" => T("розгортання не завершилося вчасно", "the deploy did not end in time"),
        "not-root" or "no-config" or "bad-config" or "usage" => T("помилка налаштування deploy.sh", "deploy.sh is not configured correctly"),
        null => T("невідома (deploy.sh не дав результату)", "unknown (deploy.sh gave no result)"),
        _ => code,
    };

    public string ReasonLine(string reason, string? detail) =>
        T("Причина: ", "Cause: ") + Html.Escape(reason) + (detail is null ? "" : $": <code>{Html.Escape(Html.Cut(detail, 300))}</code>");

    public string ServiceJournal => T("Журнал сервісу:", "Journal of the service:");
    public string DeployOutput => T("Вивід deploy.sh:", "Output of deploy.sh:");
    public string Lost(PendingRequest request, string title) =>
        T($"⌛ <b>{title}</b>: запит {request.Version} без результату — ops-runner не відповів. Перевірте ops-runner.path на хості.",
          $"⌛ <b>{title}</b>: the request {request.Version} has no result — ops-runner gave no answer. Examine ops-runner.path on the host.");

    // The monitoring.
    public string Down(string title, string health) => T($"🔴 <b>{title}</b> не працює. {health}", $"🔴 <b>{title}</b> does not work. {health}");
    public string Up(string title) => T($"🟢 <b>{title}</b> знову працює.", $"🟢 <b>{title}</b> works again.");
    public string ArtifactArrived(string title, string version, string? active) => active is null
        ? T($"📦 <b>{title}</b>: новий артефакт {version}.", $"📦 <b>{title}</b>: new artifact {version}.")
        : T($"📦 <b>{title}</b>: новий артефакт {version} (працює {active}).", $"📦 <b>{title}</b>: new artifact {version} ({active} runs).");

    public string ChangeText(Change change) => en ? change.En : change.Uk;

    public static string Date(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
}
