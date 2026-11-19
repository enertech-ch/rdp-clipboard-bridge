using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

enum TextId
{
    MenuPause,
    MenuResume,
    MenuStartWithWindows,
    MenuAutomatic,
    MenuConvertAutomatically,
    MenuOnDemand,
    MenuOnDemandOff,
    MenuOnDemandCopies,
    MenuOpenTempFolder,
    MenuPauseOnMetered,
    MenuFileTypes,
    MenuSizeLimit,
    SizeLimitExplanation,
    ButtonOk,
    ButtonCancel,
    FileTypeImages,
    FileTypeVideos,
    FileTypeAudio,
    FileTypePdf,
    FileTypeOffice,
    FileTypeText,
    FileTypeArchives,
    FileTypeApplications,
    FileTypeNoExtension,
    FileTypeOther,
    FileTypeFolders,
    MenuInstall,
    MenuUninstall,
    MenuAbout,
    MenuExit,
    AboutVersion,
    AboutExplanation,
    TrayTooltipPaused,
    TrayTooltipMetered,
    TrayTooltipFileType,
    TrayTooltipSizeLimit,
    WarningPrepareFailed,
    WarningFilesFailed,
    WarningAutostartFailed,
    WarningOpenFolderFailed,
    InstallExplanation,
    InstallReplacesExisting,
    ButtonInstallWithAutostart,
    ButtonInstallOnly,
    ButtonRunWithoutInstalling,
    ErrorInstallFailed,
    ConfirmUninstall,
    ErrorUninstallIncomplete,
    ConfirmReplaceInstalled,
    ConfirmReplaceRunning,
    ErrorRunningCopyDidNotExit,
    ErrorStartFailed
}

// Every user-visible text, per language of the Windows display language. English is the complete
// reference: a text missing in another language, and any language without a file, falls back to it.
// {0}, {1}... are filled in by the caller, so each language can put them where its grammar wants.
// The product name is passed in rather than written into the texts, so it is never translated.
//
// The texts are src/lang/<code>.txt, keyed by two-letter ISO 639-1 code, one "TextId = text" per
// line. build.ps1 embeds every file there as the resource lang.<code>.txt, so adding a language is
// adding a file. Reading them must never take the app down: whatever cannot be read is missing,
// and a missing text falls back to English - or, if even that is gone, shows its id.
static class Texts
{
    public const string ProductName = "RDP Clipboard Bridge";
    const string ReferenceLanguage = "en";

    static readonly Dictionary<TextId, string> English = Load(ReferenceLanguage);
    static readonly CultureInfo DisplayLanguage = NeutralDisplayLanguage();
    static readonly Dictionary<TextId, string> Current = TranslationFor(DisplayLanguage);

    public static string Get(TextId id, params object[] values)
    {
        string text;
        if (!Current.TryGetValue(id, out text) && !English.TryGetValue(id, out text)) return id.ToString();
        if (values.Length == 0) return text;
        try
        {
            return string.Format(text, values);
        }
        catch (FormatException)
        {
            // a stray brace in a translation file must not end the app; tools/check-texts.py is
            // there to catch it before
            return text;
        }
    }

    // de-CH and de-DE both end up at de, pt-BR at pt, zh-CN and zh-TW at zh.
    static CultureInfo NeutralDisplayLanguage()
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        while (!culture.IsNeutralCulture && !culture.Equals(CultureInfo.InvariantCulture)) culture = culture.Parent;
        return culture;
    }

    static Dictionary<TextId, string> TranslationFor(CultureInfo language)
    {
        string code = language.TwoLetterISOLanguageName;
        if (code == ReferenceLanguage) return English;
        Dictionary<TextId, string> texts = Load(code);
        return texts.Count > 0 ? texts : English;
    }

    // The texts of one language, as embedded by build.ps1; empty when there is no such language.
    // Internal so that a probe can load every language, not only the one of the machine it runs on.
    internal static Dictionary<TextId, string> Load(string languageCode)
    {
        Dictionary<TextId, string> texts = new Dictionary<TextId, string>();
        try
        {
            using (Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("lang." + languageCode + ".txt"))
            {
                if (stream == null) return texts;
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) AddLine(texts, line);
                }
            }
        }
        catch (Exception)
        {
            // keep what was read; the rest falls back to English
        }
        return texts;
    }

    // "TextId = text"; blank lines, # comments, unknown ids and lines without "=" are skipped -
    // tools/check-texts.py reports those, the app just does without them.
    static void AddLine(Dictionary<TextId, string> texts, string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) return;
        int equals = trimmed.IndexOf('=');
        if (equals < 0) return;
        string name = trimmed.Substring(0, equals).Trim();
        // IsDefined takes the exact name only; Enum.Parse alone would also take "3" or "A, B"
        if (!Enum.IsDefined(typeof(TextId), name)) return;
        texts[(TextId)Enum.Parse(typeof(TextId), name)] = Unescape(trimmed.Substring(equals + 1).Trim());
    }

    // \n is a line break and \\ a backslash; any other backslash is kept as it is.
    static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;
        StringBuilder unescaped = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (text[i] == '\\' && next == 'n') { unescaped.Append('\n'); i++; }
            else if (text[i] == '\\' && next == '\\') { unescaped.Append('\\'); i++; }
            else unescaped.Append(text[i]);
        }
        return unescaped.ToString();
    }
}
