namespace RetroShowroom.Core;

/// <summary>
/// Sprache der App: Deutsch (Standard) oder Englisch. Jeder Text steht direkt im Code als Paar
/// <c>Loc.T("Deutsch", "English")</c>, so bleiben beide Fassungen immer nebeneinander.
/// Gewechselt wird beim Start (siehe Einstellungen → Sprache, danach Neustart).
/// </summary>
public static class Loc
{
    public const string German = "de";
    public const string EnglishCode = "en";

    public static bool English { get; private set; }

    public static string Language => English ? EnglishCode : German;

    /// <summary>„en“ = Englisch, alles andere = Deutsch.</summary>
    public static void Set(string? language) =>
        English = string.Equals(language, EnglishCode, StringComparison.OrdinalIgnoreCase);

    public static string T(string de, string en) => English ? en : de;

    /// <summary>BCP-47-Tag für Windows (WinUI-eigene Texte).</summary>
    public static string WindowsTag => English ? "en-US" : "de-DE";
}
