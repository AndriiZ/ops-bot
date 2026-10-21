using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpsBot.Services;

public enum DeployAction
{
    Deploy,
    Rollback,
}

/// <summary>
/// A request that waits for ops-runner. The bot keeps the versions and the changes of the request:
/// after a deploy, the release of <see cref="From"/> can be already removed.
/// </summary>
public sealed record PendingRequest(
    string Id,
    DeployAction Action,
    string Service,
    string Version,
    string? From,
    IReadOnlyList<Change>? Changes,
    long UserId,
    DateTimeOffset CreatedAt);

/// <summary>
/// The requests for ops-runner (README.md §4). The bot has no root rights: it writes a file
/// data/requests/&lt;id&gt;.req, and the unit ops-runner.path starts ops-runner as root.
/// </summary>
internal sealed partial class DeployRequests(IOptions<OpsOptions> options, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();

    private string PendingDirectory => Path.Combine(options.Value.StateDirectory, "pending");

    /// <summary>Writes the request. Null if a request for this service waits.</summary>
    public PendingRequest? Create(DeployAction action, string service, string version, string? from, IReadOnlyList<Change>? changes, long userId)
    {
        lock (gate)
        {
            if (Pending().Any(p => p.Service == service))
            {
                return null;
            }

            var now = time.GetUtcNow();
            var id = $"{now:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6, lowercase: true)}";
            var request = new PendingRequest(id, action, service, version, from, changes, userId, now);
            Directory.CreateDirectory(PendingDirectory);
            File.WriteAllText(Path.Combine(PendingDirectory, id + ".json"), JsonSerializer.Serialize(request, Json));

            // The name with a dot and .tmp does not match ops-runner.path; the rename gives the full file in one step.
            var directory = options.Value.RequestsDirectory;
            var temporary = Path.Combine(directory, $".{id}.tmp");
            var verb = action == DeployAction.Deploy ? "deploy" : "rollback";
            File.WriteAllText(temporary, $"{verb} {service} {version}\n");
            File.Move(temporary, Path.Combine(directory, id + ".req"));
            return request;
        }
    }

    public IReadOnlyList<PendingRequest> Pending()
    {
        if (!Directory.Exists(PendingDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(PendingDirectory, "*.json")
            .Select(Read)
            .OfType<PendingRequest>()
            .OrderBy(p => p.CreatedAt)
            .ToList();
    }

    public PendingRequest? Find(string id) =>
        IdPattern().IsMatch(id) ? Read(Path.Combine(PendingDirectory, id + ".json")) : null;

    /// <summary>The requests without a result after <see cref="OpsOptions.RequestTimeout"/>.</summary>
    public IReadOnlyList<PendingRequest> Expired()
    {
        var limit = time.GetUtcNow() - options.Value.RequestTimeout;
        return Pending().Where(p => p.CreatedAt < limit).ToList();
    }

    /// <summary>Removes the request: ops-runner gave the result, or the request is lost.</summary>
    public void Complete(string id)
    {
        if (!IdPattern().IsMatch(id))
        {
            return;
        }

        File.Delete(Path.Combine(PendingDirectory, id + ".json"));
        // A lost request must not run later.
        File.Delete(Path.Combine(options.Value.RequestsDirectory, id + ".req"));
    }

    private static PendingRequest? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PendingRequest>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[0-9]{14}-[0-9a-f]{6}$")]
    private static partial Regex IdPattern();
}
