using System.Numerics;
using System.Runtime.InteropServices;
using TileWall.Core.Shell;
using TileWall.Graphics;
using TileWall.Shell;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using ICompositionSupportsSystemBackdrop = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;

namespace TileWall.Views;

/// <summary>One HWND composition target for all main-wall background content.</summary>
internal sealed class NativeWallBackground(IntPtr hwnd) : ICompositionSupportsSystemBackdrop, IDisposable
{
    internal sealed record Cover(Color Color, float Opacity, DecodedImageFrame? Image, RectInt32? DesktopBounds,
        bool SuppressMaterial);

    private static readonly Guid DesktopInteropId = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    private Compositor? _localCompositor;
    private Projection? _projection;
    private Cover? _cover;
    private CompositionBrush? _material;
    private Vector2 _extent;
    private RectInt32 _clientBounds;
    private bool _disposed;

    internal long CoverRevision { get; private set; }
    internal bool HasOpaqueSnapshotCover => _cover is { DesktopBounds: { } bounds, Opacity: 1f } &&
        _projection is { IsAttached: true, IsShowingImage: true } && _clientBounds.Width > 0 && _clientBounds.Height > 0 &&
        _clientBounds.X >= bounds.X && _clientBounds.Y >= bounds.Y &&
        (long)_clientBounds.X + _clientBounds.Width <= (long)bounds.X + bounds.Width &&
        (long)_clientBounds.Y + _clientBounds.Height <= (long)bounds.Y + bounds.Height;

    private PointInt32 ClientOrigin => new(_clientBounds.X, _clientBounds.Y);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDesktopWindowTarget(IntPtr interop, IntPtr hwnd, int topmost, out IntPtr target);

    public CompositionBrush SystemBackdrop
    {
        get => _material!;
        set
        {
            if (_disposed) return;
            if (value is not null) EnsureProjection(value.Compositor);
            if (_projection is not null) _projection.Material.Brush = value;
            _material = value;
        }
    }

