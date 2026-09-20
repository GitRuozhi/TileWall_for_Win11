using TileWall.Core.Entries;
using Xunit;

namespace TileWall.Core.Tests.Entries;

/// <summary>T-NAME（M4 设计 §10.2）：EntryNames 正/负例矩阵 —— [R10] 文件名规则纯函数。</summary>
public class EntryNamesTests
{
    [Theory]
    [InlineData("工作浏览器")]
    [InlineData("My Tile 2")]
    [InlineData("a")]
    [InlineData("网址.收藏夹")] // 中间点合法（保留判定只看首个 '.' 之前）
    [InlineData("CONX")]
    [InlineData("COM10")] // 两位数不保留（[R10] 官方规则 COM1–9）
    [InlineData("audit")]
    public void Validate_合法名_返回空(string name)
    {
        Assert.Empty(EntryNames.Validate(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_空或纯空白_NAME_EMPTY(string name)
    {
        Assert.Equal([EntryNames.NameEmpty], EntryNames.Validate(name));
    }

    [Theory]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a\"b")]
    public void Validate_非法字符_NAME_INVALID_CHARS(string name)
    {
        Assert.Contains(EntryNames.NameInvalidChars, EntryNames.Validate(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("PRN")]
    [InlineData("AUX.png")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    public void Validate_保留名含主体保留_NAME_RESERVED(string name)
    {
        Assert.Contains(EntryNames.NameReserved, EntryNames.Validate(name));
    }

    [Theory]
    [InlineData("name.")]
    [InlineData("name ")]
    public void Validate_尾点尾空格_NAME_TRAILING_JUNK(string name)
    {
        Assert.Contains(EntryNames.NameTrailingJunk, EntryNames.Validate(name));
    }

    [Fact]
    public void Validate_200字符_合法_201_超长()
    {
        Assert.Empty(EntryNames.Validate(new string('a', EntryNames.MaxBaseNameLength)));
        var errors = EntryNames.Validate(new string('a', EntryNames.MaxBaseNameLength + 1));
        Assert.Equal([EntryNames.NameTooLong], errors);
    }

    [Fact]
    public void Validate_保留名兼尾点_多码并列()
    {
        var errors = EntryNames.Validate("CON.");
        Assert.Contains(EntryNames.NameReserved, errors);
        Assert.Contains(EntryNames.NameTrailingJunk, errors);
    }

    [Fact]
    public void CombineName_扩展名由类型决定_用户不可编辑()
    {
        Assert.Equal("工作浏览器.lnk", EntryNames.CombineName("工作浏览器", EntryKind.Lnk));
        Assert.Equal("工作浏览器.url", EntryNames.CombineName("工作浏览器", EntryKind.Url));
        Assert.Throws<ArgumentOutOfRangeException>(() => EntryNames.CombineName("x", EntryKind.None));
    }

    [Theory]
    [InlineData("Objects/o1/工作浏览器.lnk", EntryKind.Lnk)]
    [InlineData("Objects/o1/page.URL", EntryKind.Url)]
    [InlineData("Objects/o1/notes.txt", EntryKind.None)]
    [InlineData("nope", EntryKind.None)]
    public void KindOfRelativePath_大小写不敏感扩展名判定(string path, EntryKind expected)
    {
        Assert.Equal(expected, EntryNames.KindOfRelativePath(path));
    }

    [Fact]
    public void SuggestBaseName_网址取主机_路径取主体_文件夹取名()
    {
        Assert.Equal("example.com", EntryNames.SuggestBaseName("https://example.com/path?q=1", targetIsDirectory: false));
        Assert.Equal("notepad", EntryNames.SuggestBaseName("C:\\Windows\\System32\\notepad.exe", targetIsDirectory: false));
        Assert.Equal("项目文件夹", EntryNames.SuggestBaseName("D:\\work\\项目文件夹\\", targetIsDirectory: true));
    }
}
