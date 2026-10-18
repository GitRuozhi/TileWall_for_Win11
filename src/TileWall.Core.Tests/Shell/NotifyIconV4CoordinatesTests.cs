using Xunit;

using TileWall.Core.Shell;

namespace TileWall.Core.Tests.Shell;

public sealed class NotifyIconV4CoordinatesTests
{
    [Theory]
    [InlineData(0x0200u)] // WM_MOUSEFIRST
    [InlineData(0x020Eu)] // WM_MOUSELAST
    [InlineData(0x0400u)] // NIN_SELECT
    [InlineData(0x0401u)] // NIN_KEYSELECT
    [InlineData(0x0408u)] // NIN_POPUPOPEN
    public void DefinedV4AnchorEventsDecodeSignedScreenCoordinates(uint notificationEvent)
    {
        var wParam = new IntPtr(unchecked((int)0xFFFDFFFC));

        Assert.True(NotifyIconV4Coordinates.TryDecode(notificationEvent, wParam, out var x, out var y));
        Assert.Equal(-4, x);
        Assert.Equal(-3, y);
    }

    [Theory]
    [InlineData(0x007Bu)] // WM_CONTEXTMENU: wParam is undefined for v4 callbacks.
    [InlineData(0x0402u)] // NIN_BALLOONSHOW
    public void UndefinedAnchorEventsDoNotInterpretWParam(uint notificationEvent)
    {
        Assert.False(NotifyIconV4Coordinates.TryDecode(notificationEvent, new IntPtr(0x00020001), out var x, out var y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }
}
