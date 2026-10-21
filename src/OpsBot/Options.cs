namespace OpsBot;

/// <summary>The settings of Telegram (section <c>Bot</c>).</summary>
public sealed class BotOptions
{
    public const string Section = "Bot";

    /// <summary>The token from @BotFather.</summary>
    public string Token { get; set; } = "";

    /// <summary>The public HTTPS address of <c>POST /telegram/webhook</c>. Empty: the bot does not register the webhook.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>The value of the header X-Telegram-Bot-Api-Secret-Token: 1–256 characters A-Z, a-z, 0-9, _ and -.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>The Telegram user IDs that manage the services. Other users get no access.</summary>
    public long[] AdminIds { get; set; } = [];

    /// <summary>Optional proxy for the Bot API, for example <c>socks5://127.0.0.1:1080</c>.</summary>
    public string? Proxy { get; set; }
}

/// <summary>The services on this host (section <c>Ops</c>, README.md §2).</summary>
public sealed class OpsOptions
{
    public const string Section = "Ops";

    /// <summary>The directory with the services: <c>&lt;root&gt;/&lt;id&gt;/{app,v*,data,configs,artifacts,bin}</c>.</summary>
    public string ServicesRoot { get; set; } = "/opt";

    /// <summary>The ids of the services that the bot manages, one in a line. Root owns the file: ops-runner reads it too.</summary>
    public string ServicesFile { get; set; } = "/opt/ops-bot/configs/services";

    /// <summary>The requests for ops-runner. The bot writes them; ops-runner (root) removes them.</summary>
    public string RequestsDirectory { get; set; } = "/opt/ops-bot/data/requests";

    /// <summary>The results of ops-runner: <c>&lt;id&gt;.log</c> and <c>&lt;id&gt;.done</c>.</summary>
    public string ResultsDirectory { get; set; } = "/opt/ops-bot/data/results";

    /// <summary>The state of the bot: the waiting requests, the languages, the reported artifacts.</summary>
    public string StateDirectory { get; set; } = "/opt/ops-bot/data/state";

    /// <summary>The time zone of the dates in the messages.</summary>
    public string TimeZone { get; set; } = "Europe/Kyiv";

    /// <summary>The interval of the checks: health and new artifacts.</summary>
    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The number of failed health checks one after the other before an alert.</summary>
    public int HealthFailuresToAlert { get; set; } = 3;

    /// <summary>The time limit of one health request.</summary>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A request without a result after this time is lost (ops-runner stops a deploy after 30 minutes).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(40);
}
