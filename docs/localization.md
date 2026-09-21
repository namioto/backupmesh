# Storage app localization

The WPF app uses .NET resources: `Strings.resx` is the English fallback and `Strings.ko.resx` supplies Korean. `Localization.Text`/`Format` serve C# and `{local:Loc Key}` serves XAML. No extra runtime dependency is required.

To add a language, copy the resource keys into `Strings.<culture>.resx`, translate the values, and add the culture to `Localization.ResolveCulture` and the view model's language options and validation. Extend `LocalizationTests` for that culture. Preserve format placeholders; do not translate protocol states, identifiers, paths, or stored source names. Translate state labels only at display time. Plural rules currently cover English and Korean; adapt count formatting for languages with different rules.

The optional `AppConfiguration.Language` field stores an empty string (system default), `en`, or `ko`. Old settings remain valid. Changing language saves this field immediately and takes effect after the tray app exits and restarts. Regional number/date formats remain unchanged. Backend diagnostic messages remain in their original language.

Preview without modifying installed settings:

```powershell
dotnet run --project storage-agent/src/BackupMesh.Storage.App -- --demo --language=ko
dotnet run --project storage-agent/src/BackupMesh.Storage.App -- --demo --language=en
```

Localization tests check resource coverage, composite formats, old configuration compatibility, and culture-independent numeric job progress.
