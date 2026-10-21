using Microsoft.Extensions.Options;
using OpsBot.Services;

namespace OpsBot.Chat;

/// <summary>
/// Sends the results of ops-runner: data/results/&lt;id&gt;.done (the exit code) and &lt;id&gt;.log (the output).
/// After a deploy of ops-bot itself, the new release sends the result.
/// </summary>
internal sealed partial class ResultWatcher(
    DeployRequests requests,
    ServiceCatalog catalog,
    Notifier notifier,
    IOptions<OpsOptions> options,
    TimeProvider time,
    ILogger<ResultWatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ScanAsync(CancellationToken cancellationToken)
    {
        var directory = options.Value.ResultsDirectory;
        if (Directory.Exists(directory))
        {
            foreach (var done in Directory.EnumerateFiles(directory, "*.done").ToList())
            {
                var id = Path.GetFileNameWithoutExtension(done);
                var logFile = Path.Combine(directory, id + ".log");
                var code = int.TryParse(File.ReadAllText(done).Trim(), out var value) ? value : -1;
                var log = DeployLog.Parse(File.Exists(logFile) ? File.ReadAllText(logFile) : "");
                // Without the pending request (for example, a request from another copy of the state), only the id is known.
                var request = requests.Find(id) ?? new PendingRequest(id, DeployAction.Deploy, "?", id, null, null, 0, time.GetUtcNow());
                var title = catalog.Find(request.Service)?.Title ?? request.Service;
                await notifier.BroadcastAsync(t => Cards.Result(t, request, title, code, log), cancellationToken);
                File.Delete(done);
                File.Delete(logFile);
                requests.Complete(id);
            }
        }

        foreach (var lost in requests.Expired())
        {
            var title = catalog.Find(lost.Service)?.Title ?? lost.Service;
            await notifier.BroadcastAsync(t => new Card(t.Lost(lost, Html.Escape(title))), cancellationToken);
            requests.Complete(lost.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The scan of the results failed")]
    private partial void LogFailed(Exception exception);
}
