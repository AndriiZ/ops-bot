using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpsBot.Services;

/// <summary>A service of the standard layout (README.md §2) and the parameters of its configs/service.env.</summary>
public sealed record ServiceDefinition(string Id, string Root, string Title, string Unit, string HealthUrl, string? VersionUrl)
{
    public string ArtifactsDirectory => Path.Combine(Root, "artifacts");

    public string Artifact(string version) => Path.Combine(ArtifactsDirectory, $"{Id}-{version}.tar.gz");

    public string Release(string version) => Path.Combine(Root, "v" + version);
}

/// <summary>
/// The services from <see cref="OpsOptions.ServicesFile"/>. The bot reads the file at each use: a new service needs no restart.
/// </summary>
internal sealed partial class ServiceCatalog(IOptions<OpsOptions> options, ILogger<ServiceCatalog> logger)
{
    public IReadOnlyList<ServiceDefinition> All()
    {
        var file = options.Value.ServicesFile;
        if (!File.Exists(file))
        {
            return [];
        }

        var result = new List<ServiceDefinition>();
        foreach (var line in File.ReadAllLines(file))
        {
            var id = line.Trim();
            if (id.Length == 0 || id.StartsWith('#') || result.Any(s => s.Id == id))
            {
                continue;
            }

            if (!IsValidId(id))
            {
                LogWrongId(id, file);
                continue;
            }

            if (Load(id) is { } service)
            {
                result.Add(service);
            }
        }

        return result;
    }

    public ServiceDefinition? Find(string id) => IsValidId(id) ? All().FirstOrDefault(s => s.Id == id) : null;

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    private ServiceDefinition? Load(string id)
    {
        var root = Path.Combine(options.Value.ServicesRoot, id);
        var file = Path.Combine(root, "configs", "service.env");
        try
        {
            var values = ParseEnv(File.ReadAllText(file));
            var health = values.GetValueOrDefault("HEALTH_URL");
            if (string.IsNullOrEmpty(health))
            {
                LogNoHealth(file);
                return null;
            }

            return new ServiceDefinition(
                id,
                root,
                values.GetValueOrDefault("TITLE") is { Length: > 0 } title ? title : id,
                values.GetValueOrDefault("UNIT") is { Length: > 0 } unit ? unit : id,
                health,
                values.GetValueOrDefault("VERSION_URL") is { Length: > 0 } version ? version : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogNoFile(file, e.Message);
            return null;
        }
    }

    /// <summary>The lines KEY=VALUE of service.env. The file is data, not a shell script: no quotes, no expansion.</summary>
    internal static Dictionary<string, string> ParseEnv(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line.StartsWith('#') || equals <= 0)
            {
                continue;
            }

            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return values;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Wrong id of a service '{Id}' in {File}")]
    private partial void LogWrongId(string id, string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{File} has no HEALTH_URL")]
    private partial void LogNoHealth(string file);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot read {File}: {Error}")]
    private partial void LogNoFile(string file, string error);
}
