using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpsBot.Chat;

/// <summary>
/// The language of each administrator: at first from Telegram (language_code "uk" gives Ukrainian, another code gives
/// English), then the choice of the button. The file keeps it: the alerts and the results use it too.
/// </summary>
internal sealed class AdminLanguages(IOptions<OpsOptions> options)
{
    private readonly Lock gate = new();
    private Dictionary<long, Language>? languages;

    private string FilePath => Path.Combine(options.Value.StateDirectory, "languages.json");

    public Language Get(long userId, string? telegramCode = null)
    {
        lock (gate)
        {
            var all = Load();
            if (all.TryGetValue(userId, out var language))
            {
                return language;
            }

            if (telegramCode is null)
            {
                return Language.Uk;
            }

            language = telegramCode.StartsWith("uk", StringComparison.OrdinalIgnoreCase) ? Language.Uk : Language.En;
            all[userId] = language;
            Save(all);
            return language;
        }
    }

    public void Set(long userId, Language language)
    {
        lock (gate)
        {
            var all = Load();
            all[userId] = language;
            Save(all);
        }
    }

    private Dictionary<long, Language> Load()
    {
        if (languages is not null)
        {
            return languages;
        }

        try
        {
            languages = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<long, Language>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            languages = [];
        }

        return languages;
    }

    private void Save(Dictionary<long, Language> all)
    {
        try
        {
            Directory.CreateDirectory(options.Value.StateDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch (IOException)
        {
            // The language stays in the memory until a restart.
        }
    }
}
