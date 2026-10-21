using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace OpsBot.Tests;

/// <summary>
/// A temporary copy of the host layout (README.md §2): /opt/&lt;id&gt;/{app,v*,configs,artifacts} and /opt/ops-bot.
/// </summary>
public sealed class HostLayout : IDisposable
{
    public HostLayout()
    {
        Directory.CreateDirectory(Requests);
        Directory.CreateDirectory(Results);
        Directory.CreateDirectory(State);
        Directory.CreateDirectory(Path.GetDirectoryName(ServicesFile)!);
        File.WriteAllText(ServicesFile, "# The services of the test\ndemo\n");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ops-bot-test-" + Guid.NewGuid().ToString("N"));

    public string Opt => Path.Combine(Root, "opt");

    public string ServicesFile => Path.Combine(Opt, "ops-bot", "configs", "services");

    public string Requests => Path.Combine(Opt, "ops-bot", "data", "requests");

    public string Results => Path.Combine(Opt, "ops-bot", "data", "results");

    public string State => Path.Combine(Opt, "ops-bot", "data", "state");

    public Dictionary<string, string> Settings => new()
    {
        ["Ops:ServicesRoot"] = Opt,
        ["Ops:ServicesFile"] = ServicesFile,
        ["Ops:RequestsDirectory"] = Requests,
        ["Ops:ResultsDirectory"] = Results,
        ["Ops:StateDirectory"] = State,
    };

    /// <summary>The service "demo": service.env, the releases and the active release.</summary>
    public string Service(string id = "demo", string title = "Demo service", string? active = null, params string[] releases)
    {
        var root = Path.Combine(Opt, id);
        Directory.CreateDirectory(Path.Combine(root, "configs"));
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllText(Path.Combine(root, "configs", "service.env"), $"""
            # The test service.
            TITLE={title}
            UNIT={id}
            HEALTH_URL=http://health.test/{id}
            VERSION_URL=http://health.test/{id}
            """);
        foreach (var version in releases)
        {
            Directory.CreateDirectory(Path.Combine(root, "v" + version));
        }

        if (active is not null)
        {
            Link(Path.Combine(root, "app"), Path.Combine(root, "v" + active));
        }

        return root;
    }

    public static void WriteChanges(string releaseDirectory, string version, params (long Build, string Uk, string En)[] changes) =>
        File.WriteAllText(Path.Combine(releaseDirectory, "CHANGES.json"), ChangesJson(version, changes));

    /// <summary>An artifact as build.sh makes it: VERSION and CHANGES.json first, then the application, and the .sha256 file.</summary>
    public string Artifact(string id, string version, params (long Build, string Uk, string En)[] changes)
    {
        var path = Path.Combine(Opt, id, "artifacts", $"{id}-{version}.tar.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            Add(tar, "./VERSION", version + "\nabc\n");
            Add(tar, "./CHANGES.json", ChangesJson(version, changes));
            Add(tar, "./Demo", "binary");
        }

        File.WriteAllText(path + ".sha256", $"0000  {id}-{version}.tar.gz\n");
        return path;
    }

    public void Dispose()
    {
        try
        {
            // A link first: a recursive delete does not remove a junction on Windows.
            foreach (var link in Directory.EnumerateDirectories(Opt, "app", SearchOption.AllDirectories).ToList())
            {
                Directory.Delete(link);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file can be still open on Windows; the temporary directory stays.
        }
    }

    private static string ChangesJson(string version, (long Build, string Uk, string En)[] changes) =>
        "{\"version\":\"" + version + "\",\"commit\":\"abc\",\"changes\":[" + string.Join(',', changes.Select(c =>
            $"{{\"build\":{c.Build},\"commit\":\"c{c.Build}\",\"date\":\"2026-10-21\",\"uk\":\"{c.Uk}\",\"en\":\"{c.En}\"}}")) + "]}";

    private static void Add(TarWriter tar, string name, string text)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
        tar.WriteEntry(entry);
    }

    /// <summary>A symbolic link; on Windows without the right to make one, a junction (LinkTarget reads both).</summary>
    private static void Link(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, Path.GetFileName(target));
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
        }
    }
}
