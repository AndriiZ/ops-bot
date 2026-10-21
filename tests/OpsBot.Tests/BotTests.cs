using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpsBot.Services;
using Telegram.Bot;

namespace OpsBot.Tests;

/// <summary>The bot with a fake Bot API, a fake systemctl and a fake health address.</summary>
public sealed class BotTests : IAsyncLifetime
{
    private const long Admin = 1001;
    private const long Stranger = 2002;
    private const string Secret = "test-secret";

    private readonly HostLayout host = new();
    private readonly FakeTelegram telegram = new();
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    public ValueTask InitializeAsync()
    {
        var root = host.Service(active: "1.0.0.11", releases: ["1.0.0.10", "1.0.0.11"]);
        HostLayout.WriteChanges(Path.Combine(root, "v1.0.0.11"), "1.0.0.11", (11, "Шаблони сповіщень", "Notification templates"));
        host.Artifact("demo", "1.0.0.12", (12, "Сервер під systemd", "The server runs under systemd"), (11, "Шаблони сповіщень", "Notification templates"));

        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Bot:Token", "123456:TEST");
            builder.UseSetting("Bot:WebhookSecret", Secret);
            builder.UseSetting("Bot:AdminIds:0", Admin.ToString());
            builder.UseSetting("Ops:MonitorInterval", "01:00:00");
            foreach (var (key, value) in host.Settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ITelegramBotClient>(new TelegramBotClient("123456:TEST", new HttpClient(telegram)));
                services.AddSingleton<ISystemCommands>(new FakeSystem());
                services.AddHttpClient(ServiceInspector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeHealth("1.0.0.11"));
            });
        });
        client = factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        host.Dispose();
    }

    [Fact]
    public async Task Webhook_NeedsTheSecret()
    {
        using var content = new StringContent("{\"update_id\":1}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/telegram/webhook", content, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var health = await client.GetFromJsonAsync<JsonElement>("/health", Token);
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal("disabled", health.GetProperty("webhook").GetString());
    }

    [Fact]
    public async Task AStranger_GetsOnlyTheId()
    {
        await SendAsync(Message(Stranger, "/start"));
        var answer = await telegram.WaitAsync("sendMessage");
        Assert.Contains($"<code>{Stranger}</code>", answer.Text);

        await SendAsync(Message(Stranger, "/status"));
        await SendAsync(Callback(Stranger, "D:demo:1.0.0.12"));
        await telegram.WaitAsync("answerCallbackQuery");
        Assert.Empty(Directory.GetFiles(host.Requests));
    }

    [Fact]
    public async Task TheOverview_InTheLanguageOfTelegram()
    {
        await SendAsync(Message(Admin, "/status", "en"));
        var overview = await telegram.WaitAsync("sendMessage");
        Assert.Contains("<b>Services</b>", overview.Text);
        Assert.Contains("🟢 <b>Demo service</b> · 1.0.0.11 · ⬆️ 1.0.0.12 available", overview.Text);

        await SendAsync(Callback(Admin, "lang"));
        Assert.Contains("<b>Сервіси</b>", (await telegram.WaitAsync("editMessageText")).Text);
    }

    [Fact]
    public async Task TheDeploy_ShowsTheChangesBeforeAndAfter()
    {
        await SendAsync(Callback(Admin, "s:demo"));
        var card = await telegram.WaitAsync("editMessageText");
        Assert.Contains("Health: ✅ HTTP 200", card.Text);
        Assert.Contains("\"callback_data\":\"d:demo:1.0.0.12\"", card.Body);
        Assert.Contains("\"callback_data\":\"r:demo:1.0.0.10\"", card.Body);

        await SendAsync(Callback(Admin, "d:demo:1.0.0.12"));
        var ask = await telegram.WaitAsync("editMessageText");
        Assert.Contains("Розгорнути <b>Demo service</b> 1.0.0.12? Зараз працює 1.0.0.11.", ask.Text);
        Assert.Contains("<b>Зміни</b> (1):\n• Сервер під systemd", ask.Text);
        Assert.DoesNotContain("Шаблони сповіщень", ask.Text);

        await SendAsync(Callback(Admin, "D:demo:1.0.0.12"));
        Assert.Contains("у черзі", (await telegram.WaitAsync("editMessageText")).Text);
        var request = Assert.Single(Directory.GetFiles(host.Requests, "*.req"));
        Assert.Equal("deploy demo 1.0.0.12\n", File.ReadAllText(request));

        // ops-runner: the smoke test failed, deploy.sh went back to the previous release.
        var id = Path.GetFileNameWithoutExtension(request);
        File.Delete(request);
        await File.WriteAllTextAsync(Path.Combine(host.Results, id + ".log"), """
            [2026-10-21 10:00:00] Деплой demo v1.0.0.12
            @journal-begin
            Unhandled exception. Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:5432
            @journal-end
            @result rolled-back http://health.test/demo не відповідає 2xx
            """, Token);
        await File.WriteAllTextAsync(Path.Combine(host.Results, id + ".done"), "2\n", Token);

        var result = await telegram.WaitAsync("sendMessage");
        Assert.Contains("⚠️ <b>Demo service</b>: розгортання 1.0.0.12 не вдалося, повернуто 1.0.0.11.", result.Text);
        Assert.Contains("Причина: смоктест не пройдено: <code>http://health.test/demo не відповідає 2xx</code>", result.Text);
        Assert.Contains("<b>Не розгорнуто</b> (1):\n• Сервер під systemd", result.Text);
        Assert.Contains("Npgsql.NpgsqlException: Failed to connect", result.Text);
        Assert.Empty(Directory.GetFiles(host.Results));
    }

    [Fact]
    public async Task TheRollback_ShowsTheRemovedChanges()
    {
        await SendAsync(Callback(Admin, "r:demo:1.0.0.10", "en"));
        var ask = await telegram.WaitAsync("editMessageText");
        Assert.Contains("Roll back <b>Demo service</b> to 1.0.0.10? Now 1.0.0.11 runs.", ask.Text);
        Assert.Contains("<b>To be removed</b> (1):\n• Notification templates", ask.Text);

        await SendAsync(Callback(Admin, "R:demo:1.0.0.10", "en"));
        await telegram.WaitAsync("editMessageText");
        var request = Assert.Single(Directory.GetFiles(host.Requests, "*.req"));
        Assert.Equal("rollback demo 1.0.0.10\n", File.ReadAllText(request));

        var id = Path.GetFileNameWithoutExtension(request);
        File.Delete(request);
        await File.WriteAllTextAsync(Path.Combine(host.Results, id + ".log"), "@result ok v1.0.0.10\n", Token);
        await File.WriteAllTextAsync(Path.Combine(host.Results, id + ".done"), "0\n", Token);
        var result = await telegram.WaitAsync("sendMessage");
        Assert.Contains("↩️ <b>Demo service</b>: rolled back to 1.0.0.10 (was 1.0.0.11).", result.Text);
        Assert.Contains("<b>Removed</b> (1):\n• Notification templates", result.Text);
        Assert.DoesNotContain("Cause", result.Text);
    }

    [Fact]
    public async Task AnOutdatedButton_DoesNotMakeARequest()
    {
        await SendAsync(Callback(Admin, "D:demo:1.0.0.99"));
        Assert.Equal("Стан змінився, оновіть картку.", (await telegram.WaitAsync("answerCallbackQuery")).Json.GetProperty("text").GetString());
        Assert.Empty(Directory.GetFiles(host.Requests));
    }

    private async Task SendAsync(string update)
    {
        using var content = new StringContent(update, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Telegram-Bot-Api-Secret-Token", Secret);
        var response = await client.PostAsync("/telegram/webhook", content, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int updateId;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string User(long id, string language) =>
        $"{{\"id\":{id},\"is_bot\":false,\"first_name\":\"U\",\"language_code\":\"{language}\"}}";

    private static string Message(long from, string text, string language = "uk") =>
        $"{{\"update_id\":{Interlocked.Increment(ref updateId)},\"message\":{{\"message_id\":5,\"date\":1792563600,"
        + $"\"chat\":{{\"id\":{from},\"type\":\"private\"}},\"from\":{User(from, language)},\"text\":\"{text}\"}}}}";

    private static string Callback(long from, string data, string language = "uk") =>
        $"{{\"update_id\":{Interlocked.Increment(ref updateId)},\"callback_query\":{{\"id\":\"q{updateId}\",\"from\":{User(from, language)},"
        + $"\"chat_instance\":\"1\",\"data\":\"{data}\",\"message\":{{\"message_id\":7,\"date\":1792563600,"
        + $"\"chat\":{{\"id\":{from},\"type\":\"private\"}},\"text\":\"card\"}}}}}}";

    /// <summary>The Bot API: it keeps the calls and gives the minimal correct answers.</summary>
    private sealed class FakeTelegram : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Call> calls = new();

        public async Task<Call> WaitAsync(string method)
        {
            for (var i = 0; i < 100; i++)
            {
                var found = calls.FirstOrDefault(c => c.Method == method && !c.Taken);
                if (found is not null)
                {
                    found.Taken = true;
                    return found;
                }

                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException($"No call of {method}. Calls: {string.Join(", ", calls.Select(c => c.Method))}");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments[^1];
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            calls.Enqueue(new Call(method, body));
            var result = method is "sendMessage" or "editMessageText"
                ? "{\"message_id\":9,\"date\":1792563600,\"chat\":{\"id\":1,\"type\":\"private\"},\"text\":\"x\"}"
                : "true";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"ok\":true,\"result\":{result}}}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Call(string method, string body)
    {
        public string Method { get; } = method;

        public string Body { get; } = body;

        public JsonElement Json { get; } = JsonDocument.Parse(body).RootElement;

        public string Text => Json.TryGetProperty("text", out var text) ? text.GetString()! : "";

        public bool Taken { get; set; }
    }

    private sealed class FakeSystem : ISystemCommands
    {
        public Task<string?> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(file == "systemctl"
                ? "LoadState=loaded\nActiveState=active\nSubState=running\nActiveEnterTimestamp=@1792563600\nNRestarts=0\n"
                : "2026-10-21T10:00:00+0300 host demo[1]: started");
    }

    private sealed class FakeHealth(string version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"status\":\"ok\",\"version\":\"{version}\"}}"),
            });
    }
}
