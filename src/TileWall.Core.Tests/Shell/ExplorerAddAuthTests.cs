using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

/// <summary>C1/F-01：WM_COPYDATA 发送方鉴权判定（纯函数）——魔数、映像路径一致性、综合裁定。</summary>
public sealed class ExplorerAddAuthTests
{
    private const string SelfPath = @"C:\Apps\TileWall\TileWall.exe";

    [Fact]
    public void 私有魔数为_TWl1_的小端数值()
    {
        Assert.Equal(0x314C5754u, ExplorerAddAuth.Magic); // 'T'54 'W'57 'L'4C '1'31 little-endian
        Assert.True(ExplorerAddAuth.HasMagic(ExplorerAddAuth.Magic));
        Assert.False(ExplorerAddAuth.HasMagic(0));
        Assert.False(ExplorerAddAuth.HasMagic(ExplorerAddAuth.Magic ^ 0x1)); // 最低位扰动即失配
    }

    [Fact]
    public void 魔数不符_直接拒绝_不进入映像比对()
    {
        Assert.Equal(
            ExplorerAddAuthVerdict.RejectBadMagic,
            ExplorerAddAuth.Verify(0, SelfPath, SelfPath)); // 即便映像完全一致也先拒魔数
        Assert.Equal(
            ExplorerAddAuthVerdict.RejectBadMagic,
            ExplorerAddAuth.Verify(uint.MaxValue, SelfPath, SelfPath));
    }

    [Fact]
    public void 合法发送方_通过()
    {
        Assert.Equal(ExplorerAddAuthVerdict.Accept, ExplorerAddAuth.Verify(ExplorerAddAuth.Magic, SelfPath, SelfPath));
    }

    [Fact]
    public void 映像比对_大小写不敏感()
    {
        Assert.True(ExplorerAddAuth.SenderImageMatches(@"c:\apps\tilewall\tilewall.exe", @"C:\APPS\TILEWALL\TILEWALL.EXE"));
        Assert.Equal(
            ExplorerAddAuthVerdict.Accept,
            ExplorerAddAuth.Verify(ExplorerAddAuth.Magic, @"c:\apps\tilewall\tilewall.exe", SelfPath));
    }

    [Fact]
    public void 映像比对_尾部分隔符容忍()
    {
        Assert.True(ExplorerAddAuth.SenderImageMatches(SelfPath + @"\", SelfPath));
        Assert.True(ExplorerAddAuth.SenderImageMatches(SelfPath, SelfPath + "/")); // 正斜杠同样容忍
    }

    [Fact]
    public void 映像不一致_拒绝()
    {
        Assert.Equal(
            ExplorerAddAuthVerdict.RejectUnknownSender,
            ExplorerAddAuth.Verify(ExplorerAddAuth.Magic, @"D:\OtherCopy\TileWall.exe", SelfPath));
        Assert.False(ExplorerAddAuth.SenderImageMatches(@"C:\Apps\TileWall\TileWall2.exe", SelfPath));
        Assert.False(ExplorerAddAuth.SenderImageMatches(@"C:\Apps\TileWall\TileWall.exe.bak", SelfPath));
    }

    [Fact]
    public void 空路径_保守拒绝()
    {
        Assert.False(ExplorerAddAuth.SenderImageMatches(null, SelfPath));
        Assert.False(ExplorerAddAuth.SenderImageMatches(string.Empty, SelfPath));
        Assert.False(ExplorerAddAuth.SenderImageMatches(" ", SelfPath));
        Assert.Equal(
            ExplorerAddAuthVerdict.RejectUnknownSender,
            ExplorerAddAuth.Verify(ExplorerAddAuth.Magic, null, SelfPath));
        Assert.Equal(
            ExplorerAddAuthVerdict.RejectSelfPathUnavailable,
            ExplorerAddAuth.Verify(ExplorerAddAuth.Magic, SelfPath, null)); // 本进程路径不可得：无法完成比对
    }
}
