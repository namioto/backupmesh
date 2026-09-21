using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BackupMesh.Storage.App;

namespace BackupMesh.Storage.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

[Collection("Localization")]
public sealed class LocalizationTests
{
    [Fact]
    public void PairingInvitationContainsExactlyTheThreeConnectionValues()
    {
        var session = new PairingSessionDto("one-time-test-code", "https://192.168.1.20:7443", new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(10), null);
        Assert.StartsWith("backupmesh:v1:", session.Invitation);
        var encoded = session.Invitation["backupmesh:v1:".Length..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        using var data = JsonDocument.Parse(Convert.FromBase64String(encoded));
        Assert.Equal(3, data.RootElement.EnumerateObject().Count());
        Assert.Equal(session.ControlEndpoint, data.RootElement.GetProperty("endpoint").GetString());
        Assert.Equal(session.Code, data.RootElement.GetProperty("code").GetString());
        Assert.Equal(session.CertificateSha256, data.RootElement.GetProperty("fingerprint").GetString());
    }

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
            Localization.Initialize("ko");
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
            Localization.Initialize(originalUi.TwoLetterISOLanguageName);
            CultureInfo.CurrentCulture = originalFormat;
        }
    }

    [Fact]
    public void LanguageSwitchUpdatesExistingBindingsAndPreservesViewModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = Localization.Source.Culture;
            try
            {
                Localization.Initialize("en");
                var label = (System.Windows.Controls.TextBlock)System.Windows.Markup.XamlReader.Parse("""
                    <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                               xmlns:local="clr-namespace:BackupMesh.Storage.App;assembly=BackupMesh.Storage.App"
                               Text="{local:Loc Text_SourceAgent_21CF69}"/>
                    """);
                using var model = new MainWindowViewModel(loadLocalState: false);
                var jobs = model.Jobs;
                var job = new BackupJobViewModel(new(Guid.NewGuid(), "RUNNING", DateTimeOffset.UtcNow, null, null));
                jobs.Add(job);
                model.SelectedJob = job;
                Assert.Equal("Remote Agent", label.Text);
                model.Language = "ko";
                Assert.Equal("원격 에이전트", label.Text);
                Assert.Equal("실행 중", job.StateDisplay);
                Assert.Same(jobs, model.Jobs);
                Assert.Same(job, model.SelectedJob);
                model.Language = "en";
                Assert.Equal("Remote Agent", label.Text);
                Assert.Equal("RUNNING", job.State);
                model.Language = "";
                var automatic = Localization.Source.Culture;
                model.Language = "ko";
                model.Language = "";
                Assert.Equal(automatic, Localization.Source.Culture);
            }
            catch (Exception error) { failure = error; }
            finally { Localization.Initialize(original.TwoLetterISOLanguageName); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Language switch check timed out.");
        Assert.Null(failure);
    }
}
