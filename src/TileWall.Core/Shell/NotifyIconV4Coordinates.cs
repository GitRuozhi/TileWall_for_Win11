namespace TileWall.Core.Shell;

/// <summary>NOTIFYICON_VERSION_4 events whose wParam contains signed screen coordinates.</summary>
public static class NotifyIconV4Coordinates
{
    private const uint WmMouseFirst = 0x0200;
    private const uint WmMouseLast = 0x020E;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinPopupOpen = 0x0408;

    /// <summary>
    /// Decodes GET_X_LPARAM/GET_Y_LPARAM only for events where the v4 protocol defines an anchor.
    /// WM_CONTEXTMENU and other events leave wParam undefined and return false.
    /// </summary>
    public static bool TryDecode(uint notificationEvent, IntPtr wParam, out int x, out int y)
    {
        if (!HasAnchor(notificationEvent))
        {
            x = 0;
            y = 0;
            return false;
        }

        var value = wParam.ToInt64();
        x = unchecked((short)(value & 0xFFFF));
        y = unchecked((short)((value >> 16) & 0xFFFF));
        return true;
    }

    private static bool HasAnchor(uint notificationEvent) =>
        (notificationEvent >= WmMouseFirst && notificationEvent <= WmMouseLast) ||
        notificationEvent == NinSelect ||
        notificationEvent == NinKeySelect ||
        notificationEvent == NinPopupOpen;
}
