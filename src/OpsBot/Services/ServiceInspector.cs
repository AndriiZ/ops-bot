using System.Diagnostics;
using System.Globalization;

namespace OpsBot.Services;

/// <summary>The state of the systemd unit (systemctl show).</summary>
public sealed record UnitState(string LoadState, string ActiveState, string SubState, DateTimeOffset? Since, int Restarts)
{
    public bool IsActive => ActiveState == "active";
}

public enum HealthIssue
{
    None,
    HttpStatus,
    NoConnection,
    NoAnswer,
    WrongVersion,
}

/// <summary>The result of the health check: HEALTH_URL gives 2xx, and VERSION_URL shows the active version.</summary>
public sealed record HealthResult(HealthIssue Issue, int? Status = null, long? Milliseconds = null, string? Detail = null)
{
    public bool Ok => Issue == HealthIssue.None;
}

/// <summary>The state of a service: the releases, the artifacts, the unit and the health.</summary>
public sealed record ServiceState(
    ServiceDefinition Service,
    string? Active,
    IReadOnlyList<string> Releases,
    IReadOnlyList<string> Artifacts,
    UnitState? Unit,
    HealthResult Health)
{
    /// <summary>The newest artifact above the active version: the candidate for a deploy.</summary>
    public string? NewArtifact => Artifacts.FirstOrDefault(a => ReleaseVersion.IsNewer(a, Active));

    /// <summary>The newest release below the active version: the target of a rollback.</summary>
    public string? Previous => Active is null ? null : Releases.FirstOrDefault(r => ReleaseVersion.Compare(r, Active) < 0);

    /// <summary>The unit is active (if systemctl answers) and the health check is good.</summary>
    public bool Ok => Health.Ok && (Unit is null || Unit.IsActive);
}

internal sealed class ServiceInspector(IHttpClientFactory http, ISystemCommands system)
{
    public const string HttpClientName = "health";

    public async Task<ServiceState> InspectAsync(ServiceDefinition service, CancellationToken cancellationToken)
    {
        var active = ActiveVersion(service.Root);
        var unit = await UnitAsync(service.Unit, cancellationToken);
        var health = await HealthAsync(service, active, cancellationToken);
        return new ServiceState(service, active, Releases(service.Root), Artifacts(service), unit, health);
    }

    /// <summary>The version of the release that the link app shows.</summary>
    internal static string? ActiveVersion(string root)
    {
        var target = new DirectoryInfo(Path.Combine(root, "app")).LinkTarget;
        if (target is null)
        {
            return null;
        }

        var name = Path.GetFileName(target.TrimEnd('/', '\\'));
        return name.StartsWith('v') && ReleaseVersion.IsValid(name[1..]) ? name[1..] : null;
    }

    /// <summary>The versions of the releases v*, the newest first.</summary>
    internal static IReadOnlyList<string> Releases(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "v*")
                .Select(path => Path.GetFileName(path)[1..])
                .Where(ReleaseVersion.IsValid)
                .OrderByDescending(v => v, ReleaseVersion.Comparer)
                .ToList()
            : [];

    /// <summary>The versions of the complete artifacts (the file .sha256 comes last), the newest first.</summary>
    internal static IReadOnlyList<string> Artifacts(ServiceDefinition service)
    {
        if (!Directory.Exists(service.ArtifactsDirectory))
        {
            return [];
        }

        var prefix = service.Id + "-";
        const string suffix = ".tar.gz.sha256";
        return Directory.EnumerateFiles(service.ArtifactsDirectory, prefix + "*" + suffix)
            .Select(Path.GetFileName)
            .Select(name => name![prefix.Length..^suffix.Length])
            .Where(v => ReleaseVersion.IsValid(v) && File.Exists(service.Artifact(v)))
            .OrderByDescending(v => v, ReleaseVersion.Comparer)
            .ToList();
    }

    public Task<string?> JournalAsync(ServiceDefinition service, CancellationToken cancellationToken) =>
        system.RunAsync("journalctl", ["--unit", service.Unit, "--lines", "30", "--no-pager", "--output", "short-iso"], cancellationToken);

    private async Task<UnitState?> UnitAsync(string unit, CancellationToken cancellationToken)
    {
        var output = await system.RunAsync(
            "systemctl",
            ["show", unit, "--property=LoadState,ActiveState,SubState,ActiveEnterTimestamp,NRestarts", "--timestamp=unix"],
            cancellationToken);
        return output is null ? null : ParseUnit(output);
    }

    internal static UnitState ParseUnit(string output)
    {
        var values = output.Split('\n')
            .Select(line => line.Trim().Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);
        DateTimeOffset? since = values.GetValueOrDefault("ActiveEnterTimestamp") is ['@', .. var seconds]
            && long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
        return new UnitState(
            values.GetValueOrDefault("LoadState", "?"),
            values.GetValueOrDefault("ActiveState", "?"),
            values.GetValueOrDefault("SubState", "?"),
            since,
            int.TryParse(values.GetValueOrDefault("NRestarts"), CultureInfo.InvariantCulture, out var restarts) ? restarts : 0);
    }

    private async Task<HealthResult> HealthAsync(ServiceDefinition service, string? active, CancellationToken cancellationToken)
    {
        var client = http.CreateClient(HttpClientName);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(service.HealthUrl, cancellationToken);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new HealthResult(HealthIssue.HttpStatus, status);
            }

            var milliseconds = watch.ElapsedMilliseconds;
            if (service.VersionUrl is { } versionUrl && active is not null
                && !ShowsVersion(await client.GetStringAsync(versionUrl, cancellationToken), active))
            {
                return new HealthResult(HealthIssue.WrongVersion, status, Detail: active);
            }

            return new HealthResult(HealthIssue.None, status, milliseconds);
        }
        catch (HttpRequestException e)
        {
            return new HealthResult(HealthIssue.NoConnection, Detail: e.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthResult(HealthIssue.NoAnswer);
        }
    }

    /// <summary>The same check as the smoke test of deploy.sh: "version":"&lt;VERSION&gt;" without spaces.</summary>
    internal static bool ShowsVersion(string body, string version) =>
        string.Concat(body.Where(c => !char.IsWhiteSpace(c))).Contains($"\"version\":\"{version}\"", StringComparison.Ordinal);
}
