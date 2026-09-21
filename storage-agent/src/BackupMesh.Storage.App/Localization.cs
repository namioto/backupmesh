using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace BackupMesh.Storage.App;

public static class Localization
{
    internal static readonly ResourceManager Resources = new("BackupMesh.Storage.App.Strings", typeof(Localization).Assembly);
    public static string Text(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
    public static string State(string? state) => state is null ? "—" : Resources.GetString("State_" + state, CultureInfo.CurrentUICulture) ?? state;
    public static string Count(int count, string noun) => Format("Count_" + noun, count,
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" && count != 1 ? "s" : "");

    public static CultureInfo ResolveCulture(string? language, CultureInfo systemCulture) =>
        CultureInfo.GetCultureInfo(language is "ko" or "en" ? language : systemCulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en");

    public static void Initialize(string? language)
    {
        var culture = ResolveCulture(language, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // Keep the user's regional number/date formatting independent of the UI language.
    }
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Localization.Text(key);
}

public sealed record LanguageOption(string Code, string DisplayName);
