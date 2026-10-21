using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpsBot.Services;

namespace OpsBot.Tests;

public sealed class ServicesTests : IDisposable
{
    private readonly HostLayout host = new();

    public void Dispose() => host.Dispose();

    [Fact]
    public void ReleaseVersion_TheOrderOfSortV()
    {
        string[] versions = ["1.0.0.10", "1.0.0.9", "1.2", "1.0.0.100", "2.0.0.1"];
        Assert.Equal(["1.0.0.9", "1.0.0.10", "1.0.0.100", "1.2", "2.0.0.1"], versions.Order(ReleaseVersion.Comparer));
        Assert.True(ReleaseVersion.IsValid("1.0.0.845"));
        Assert.False(ReleaseVersion.IsValid("1"));
        Assert.False(ReleaseVersion.IsValid("1.0.0.1;rm"));
        Assert.False(ReleaseVersion.IsValid("1.0.0.0.1"));
        Assert.Equal(845, ReleaseVersion.Build("1.0.0.845"));
        Assert.True(ReleaseVersion.IsNewer("1.0.0.10", null));
        Assert.False(ReleaseVersion.IsNewer("1.0.0.10", "1.0.0.10"));
    }

    [Fact]
    public void ServiceEnv_IsDataNotAScript()
    {
        var values = ServiceCatalog.ParseEnv("# comment\r\nTITLE=Finance App — сервер\r\n\r\nHEALTH_URL=http://127.0.0.1:8085/health?a=b\nBROKEN\n");
        Assert.Equal("Finance App — сервер", values["TITLE"]);
        Assert.Equal("http://127.0.0.1:8085/health?a=b", values["HEALTH_URL"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Catalog_TheServicesOfTheFile()
    {
        host.Service();
        host.Service("no-env");
        File.Delete(Path.Combine(host.Opt, "no-env", "configs", "service.env"));
        File.WriteAllText(host.ServicesFile, "demo\n../etc\nno-env\ndemo\nmissing\n");

        var services = Catalog().All();

        var demo = Assert.Single(services);
        Assert.Equal(("demo", "Demo service", "demo", "http://health.test/demo"), (demo.Id, demo.Title, demo.Unit, demo.HealthUrl));
        Assert.Null(Catalog().Find("../etc"));
    }

    [Fact]
    public void Inspector_TheReleasesTheArtifactsAndTheActiveRelease()
    {
        var root = host.Service(active: "1.0.0.11", releases: ["1.0.0.9", "1.0.0.10", "1.0.0.11", "bad"]);
        host.Artifact("demo", "1.0.0.12");
        host.Artifact("demo", "1.0.0.10");
        // Without .sha256 the upload is not complete.
        File.WriteAllText(Path.Combine(root, "artifacts", "demo-1.0.0.13.tar.gz"), "partial");
        var service = Catalog().Find("demo")!;

        Assert.Equal("1.0.0.11", ServiceInspector.ActiveVersion(root));
        Assert.Equal(["1.0.0.11", "1.0.0.10", "1.0.0.9"], ServiceInspector.Releases(root));
        Assert.Equal(["1.0.0.12", "1.0.0.10"], ServiceInspector.Artifacts(service));

        var state = new ServiceState(service, "1.0.0.11", ServiceInspector.Releases(root), ServiceInspector.Artifacts(service), null, new HealthResult(HealthIssue.None));
        Assert.Equal("1.0.0.12", state.NewArtifact);
        Assert.Equal("1.0.0.10", state.Previous);
    }

    [Fact]
    public void Inspector_TheUnitAndTheVersion()
    {
        var unit = ServiceInspector.ParseUnit("LoadState=loaded\nActiveState=active\nSubState=running\nActiveEnterTimestamp=@1792563600\nNRestarts=2\n");
        Assert.Equal(new UnitState("loaded", "active", "running", DateTimeOffset.FromUnixTimeSeconds(1792563600), 2), unit);
        Assert.Null(ServiceInspector.ParseUnit("ActiveState=inactive\nActiveEnterTimestamp=\n").Since);

        Assert.True(ServiceInspector.ShowsVersion("{ \"server\": { \"name\": \"x\", \"version\": \"1.0.0.12\" } }", "1.0.0.12"));
        Assert.False(ServiceInspector.ShowsVersion("{\"version\":\"1.0.0.120\"}", "1.0.0.12"));
    }

    [Fact]
    public void Changes_BetweenTwoVersions()
    {
        var root = host.Service(active: "1.0.0.11", releases: ["1.0.0.10", "1.0.0.11"]);
        HostLayout.WriteChanges(Path.Combine(root, "v1.0.0.11"), "1.0.0.11", (11, "Нове B", "New B"), (10, "Нове A", "New A"), (9, "Старе", "Old"));
        host.Artifact("demo", "1.0.0.14", (14, "Четверте", "Fourth"), (12, "Друге", "Second"), (11, "Нове B", "New B"));
        var service = Catalog().Find("demo")!;
        var reader = new ChangeReader(NullLogger<ChangeReader>.Instance);

        Assert.Equal(["Fourth", "Second"], reader.ForDeploy(service, "1.0.0.11", "1.0.0.14")!.Select(c => c.En));
        Assert.Equal(["Четверте", "Друге", "Нове B"], reader.ForDeploy(service, null, "1.0.0.14")!.Select(c => c.Uk));
        Assert.Equal(["New B", "New A"], reader.ForRollback(service, "1.0.0.11", "1.0.0.9")!.Select(c => c.En));
        Assert.Null(reader.ForDeploy(service, "1.0.0.11", "1.0.0.99"));
    }

    [Fact]
    public void Requests_OneRequestForAService()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T10:00:00Z"));
        var requests = new DeployRequests(Options.Create(Ops()), time);
        Change[] changes = [new(12, "c12", "2026-10-21", "Зміна", "Change")];

        var request = requests.Create(DeployAction.Deploy, "demo", "1.0.0.12", "1.0.0.11", changes, 42)!;

        Assert.Matches("^20261021100000-[0-9a-f]{6}$", request.Id);
        Assert.Equal("deploy demo 1.0.0.12\n", File.ReadAllText(Path.Combine(host.Requests, request.Id + ".req")));
        Assert.Empty(Directory.GetFiles(host.Requests, ".*"));
        Assert.Null(requests.Create(DeployAction.Rollback, "demo", "1.0.0.10", "1.0.0.11", null, 42));
        Assert.Equal(request, requests.Find(request.Id)! with { Changes = request.Changes });
        Assert.Equal("Change", requests.Find(request.Id)!.Changes![0].En);

        time.Advance(TimeSpan.FromMinutes(41));
        Assert.Equal(request.Id, Assert.Single(requests.Expired()).Id);
        requests.Complete(request.Id);
        Assert.Empty(requests.Pending());
        Assert.Empty(Directory.GetFiles(host.Requests));
    }

    [Fact]
    public void DeployLog_TheResultTheJournalAndTheOutput()
    {
        var log = DeployLog.Parse("""
            [2026-10-21 10:00:00] Деплой demo v1.0.0.12
            @journal-begin
            Unhandled exception. System.InvalidOperationException: No database
            @journal-end
            [2026-10-21 10:01:00] Працює попередній реліз v1.0.0.11.
            @result rolled-back http://127.0.0.1:8085/health не відповідає 2xx
            """);

        Assert.Equal("rolled-back", log.Code);
        Assert.Equal("http://127.0.0.1:8085/health не відповідає 2xx", log.Detail);
        Assert.Equal("Unhandled exception. System.InvalidOperationException: No database", log.Journal);
        Assert.DoesNotContain("@", log.Output);
        Assert.Null(DeployLog.Parse("no result").Code);
    }

    private ServiceCatalog Catalog() => new(Options.Create(Ops()), NullLogger<ServiceCatalog>.Instance);

    private OpsOptions Ops() => new()
    {
        ServicesRoot = host.Opt,
        ServicesFile = host.ServicesFile,
        RequestsDirectory = host.Requests,
        ResultsDirectory = host.Results,
        StateDirectory = host.State,
    };
}
