using System.Text.RegularExpressions;

namespace OpsBot.Services;

/// <summary>
/// The version of a release: 2–4 numbers, for example 1.0.0.845 (README.md §3). The last number is the build:
/// the number of commits, thus the changes of CHANGES.json between two versions are known.
/// The order is the order of <c>sort -V</c> in deploy.sh.
/// </summary>
internal static partial class ReleaseVersion
{
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    public static bool IsValid(string? text) => text is not null && Pattern().IsMatch(text);

    public static int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var a = x.Split('.');
        var b = y.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var order = long.Parse(a[i]).CompareTo(long.Parse(b[i]));
            if (order != 0)
            {
                return order;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool IsNewer(string version, string? than) => than is null || Compare(version, than) > 0;

    /// <summary>The build number: the last number of the version.</summary>
    public static long Build(string version) => long.Parse(version[(version.LastIndexOf('.') + 1)..]);

    [GeneratedRegex(@"^[0-9]{1,9}(\.[0-9]{1,9}){1,3}$")]
    private static partial Regex Pattern();
}
