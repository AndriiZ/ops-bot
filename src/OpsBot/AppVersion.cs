using System.Reflection;

namespace OpsBot;

internal static class AppVersion
{
    /// <summary>The version of the release without the source revision after '+', for example 1.0.0.12.</summary>
    public static string Current { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
