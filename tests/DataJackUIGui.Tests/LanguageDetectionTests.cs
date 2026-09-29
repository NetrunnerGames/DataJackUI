using System.Globalization;
using DataJackUIGui;
using Xunit;

namespace DataJackUIGui.Tests;

/// <summary>
/// Tests for <see cref="Program.MatchOsLanguage"/>. The OS-display-language → supported-UI-tag mapping.
/// Regression guard for the bug where a region-qualified OS culture (e.g. Spanish "es-ES") fell through
/// to English instead of mapping to its neutral resource ("es").
/// </summary>
public class LanguageDetectionTests
{
    private static string Match(string osCultureName) =>
        Program.MatchOsLanguage(new CultureInfo(osCultureName));

    // ── Non-English locales fall back to English when English is the only supported resource ──────────────
    [Theory]
    [InlineData("es-ES", "en")]   // Spanish (Spain)
    [InlineData("fr-FR", "en")]
    [InlineData("de-DE", "en")]
    [InlineData("it-IT", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("ko-KR", "en")]
    [InlineData("ru-RU", "en")]
    [InlineData("pl-PL", "en")]
    [InlineData("nl-NL", "en")]
    [InlineData("tr-TR", "en")]
    [InlineData("uk-UA", "en")]
    [InlineData("cs-CZ", "en")]
    [InlineData("hu-HU", "en")]
    [InlineData("ro-RO", "en")]
    [InlineData("el-GR", "en")]
    [InlineData("bg-BG", "en")]
    [InlineData("th-TH", "en")]
    [InlineData("vi-VN", "en")]
    [InlineData("id-ID", "en")]
    [InlineData("da-DK", "en")]
    [InlineData("fi-FI", "en")]
    [InlineData("sv-SE", "en")]
    public void RegionQualified_FallsBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("pt-BR", "en")]
    [InlineData("pt-PT", "en")]
    [InlineData("es-419", "en")]
    public void ExactRegionalTags_FallBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("en", "en")]
    [InlineData("es", "en")]
    [InlineData("fr", "en")]
    [InlineData("de", "en")]
    public void NeutralTags_MatchOrFallback(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("zh-CN", "en")]
    [InlineData("zh-SG", "en")]
    [InlineData("zh-Hans", "en")]
    [InlineData("zh-TW", "en")]
    [InlineData("zh-HK", "en")]
    [InlineData("zh-MO", "en")]
    [InlineData("zh-Hant", "en")]
    public void Chinese_FallsBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("es-ES", "en")]
    [InlineData("es-MX", "en")]
    [InlineData("es-AR", "en")]
    [InlineData("es-CO", "en")]
    [InlineData("es-CL", "en")]
    public void Spanish_FallsBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("pt-PT", "en")]
    [InlineData("pt-BR", "en")]
    [InlineData("pt", "en")]
    public void Portuguese_FallsBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("nb-NO", "en")]
    [InlineData("nn-NO", "en")]
    [InlineData("nb", "en")]
    [InlineData("nn", "en")]
    public void Norwegian_FallsBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    [Theory]
    [InlineData("ar-SA", "en")]
    [InlineData("ar-EG", "en")]
    [InlineData("ar", "en")]
    public void Arabic_FallsBackToEnglish(string os, string expected) =>
        Assert.Equal(expected, Match(os));

    // ── Unsupported languages fall back to English ───────────────────────────────────────────────────
    [Theory]
    [InlineData("he-IL")]   // Hebrew, not shipped
    [InlineData("hi-IN")]   // Hindi, not shipped
    [InlineData("af-ZA")]   // Afrikaans, not shipped
    [InlineData("")]        // invariant culture
    public void Unsupported_FallsBackToEnglish(string os) =>
        Assert.Equal("en", Match(os));
}