    internal void SetScreenExtent(int width, int height)
    {
        var extent = new Vector2(width, height);
        if (width <= 0 || height <= 0 || _extent == extent) return;
        try
        {
            _projection?.SetExtent(extent);
            _extent = extent;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        { AppLog.Warn(nameof(NativeWallBackground), $"Background extent unavailable: 0x{ex.HResult:X8}"); }
    }

    internal void SetClientBounds(RectInt32 bounds)
    {
        if (_clientBounds.Equals(bounds)) return;
        if (_projection is { IsShowingImage: true } active)
        {
            if (_cover?.DesktopBounds is { } snapshot)
            {
                var origin = new PointInt32(bounds.X, bounds.Y);
                if (!ClientOrigin.Equals(origin))
                {
                    active.PositionSnapshot(snapshot, origin);
                    CoverRevision++;
                }
            }
            else if (bounds.Width != _clientBounds.Width || bounds.Height != _clientBounds.Height)
                active.PositionCustom(bounds);
        }
        _clientBounds = bounds;
    }

    internal void SetCover(Cover? cover)
    {
        if (_disposed || _cover == cover && (_projection?.IsAttached == true || _projection is null && cover is null)) return;
        if (_projection is null && cover is null) return;
        EnsureProjection(_projection?.Compositor ?? (_localCompositor ??= new Compositor()));
        _projection!.ApplyCover(cover, _clientBounds);
        _cover = cover;
        CoverRevision++;
    }

    internal void ForgetImage(DecodedImageFrame? frame)
    {
        // A failed replacement can leave the old cover admitted and visible.
        // Retain its surface until a replacement has actually been applied.
        if (!ReferenceEquals(_cover?.Image, frame)) _projection?.ForgetImage(frame);
    }

    internal void ReleaseIdleUploadDevice() => _projection?.ReleaseIdleUploadDevice();

    internal async Task<bool> WaitForCoverCommitAsync(CancellationToken token)
    {
        var projection = _projection;
        if (projection?.IsAttached != true) return false;
        var revision = CoverRevision;
        await projection.Compositor.RequestCommitAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        return !_disposed && projection.IsAttached && ReferenceEquals(projection, _projection) && CoverRevision == revision;
    }

    internal void BeginCoverAnimation(bool show, double scale)
    {
        if (_projection is not { } projection) return;
        ResetCoverAnimation();
        var compositor = projection.Compositor;
        using var ease = compositor.CreateCubicBezierEasingFunction(
            new(1f / 3, show ? 1 : 0), new(2f / 3, show ? 1 : 0));
        using var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = TimeSpan.FromMilliseconds(show ? ShowHideAnimator.ShowDurationMs : ShowHideAnimator.HideDurationMs);
        opacity.InsertKeyFrame(0, show ? 0 : 1);
        opacity.InsertKeyFrame(1, show ? 1 : 0, ease);
        projection.CoverRoot.StartAnimation(nameof(Visual.Opacity), opacity);
        if (!show) return;
        using var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = opacity.Duration;
        slide.InsertKeyFrame(0, new(0, (float)(ShowHideAnimator.ShowSlideDistanceDip * scale), 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        projection.CoverRoot.StartAnimation(nameof(Visual.Offset), slide);
    }

    internal void ResetCoverAnimation()
    {
        if (_projection is not { } projection) return;
        projection.CoverRoot.StopAnimation(nameof(Visual.Opacity));
        projection.CoverRoot.StopAnimation(nameof(Visual.Offset));
        projection.CoverRoot.Opacity = 1;
        projection.CoverRoot.Offset = Vector3.Zero;
    }

    private void EnsureProjection(Compositor compositor)
    {
        if (_projection is { } current && current.Compositor.Equals(compositor))
        {
            if (!current.IsAttached)
            {
                current.Attach(CreateTarget(compositor));
                CoverRevision++;
            }
            return;
        }
        var previous = _projection;
        var pending = new Projection(compositor, _extent);
        try
        {
            pending.ApplyCover(_cover, _clientBounds);
            // A HWND accepts one target at this layer. Prepare the replacement
            // graph before switching, and restore the old one on attach failure.
            previous?.Detach();
            try { pending.Attach(CreateTarget(compositor)); }
            catch
            {
                if (previous is not null)
                {
                    try { previous.Attach(CreateTarget(previous.Compositor)); }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                    { AppLog.Warn(nameof(NativeWallBackground), $"Could not restore background target: 0x{ex.HResult:X8}"); }
                }
                throw;
            }
            _projection = pending;
            CoverRevision++;
        }
        catch
        {
            pending.Dispose();
            throw;
        }
        previous?.Dispose();
    }

    private DesktopWindowTarget CreateTarget(Compositor compositor)
    {
        var unknown = WinRT.MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in DesktopInteropId, out var interop));
            try
            {
                var vtable = Marshal.ReadIntPtr(interop);
                // ICompositorDesktopInterop::CreateDesktopWindowTarget follows IUnknown.
                var method = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTarget>(
                    Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(method(interop, hwnd, 0, out var target));
                try { return DesktopWindowTarget.FromAbi(target); }
                finally { Marshal.Release(target); }
            }
            finally { Marshal.Release(interop); }
        }
        finally { Marshal.Release(unknown); }
    }

    internal void DisconnectMaterialTarget()
    {
        _projection?.Dispose();
        _projection = null;
        _material = null;
        CoverRevision++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectMaterialTarget();
        _cover = null;
        Release(_localCompositor);
        _localCompositor = null;
    }

    private static void Release(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { AppLog.Warn(nameof(NativeWallBackground), $"Background resource unavailable: 0x{ex.HResult:X8}"); }
    }

    private sealed class Projection : IDisposable
    {
        private readonly ContainerVisual _root;
        private readonly SpriteVisual _cover;
        private readonly CompositionColorBrush _solid;
        private DesktopWindowTarget? _target;
        private DesktopCompositionImageDevice? _images;
        private CompositionDrawingSurface? _imageSurface;
        private CompositionSurfaceBrush? _imageBrush;
        private DecodedImageFrame? _image;
        private Vector2 _extent;

        internal Projection(Compositor compositor, Vector2 extent)
        {
            Compositor = compositor;
            _root = compositor.CreateContainerVisual();
            Material = compositor.CreateSpriteVisual();
            CoverRoot = compositor.CreateContainerVisual();
            _cover = compositor.CreateSpriteVisual();
            _solid = compositor.CreateColorBrush();
            CoverRoot.Children.InsertAtTop(_cover);
            _root.Children.InsertAtBottom(Material);
            _root.Children.InsertAtTop(CoverRoot);
            SetExtent(extent);
        }

        internal Compositor Compositor { get; }
        internal SpriteVisual Material { get; }
        internal ContainerVisual CoverRoot { get; }
        internal bool IsShowingImage { get; private set; }
        internal bool IsAttached => _target is not null;

        internal void SetExtent(Vector2 extent)
        {
            _extent = extent;
            // Avoid a size-dependent composition ancestor while the HWND is
            // moving. The outer HWND supplies clipping for each background.
            _root.Size = extent;
            CoverRoot.Size = extent;
            Material.Size = extent;
            if (!IsShowingImage) _cover.Size = extent;
        }

        internal void Attach(DesktopWindowTarget target)
        {
            try
            {
                target.Root = _root;
                _target = target;
            }
            catch
            {
                Release(target);
                throw;
            }
        }

        internal void Detach()
        {
            Release(_target);
            _target = null;
        }

        internal void ApplyCover(Cover? cover, RectInt32 client)
        {
            Material.IsVisible = cover?.SuppressMaterial != true;
            if (cover is null)
            {
                _cover.Brush = null!;
                IsShowingImage = false;
                return;
            }

            IsShowingImage = cover.Image is { } image && LoadImage(image);
            _cover.Opacity = cover.Opacity;
            if (IsShowingImage && cover.DesktopBounds is { } bounds)
            {
                _cover.Size = new(bounds.Width, bounds.Height);
                _imageBrush!.Stretch = CompositionStretch.Fill;
                PositionSnapshot(bounds, new(client.X, client.Y));
            }
            else if (IsShowingImage)
            {
                _imageBrush!.Stretch = CompositionStretch.UniformToFill;
                PositionCustom(client);
            }
            else
            {
                _cover.Size = _extent;
                _cover.Offset = Vector3.Zero;
                _solid.Color = cover.Color;
            }
            _cover.Brush = IsShowingImage ? _imageBrush! : _solid;
        }

        internal void PositionSnapshot(RectInt32 bounds, PointInt32 origin) =>
            _cover.Offset = new(bounds.X - origin.X, bounds.Y - origin.Y, 0);

        internal void PositionCustom(RectInt32 client)
        {
            _cover.Size = client.Width > 0 && client.Height > 0 ? new(client.Width, client.Height) : _extent;
            _cover.Offset = Vector3.Zero;
        }

        private bool LoadImage(DecodedImageFrame frame)
        {
            if (ReferenceEquals(_image, frame)) return _imageSurface is not null;
            CompositionDrawingSurface? pending = null;
            try
            {
                _images ??= new DesktopCompositionImageDevice(Compositor);
                pending = _images.CreateSurface(frame);
                _imageBrush ??= Compositor.CreateSurfaceBrush();
                _imageBrush.HorizontalAlignmentRatio = 0.5f;
                _imageBrush.VerticalAlignmentRatio = 0.5f;
                _imageBrush.Surface = pending;
                Release(_imageSurface);
                _imageSurface = pending;
                _image = frame;
                if (BackdropTrace.Enabled) BackdropTrace.Write($"HWND image uploaded {frame.Width}x{frame.Height}");
                return true;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or OutOfMemoryException)
            {
                Release(pending);
                ForgetImage(_image);
                _image = frame;
                Release(_images);
                _images = null;
                AppLog.Warn(nameof(NativeWallBackground), $"Background upload unavailable: 0x{ex.HResult:X8}");
                return false;
            }
        }


        internal void ForgetImage(DecodedImageFrame? frame)
        {
            if (frame is null || !ReferenceEquals(_image, frame)) return;
            if (_imageBrush is not null) _imageBrush.Surface = null!;
            Release(_imageSurface);
            _imageSurface = null;
            _image = null;
        }

        internal void ReleaseIdleUploadDevice()
        {
            if (_imageSurface is not null || _image is not null) return;
            Release(_images);
            _images = null;
        }

        public void Dispose()
        {
            Detach();
            Release(_cover);
            Release(Material);
            Release(CoverRoot);
            Release(_root);
            Release(_imageBrush);
            Release(_imageSurface);
            Release(_images);
            Release(_solid);
        }
    }
}
