# Storage app localization

The WPF app uses .NET resources: `Strings.resx` is the English fallback and `Strings.ko.resx` supplies Korean. `Localization.Text`/`Format` serve C# and `{local:Loc Key}` serves XAML. No extra runtime dependency is required.

To add a language, copy the resource keys into `Strings.<culture>.resx`, translate the values, and add the culture to `Localization.ResolveCulture` and the view model's language options and validation. Extend `LocalizationTests` for that culture. Preserve format placeholders; do not translate protocol states, identifiers, paths, or stored source names. Translate state labels only at display time. Plural rules currently cover English and Korean; adapt count formatting for languages with different rules.

The optional `AppConfiguration.Language` field stores an empty string (system default), `en`, or `ko`. Old settings remain valid. Changing language saves and applies immediately. `LocExtension` returns a binding to a shared resource indexer; property-change notifications update existing controls, including column headers. View models refresh computed labels, and the tray menu refreshes separately. Existing view models and backup operations stay alive. The resource provider owns the selected culture so asynchronous callbacks cannot revert to a previously captured UI culture. System default always resolves from the original system culture, not the last selected language. Regional number/date formats remain unchanged. Existing activity records and backend diagnostic messages retain their original language.

Preview without modifying installed settings:

```powershell
dotnet run --project storage-agent/src/BackupMesh.Storage.App -- --demo --language=ko
dotnet run --project storage-agent/src/BackupMesh.Storage.App -- --demo --language=en
```

Localization tests check resource coverage, composite formats, old configuration compatibility, and culture-independent numeric job progress.
