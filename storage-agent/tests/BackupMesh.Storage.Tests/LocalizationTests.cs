using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BackupMesh.Storage.App;

namespace BackupMesh.Storage.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void KoreanResourcesCoverEnglishKeysAndFormats()
    {
        var english = Localization.Resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!;
        var korean = Localization.Resources.GetResourceSet(CultureInfo.GetCultureInfo("ko"), true, false)!;
        Assert.Equal(english.Cast<DictionaryEntry>().Count(), korean.Cast<DictionaryEntry>().Count());
        foreach (DictionaryEntry entry in english)
        {
            var translated = korean.GetString((string)entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(translated), (string)entry.Key);
            var sourceFormat = CompositeFormat.Parse((string)entry.Value!);
            var translatedFormat = CompositeFormat.Parse(translated!);
            Assert.True(translatedFormat.MinimumArgumentCount <= sourceFormat.MinimumArgumentCount, (string)entry.Key);
        }
        Assert.Equal("ko", Localization.ResolveCulture("", CultureInfo.GetCultureInfo("ko-KR")).Name);
        Assert.Equal("en", Localization.ResolveCulture("invalid", CultureInfo.GetCultureInfo("fr-FR")).Name);
        Assert.Equal("en", Localization.ResolveCulture("en", CultureInfo.GetCultureInfo("ko-KR")).Name);
    }

    [Fact]
    public void LanguageChangesOnlyPresentationAndProgressDoesNotParseLocalizedText()
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var originalFormat = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var job = new BackupJobViewModel(new(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, new(125, 1000, 1, 8), null));
            Assert.Equal("RUNNING", job.State);
            Assert.Equal("실행 중", job.StateDisplay);
            Assert.Equal(12.5, new FlyoutJobViewModel(job).PercentComplete);
            Assert.Contains("12,5%", job.Progress);
            Assert.Contains("파일", job.Progress);
            Assert.Equal("백업 2개", MainWindowViewModel.Pluralize(2, "backup"));
            Assert.Equal("UNKNOWN_NEW_STATE", Localization.State("UNKNOWN_NEW_STATE"));
            var old = JsonSerializer.Deserialize<AppConfiguration>("""{"Topology":{"Devices":[],"BackupSets":[],"Mappings":[]}}""")!;
            Assert.Equal("", old.Language);
            var saved = JsonSerializer.Deserialize<AppConfiguration>(JsonSerializer.Serialize(old with { Language = "ko" }))!;
            Assert.Equal("ko", saved.Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.CurrentCulture = originalFormat;
        }
    }
}
