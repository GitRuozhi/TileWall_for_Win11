using System.Text;
using TileWall.Core.Import;
using Xunit;

namespace TileWall.Core.Tests.Import;

public sealed class WindowsStartLayoutTests
{
    [Fact]
    public void XmlKeepsOriginalGroupsAndSortsByRowThenColumn()
    {
        const string xml = """
            <LayoutModificationTemplate xmlns="http://schemas.microsoft.com/Start/2014/LayoutModification"
                xmlns:layout="http://schemas.microsoft.com/Start/2014/FullDefaultLayout"
                xmlns:start="http://schemas.microsoft.com/Start/2014/StartLayout" Version="1">
              <DefaultLayoutOverride><StartLayoutCollection><layout:StartLayout>
                <start:Group Name="">
                  <start:Tile Row="1" Column="0" AppUserModelID="Packaged.One!App"/>
                  <start:DesktopApplicationTile Row="0" Column="1" DesktopApplicationLinkPath="%APPDATA%\App.lnk"/>
                  <start:DesktopApplicationTile Row="0" Column="1" DesktopApplicationID="Vendor.Editor"/>
                  <start:SecondaryTile Row="0" Column="0" AppUserModelID="Clock!App" Arguments="CITY"/>
                </start:Group>
                <start:Group Name="Resources">
                  <start:DesktopApplicationTile Row="0" Column="0" DesktopApplicationLinkPath="%ALLUSERSPROFILE%\Site.url"/>
                </start:Group>
              </layout:StartLayout></StartLayoutCollection></DefaultLayoutOverride>
            </LayoutModificationTemplate>
            """;
        var layout = WindowsStartLayout.Parse(Encoding.UTF8.GetBytes(xml), false, "Start menu", default);

        Assert.Equal(5, layout.TotalCount);
        Assert.Collection(layout.Groups,
            group =>
            {
                Assert.Equal("", group.Title);
                Assert.Collection(group.Pins,
                    pin => Assert.Equal(WindowsStartPinKind.Unresolved, pin.Kind),
                    pin => Assert.Equal(new(WindowsStartPinKind.LinkPath, @"%APPDATA%\App.lnk"), pin),
                    pin => Assert.Equal(new(WindowsStartPinKind.ApplicationId, "Vendor.Editor"), pin),
                    pin => Assert.Equal(new(WindowsStartPinKind.ApplicationId, "Packaged.One!App"), pin));
            },
            group =>
            {
                Assert.Equal("Resources", group.Title);
                Assert.Equal(new(WindowsStartPinKind.LinkPath, @"%ALLUSERSPROFILE%\Site.url"), Assert.Single(group.Pins));
            });
    }

    [Fact]
    public void JsonRetainsTheEntireFlatOrderAndDetectsAChangedPin()
    {
        const string original = """
            {"pinnedList":[{"desktopAppLink":"%APPDATA%\\One.lnk"},
                           {"packagedAppId":"Packaged.One!App"},
                           {"desktopAppId":"Vendor.Editor"}]}
            """;
        var layout = WindowsStartLayout.Parse(Encoding.UTF8.GetBytes(original), true, "Windows Start", default);
        var group = Assert.Single(layout.Groups);
        Assert.Equal("Windows Start", group.Title);
        Assert.Equal([@"%APPDATA%\One.lnk", "Packaged.One!App", "Vendor.Editor"], group.Pins.Select(pin => pin.Value));

        Assert.True(layout.SamePins(WindowsStartLayout.Parse(Encoding.UTF8.GetBytes(original), true, "Windows Start", default)));
        var updated = original.Replace("Vendor.Editor", "Vendor.Reader", StringComparison.Ordinal);
        Assert.False(layout.SamePins(WindowsStartLayout.Parse(Encoding.UTF8.GetBytes(updated), true, "Windows Start", default)));
    }

    [Fact]
    public void ValidEmptyJsonHasNoItemsAndAbsentOrAmbiguousListIsInvalid()
    {
        Assert.Empty(WindowsStartLayout.Parse("""{"pinnedList":[]}"""u8.ToArray(), true, "Start", default).Groups);
        Assert.Throws<InvalidDataException>(() => WindowsStartLayout.Parse("{}"u8.ToArray(), true, "Start", default));
        Assert.Throws<InvalidDataException>(() => WindowsStartLayout.Parse(
            """{"pinnedList":[],"pinnedList":[]}"""u8.ToArray(), true, "Start", default));
    }
}
