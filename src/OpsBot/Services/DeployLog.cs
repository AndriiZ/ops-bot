namespace OpsBot.Services;

/// <summary>
/// The output of deploy.sh or ops-runner: the line "@result &lt;code&gt; &lt;detail&gt;", the journal of the service
/// between "@journal-begin" and "@journal-end" and the other lines (README.md §4).
/// </summary>
public sealed record DeployLog(string? Code, string? Detail, string Journal, string Output)
{
    public static DeployLog Parse(string text)
    {
        string? code = null;
        string? detail = null;
        var journal = new List<string>();
        var output = new List<string>();
        var inJournal = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == "@journal-begin")
            {
                inJournal = true;
            }
            else if (line == "@journal-end")
            {
                inJournal = false;
            }
            else if (inJournal)
            {
                journal.Add(line);
            }
            else if (line.StartsWith("@result ", StringComparison.Ordinal))
            {
                var parts = line["@result ".Length..].Split(' ', 2);
                code = parts[0];
                detail = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            }
            else if (line.Length > 0)
            {
                output.Add(line);
            }
        }

        return new DeployLog(code, detail, string.Join('\n', journal).Trim(), string.Join('\n', output.TakeLast(12)));
    }
}
