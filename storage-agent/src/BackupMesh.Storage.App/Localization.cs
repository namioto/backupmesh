using System.Globalization;
using System.Resources;
using System.Windows.Markup;
using System.ComponentModel;
using System.Windows.Data;

namespace BackupMesh.Storage.App;

public static class Localization
{
    internal static readonly ResourceManager Resources = new("BackupMesh.Storage.App.Strings", typeof(Localization).Assembly);
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    public static TranslationSource Source { get; } = new();
    public static event EventHandler? LanguageChanged;
    public static string Text(string key) => Resources.GetString(key, Source.Culture) ?? key;
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
    public static string State(string? state) => state is null ? "—" : Resources.GetString("State_" + state, Source.Culture) ?? state;
    public static string Count(int count, string noun) => Format("Count_" + noun, count,
        Source.Culture.TwoLetterISOLanguageName == "en" && count != 1 ? "s" : "");

    public static CultureInfo ResolveCulture(string? language, CultureInfo systemCulture) =>
        CultureInfo.GetCultureInfo(language is "ko" or "en" ? language : systemCulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en");

    public static void Initialize(string? language)
    {
        var culture = ResolveCulture(language, SystemCulture);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Source.ChangeCulture(culture);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
        // Keep the user's regional number/date formatting independent of the UI language.
    }
}

public sealed class TranslationSource : INotifyPropertyChanged
{
    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentUICulture;
    public string this[string key] => Localization.Text(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void ChangeCulture(CultureInfo culture)
    {
        Culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new System.Windows.Data.Binding($"[{key}]") { Source = Localization.Source, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

public sealed class LanguageOption(string code, string displayName) : ObservableObject
{
    public string Code => code;
    public string DisplayName => code.Length == 0 ? Localization.Text("SystemDefault") : displayName;
}
