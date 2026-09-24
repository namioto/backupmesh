using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BackupMesh.Storage.App;

public sealed class RuleIcon : System.Windows.Controls.Image
{
    public static readonly DependencyProperty IconIdProperty = DependencyProperty.Register(
        nameof(IconId), typeof(int), typeof(RuleIcon), new PropertyMetadata(0, (element, _) =>
        {
            var icon = (RuleIcon)element;
            icon.Source = RuleIconCatalog.Get(icon.IconId);
        }));

    public int IconId
    {
        get => (int)GetValue(IconIdProperty);
        set => SetValue(IconIdProperty, value);
    }

    public RuleIcon()
    {
        Stretch = Stretch.Uniform;
        Source = RuleIconCatalog.Get(0);
    }
}

public sealed class RuleIconChoice : ObservableObject
{
    private readonly string _englishName;
    private readonly string _koreanName;
    public int Id { get; }
    public string Label => Localization.Source.Culture.TwoLetterISOLanguageName == "ko" ? _koreanName : _englishName;

    public RuleIconChoice(int id, string englishName, string koreanName)
    {
        Id = id;
        _englishName = englishName;
        _koreanName = koreanName;
        Localization.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }
}

public static class RuleIconCatalog
{
    private static readonly string[] AtlasFiles = ["basic", "work", "media", "development", "personal", "storage", "business", "symbols"];
    private static readonly (string English, string Korean)[] Names =
    [
        ("Folder", "폴더"), ("Photos", "사진"), ("Documents", "문서"), ("Code", "코드"), ("Music", "음악"), ("Videos", "동영상"), ("Downloads", "다운로드"), ("Database", "데이터베이스"), ("Archive", "보관함"),
        ("Spreadsheet", "스프레드시트"), ("Presentation", "프레젠테이션"), ("PDF", "PDF"), ("Email", "이메일"), ("Calendar", "일정"), ("Contacts", "연락처"), ("Notes", "메모"), ("Tasks", "할 일"), ("Books", "책"),
        ("Camera", "카메라"), ("Portraits", "인물 사진"), ("Landscapes", "풍경 사진"), ("Film", "필름"), ("Microphone", "마이크"), ("Podcasts", "팟캐스트"), ("Audio", "오디오"), ("Headphones", "헤드폰"), ("Gallery", "갤러리"),
        ("Terminal", "터미널"), ("Branches", "브랜치"), ("Packages", "패키지"), ("Servers", "서버"), ("Cloud", "클라우드"), ("API", "API"), ("Lock", "자물쇠"), ("Keys", "키"), ("Computer", "컴퓨터"),
        ("Home", "집"), ("Family", "가족"), ("Favorites", "즐겨찾기"), ("Education", "교육"), ("Travel", "여행"), ("Money", "돈"), ("Health", "건강"), ("Cooking", "요리"), ("Games", "게임"),
        ("Hard drive", "하드 드라이브"), ("SSD", "SSD"), ("USB drive", "USB 드라이브"), ("NAS", "NAS"), ("Cloud sync", "클라우드 동기화"), ("Network", "네트워크"), ("Backup", "백업"), ("Protection", "보호"), ("Vault", "금고"),
        ("Office", "사무실"), ("Briefcase", "서류 가방"), ("Invoices", "청구서"), ("Charts", "차트"), ("Contracts", "계약서"), ("Shared folders", "공유 폴더"), ("Analytics", "분석"), ("Scanner", "스캐너"), ("Printer", "프린터"),
        ("Star", "별"), ("Bookmark", "북마크"), ("Location", "위치"), ("Flag", "깃발"), ("Tag", "태그"), ("Settings", "설정"), ("Lightning", "번개"), ("World", "세계"), ("Clock", "시계")
    ];

    private static readonly Lazy<BitmapSource[]> Icons = new(LoadIcons);
    public static IReadOnlyList<RuleIconChoice> Choices { get; } = Names.Select((name, id) => new RuleIconChoice(id, name.English, name.Korean)).ToArray();
    public static int Count => Names.Length;
    public static BitmapSource Get(int id) => Icons.Value[Math.Clamp(id, 0, Count - 1)];

    public static int Suggest(string name)
    {
        name = name.ToLowerInvariant();
        if (name.Contains("사진") || name.Contains("photo") || name.Contains("picture")) return 1;
        if (name.Contains("문서") || name.Contains("document")) return 2;
        if (name.Contains("개발") || name.Contains("project") || name.Contains("code")) return 3;
        if (name.Contains("음악") || name.Contains("music")) return 4;
        if (name.Contains("동영상") || name.Contains("video")) return 5;
        if (name.Contains("다운로드") || name.Contains("download")) return 6;
        if (name.Contains("db") || name.Contains("database")) return 7;
        if (name.Contains("회계") || name.Contains("account") || name.Contains("spreadsheet")) return 9;
        if (name.Contains("노트") || name.Contains("note")) return 15;
        return 0;
    }

    private static BitmapSource[] LoadIcons()
    {
        var result = new BitmapSource[Count];
        for (var atlasIndex = 0; atlasIndex < AtlasFiles.Length; atlasIndex++)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "RuleAtlas", AtlasFiles[atlasIndex] + ".png");
            var atlas = new BitmapImage(new Uri(path));
            var cellWidth = atlas.PixelWidth / 3;
            var cellHeight = atlas.PixelHeight / 3;
            for (var cell = 0; cell < 9; cell++)
            {
                var cropped = new CroppedBitmap(atlas, new Int32Rect(cell % 3 * cellWidth, cell / 3 * cellHeight, cellWidth, cellHeight));
                cropped.Freeze();
                result[atlasIndex * 9 + cell] = cropped;
            }
        }
        return result;
    }
}
