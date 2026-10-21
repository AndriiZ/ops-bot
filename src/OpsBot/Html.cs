namespace OpsBot;

/// <summary>The text for ParseMode.Html of Telegram.</summary>
internal static class Html
{
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The end of a long text: Telegram accepts up to 4096 characters in a message.</summary>
    public static string Tail(string text, int maxLength) =>
        text.Length <= maxLength ? text : "…" + text[^(maxLength - 1)..];

    public static string Cut(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
}
