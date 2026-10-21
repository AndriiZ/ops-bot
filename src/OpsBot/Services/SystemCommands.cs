using System.ComponentModel;
using System.Diagnostics;

namespace OpsBot.Services;

/// <summary>The commands of the host: systemctl and journalctl. The tests replace them.</summary>
internal interface ISystemCommands
{
    /// <summary>The output of the command, or null if the command is missing, fails or takes too long.</summary>
    Task<string?> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed partial class SystemCommands(ILogger<SystemCommands> logger) : ISystemCommands
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<string?> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // No shell: the arguments go to the command as they are.
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception e)
        {
            LogFailed(file, e.Message);
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(Timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var text = await output;
                await error;
                if (process.ExitCode == 0)
                {
                    return text;
                }

                LogFailed(file, $"exit code {process.ExitCode}");
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                LogFailed(file, "timeout");
                return null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The command {File} failed: {Error}")]
    private partial void LogFailed(string file, string error);
}
