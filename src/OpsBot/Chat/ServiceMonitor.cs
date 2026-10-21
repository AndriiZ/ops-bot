using Microsoft.Extensions.Options;
using OpsBot.Services;

namespace OpsBot.Chat;

/// <summary>
/// Each <see cref="OpsOptions.MonitorInterval"/>: an alert after <see cref="OpsOptions.HealthFailuresToAlert"/> failed
/// checks one after the other and a message on recovery; a message with the changes for each new artifact.
/// During a request for the service, the health of the service is not examined.
/// </summary>
internal sealed partial class ServiceMonitor(
    ServiceCatalog catalog,
    ServiceInspector inspector,
    ChangeReader changes,
    DeployRequests requests,
    Notifier notifier,
    IOptions<OpsOptions> options,
    TimeProvider time,
    ILogger<ServiceMonitor> logger) : BackgroundService
{
    private readonly Dictionary<string, int> failures = [];
    private readonly HashSet<string> down = [];

    private string ReportedFile => Path.Combine(options.Value.StateDirectory, "reported-artifacts");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.MonitorInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(e);
            }
        }
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var reported = File.Exists(ReportedFile) ? File.ReadAllLines(ReportedFile).ToHashSet() : [];
        var reportedCount = reported.Count;
        var busy = requests.Pending().Select(p => p.Service).ToHashSet();
        foreach (var service in catalog.All())
        {
            var state = await inspector.InspectAsync(service, cancellationToken);
            var title = Html.Escape(service.Title);
            if (!busy.Contains(service.Id))
            {
                if (state.Ok)
                {
                    failures[service.Id] = 0;
                    if (down.Remove(service.Id))
                    {
                        await notifier.BroadcastAsync(t => new Card(t.Up(title)), cancellationToken);
                    }
                }
                else if ((failures[service.Id] = failures.GetValueOrDefault(service.Id) + 1) == options.Value.HealthFailuresToAlert)
                {
                    down.Add(service.Id);
                    await notifier.BroadcastAsync(t => new Card(t.Down(title, t.Health(state.Health))), cancellationToken);
                }
            }

            if (state.NewArtifact is { } version && reported.Add($"{service.Id} {version}"))
            {
                var list = changes.ForDeploy(service, state.Active, version);
                await notifier.BroadcastAsync(t => Cards.ArtifactArrived(t, state, version, list), cancellationToken);
            }
        }

        if (reported.Count != reportedCount)
        {
            Directory.CreateDirectory(options.Value.StateDirectory);
            // The last 500 lines are enough: deploy.sh removes the old artifacts.
            File.WriteAllLines(ReportedFile, reported.TakeLast(500));
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The check of the services failed")]
    private partial void LogFailed(Exception exception);
}
