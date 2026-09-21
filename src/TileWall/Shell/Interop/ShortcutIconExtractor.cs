using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using TileWall.Core.Entries;
using TileWall.Core.Import;
using TileWall.Shell.Interop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TileWall.Shell.Interop;

/// <summary>
/// 候选图标提取真身（M8 设计 §5.4）：SHGetFileInfoW(SHGFI_ICON | SHGFI_LARGEICON) 对 .lnk 返回链接自身
/// 图标项（shell 解析、不启动目标）；.url 直取失败时尽力解析 IconFile 行再取一次。
/// HICON → GetIconInfo/GetDIBits 取 BGRA 位 → BitmapEncoder 编 PNG。
/// R-3：任何失败（含畸形候选的 COM 异常）全部捕获落 null → UI 占位字形；提取不参与导入事务，绝不写入配置引用。
/// </summary>
public sealed class ShortcutIconExtractor : IShortcutIconExtractor
{
    public byte[]? Extract(string fullPath, EntryKind kind)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            var hIcon = GetFileIcon(fullPath);
            if (hIcon == IntPtr.Zero && kind == EntryKind.Url)
            {
                var iconFile = TryReadUrlIconFile(fullPath);
                if (iconFile is not null && File.Exists(iconFile))
                {
                    hIcon = GetFileIcon(iconFile);
                }
            }

            if (hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return EncodePng(hIcon);
            }
            finally
            {
                _ = Win32Api.DestroyIcon(hIcon); // R2 惯例：图标句柄即取即毁
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException
                                   or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return null; // 畸形候选/系统拒绝 → 占位字形，绝不让导入流程失败
        }
    }

    private static IntPtr GetFileIcon(string path)
    {
        var info = new Win32Api.ShFileInfoW
        {
            szDisplayName = string.Empty,
            szTypeName = string.Empty,
        };
        var size = (uint)Marshal.SizeOf<Win32Api.ShFileInfoW>();
        var result = Win32Api.SHGetFileInfoW(path, 0, ref info, size, Win32Api.ShgfiIcon | Win32Api.ShgfiLargeicon);
        return result != IntPtr.Zero ? info.hIcon : IntPtr.Zero;
    }

    /// <summary>.url 的 IconFile=/IconIndex= 行解析（INI 风格纯文本；失败 → null）。</summary>
    private static string? TryReadUrlIconFile(string urlPath)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(urlPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line["IconFile=".Length..].Trim();
                    var comma = value.LastIndexOf(',');
                    return comma >= 0 ? value[..comma] : value; // IconFile=路径,索引 形态取路径部分
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>HICON → BGRA 像素（GetDIBits 自上而下）→ PNG 字节（BitmapEncoder）。失败 → null。</summary>
    private static byte[]? EncodePng(IntPtr hIcon)
    {
        var info = new Win32Api.IconInfoW();
        if (!Win32Api.GetIconInfo(hIcon, ref info))
        {
            return null;
        }

        try
        {
            if (info.hbmColor == IntPtr.Zero || Win32Api.GetObjectW(info.hbmColor, Marshal.SizeOf<Win32Api.BitmapW>(), out var bitmap) == 0)
            {
                return null;
            }

            var width = bitmap.bmWidth;
            var height = Math.Abs(bitmap.bmHeight);
            if (width <= 0 || height <= 0 || width > 256 || height > 256)
            {
                return null; // 防御：异常尺寸不进编码
            }

            var bmi = new Win32Api.BitmapInfoW { bmiHeader = Win32Api.BitmapInfoHeaderW.BgraTopDown(width, height) };
            var pixels = Marshal.AllocHGlobal(width * height * 4);
            try
            {
                var hdc = Win32Api.CreateCompatibleDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var lines = Win32Api.GetDIBits(hdc, info.hbmColor, 0, (uint)height, pixels, ref bmi, Win32Api.DibRgbColors);
                    if (lines != height)
                    {
                        return null;
                    }
                }
                finally
                {
                    _ = Win32Api.DeleteDC(hdc);
                }

                var managed = new byte[width * height * 4];
                Marshal.Copy(pixels, managed, 0, managed.Length);
                return PngEncode(managed, width, height);
            }
            finally
            {
                Marshal.FreeHGlobal(pixels);
            }
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero)
            {
                _ = Win32Api.DeleteObject(info.hbmColor);
            }

            if (info.hbmMask != IntPtr.Zero)
            {
                _ = Win32Api.DeleteObject(info.hbmMask);
            }
        }
    }

    /// <summary>WinRT BitmapEncoder 同步编码（调用方为后台线程；异步等待在纯计算线程上无死锁面）。</summary>
    private static byte[]? PngEncode(byte[] bgraPixels, int width, int height)
    {
        try
        {
            var stream = new InMemoryRandomAccessStream();
            var encoderTask = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask();
            encoderTask.Wait();
            var encoder = encoderTask.Result;
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight, // 图标位为直通 alpha（非预乘）
                (uint)width,
                (uint)height,
                96,
                96,
                bgraPixels);
            encoder.FlushAsync().AsTask().Wait();
            var size = (int)stream.Size;
            var bytes = new byte[size];
            var reader = new DataReader(stream.GetInputStreamAt(0));
            var loadTask = reader.LoadAsync((uint)size).AsTask();
            loadTask.Wait();
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or COMException)
        {
            return null; // 编码不可得（极端环境）→ 占位字形
        }
    }
}
