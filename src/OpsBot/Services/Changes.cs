using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace OpsBot.Services;

/// <summary>A change of CHANGES.json (template/changes.sh): a commit with the text in Ukrainian and in English.</summary>
public sealed record Change(long Build, string Commit, string Date, string Uk, string En);

public sealed record ChangeSet(string Version, string? Commit, IReadOnlyList<Change> Changes)
{
    /// <summary>The changes after version <paramref name="from"/> up to version <paramref name="to"/>, the newest first.</summary>
    public IReadOnlyList<Change> Between(string? from, string to)
    {
        var low = from is null ? long.MinValue : ReleaseVersion.Build(from);
        var high = ReleaseVersion.Build(to);
        return Changes.Where(c => c.Build > low && c.Build <= high).ToList();
    }
}

/// <summary>
/// Reads CHANGES.json from an artifact (before a deploy) or from a release (before a rollback).
/// The releases are readable for all users (deploy.sh); the bot has no other rights.
/// </summary>
internal sealed partial class ChangeReader(ILogger<ChangeReader> logger)
{
    public const string FileName = "CHANGES.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<(string Path, DateTime Time, long Length), ChangeSet?> cache = new();

    /// <summary>
    /// The changes between the active version and the new version. Null if the artifact has no CHANGES.json.
    /// </summary>
    public IReadOnlyList<Change>? ForDeploy(ServiceDefinition service, string? active, string version) =>
        FromArtifact(service.Artifact(version))?.Between(active, version);

    /// <summary>The changes that a rollback from <paramref name="active"/> to <paramref name="target"/> removes.</summary>
    public IReadOnlyList<Change>? ForRollback(ServiceDefinition service, string active, string target) =>
        (FromFile(Path.Combine(service.Release(active), FileName)) ?? FromArtifact(service.Artifact(active)))
            ?.Between(target, active);

    internal ChangeSet? FromArtifact(string path) => Cached(path, () =>
    {
        // build.sh puts VERSION and CHANGES.json first: the bot does not read the full archive.
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.Name is FileName or "./" + FileName && entry.DataStream is { } data)
            {
                return JsonSerializer.Deserialize<ChangeSet>(data, Json);
            }
        }

        return null;
    });

    internal ChangeSet? FromFile(string path) => Cached(path, () =>
    {
        using var file = File.OpenRead(path);
        return JsonSerializer.Deserialize<ChangeSet>(file, Json);
    });

    private ChangeSet? Cached(string path, Func<ChangeSet?> read)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return cache.GetOrAdd((path, info.LastWriteTimeUtc, info.Length), _ => read());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LogUnreadable(path, e.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot read the changes of {Path}: {Error}")]
    private partial void LogUnreadable(string path, string error);
}
