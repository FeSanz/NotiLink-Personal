using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NotiLink.Core.Networking;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using InterpolationMode = System.Drawing.Drawing2D.InterpolationMode;

namespace NotiLink.App.Services;

/// <summary>The sending half of PC ↔ PC screen sharing: while PeerLinkService has at least one
/// viewer, captures this PC's primary monitor and sends it as JPEG frames.
///
/// Capture is DXGI Desktop Duplication (<see cref="DesktopDuplicator"/>) — straight from the GPU,
/// ~1-3 ms, and it only hands over a frame when something on screen actually changed — with GDI
/// CopyFromScreen as the fallback (measured ~28 ms per 1920×1200 grab, so it tops out near 30
/// fps). A frame is encoded only when the screen or the mouse cursor (drawn in by hand: DXGI and
/// GDI both leave it out) changed, at most <see cref="MaxFps"/> per second, and each viewer always
/// gets the newest one as soon as it has room (PeerLinkService.WantsScreenFrame — the ack-based
/// flow control), so a slow network lowers that viewer's frame rate instead of adding delay.</summary>
internal sealed class ScreenCaptureService : IDisposable
{
    private const int MaxWidth = 1920;
    private const long JpegQuality = 65;
    private const int MaxFps = 60;
    private const int GdiMaxFps = 30;

    private readonly PeerLinkService _link;
    private readonly ImageCodecInfo _jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public event Action<string>? Log;

    public ScreenCaptureService(PeerLinkService link) => _link = link;

    /// <summary>Starts or stops the capture loop to match whether anyone is watching.</summary>
    public void Update()
    {
        lock (_gate)
        {
            var anyViewer = _link.ScreenViewers().Count > 0;
            if (anyViewer && _cts == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => LoopAsync(token));
            }
            else if (!anyViewer && _cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        DesktopDuplicator? dxgi = null;
        Bitmap? screen = null;   // full-resolution grab, no cursor
        Bitmap? output = null;   // what gets encoded: scaled + cursor
        var hasImage = false;
        byte[]? latest = null;
        long version = 0;
        var sentVersion = new Dictionary<string, long>();
        var lastCursor = (X: int.MinValue, Y: 0, Handle: IntPtr.Zero);
        var clock = new Stopwatch();
        try
        {
            while (!token.IsCancellationRequested)
            {
                clock.Restart();
                var viewers = _link.ScreenViewers();
                foreach (var gone in sentVersion.Keys.Except(viewers).ToList()) sentVersion.Remove(gone);
                var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
                if (viewers.Count == 0 || bounds.Width <= 0) { await Task.Delay(20, token).ConfigureAwait(false); continue; }

                // (Re)build everything sized to the monitor — first run, or the resolution changed.
                if (screen == null || screen.Width != bounds.Width || screen.Height != bounds.Height)
                {
                    dxgi?.Dispose();
                    dxgi = DesktopDuplicator.TryCreate(bounds, out var why);
                    Log?.Invoke(dxgi != null ? $"screen capture: DXGI {bounds.Width}x{bounds.Height}" : $"screen capture: GDI fallback ({why})");
                    screen?.Dispose();
                    output?.Dispose();
                    screen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                    var scale = Math.Min(1.0, MaxWidth / (double)bounds.Width);
                    output = new Bitmap((int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale), PixelFormat.Format24bppRgb);
                    hasImage = false;
                }

                var changed = false;
                if (dxgi != null)
                {
                    switch (dxgi.TryCapture(screen, timeoutMs: 8))
                    {
                        case DesktopDuplicator.Outcome.NewFrame: changed = true; hasImage = true; break;
                        case DesktopDuplicator.Outcome.Lost:
                            // Mode change, secure desktop, fullscreen app switching... — start over.
                            dxgi.Dispose();
                            dxgi = DesktopDuplicator.TryCreate(bounds, out _);
                            break;
                    }
                }
                else if (GdiCapture(screen, bounds))
                {
                    changed = true;
                    hasImage = true;
                }

                var cursor = CursorState();
                var cursorMoved = cursor != lastCursor;
                if (hasImage && (changed || cursorMoved || latest == null))
                {
                    lastCursor = cursor;
                    latest = Encode(screen, output!, bounds, cursor);
                    version++;
                }

                if (latest != null)
                {
                    foreach (var pcId in viewers)
                    {
                        if (sentVersion.TryGetValue(pcId, out var v) && v >= version) continue;
                        if (!_link.WantsScreenFrame(pcId)) continue;
                        sentVersion[pcId] = version;
                        await _link.SendScreenFrameAsync(pcId, latest, output!.Width, output.Height).ConfigureAwait(false);
                    }
                }

                var minInterval = 1000.0 / (dxgi != null ? MaxFps : GdiMaxFps);
                var wait = minInterval - clock.Elapsed.TotalMilliseconds;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait)), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"screen capture stopped: {ex.Message}");
        }
        finally
        {
            dxgi?.Dispose();
            screen?.Dispose();
            output?.Dispose();
        }
    }

    private static bool GdiCapture(Bitmap screen, Rectangle bounds)
    {
        try
        {
            using var g = Graphics.FromImage(screen);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException)
        {
            // The secure desktop (UAC prompt, Ctrl+Alt+Supr, lock screen) can't be captured — just
            // skip frames until it's gone.
            return false;
        }
    }

    private byte[] Encode(Bitmap screen, Bitmap output, Rectangle bounds, (int X, int Y, IntPtr Handle) cursor)
    {
        using (var g = Graphics.FromImage(output))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = output.Width == screen.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(screen, 0, 0, output.Width, output.Height);
            if (cursor.Handle != IntPtr.Zero)
            {
                var scale = output.Width / (double)screen.Width;
                DrawCursor(g, cursor.Handle, (int)Math.Round((cursor.X - bounds.Left) * scale), (int)Math.Round((cursor.Y - bounds.Top) * scale));
            }
        }
        using var ms = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        output.Save(ms, _jpeg, parameters);
        return ms.ToArray();
    }

    private static (int X, int Y, IntPtr Handle) CursorState()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0) return (0, 0, IntPtr.Zero);
        return (info.ptScreenPos.X, info.ptScreenPos.Y, info.hCursor);
    }

    private static void DrawCursor(Graphics g, IntPtr cursor, int x, int y)
    {
        int hotX = 0, hotY = 0;
        if (GetIconInfo(cursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, x - hotX, y - hotY, cursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>DXGI Desktop Duplication of one monitor: the D3D11 device is created on the
    /// adapter that owns that output (required on hybrid-GPU laptops), each new desktop image is
    /// copied into a CPU-readable staging texture and from there into a GDI bitmap.</summary>
    internal sealed class DesktopDuplicator : IDisposable
    {
        public enum Outcome { NewFrame, NoChange, Lost }

        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly ID3D11Texture2D _staging;
        private readonly int _width, _height;

        private DesktopDuplicator(ID3D11Device device, IDXGIOutputDuplication duplication, int width, int height)
        {
            _device = device;
            _context = device.ImmediateContext;
            _duplication = duplication;
            _width = width;
            _height = height;
            _staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        public static DesktopDuplicator? TryCreate(Rectangle bounds, out string reason)
        {
            reason = "monitor no encontrado";
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
                {
                    using (adapter)
                    {
                        for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                        {
                            using (output)
                            {
                                var desc = output.Description;
                                var r = desc.DesktopCoordinates;
                                if (r.Left != bounds.Left || r.Top != bounds.Top || r.Right - r.Left != bounds.Width || r.Bottom - r.Top != bounds.Height)
                                    continue;
                                if (desc.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                                {
                                    reason = "monitor girado";
                                    return null;
                                }
                                var created = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                    new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 }, out var device);
                                if (created.Failure || device == null) { reason = $"D3D11 {created.Code:X}"; return null; }
                                try
                                {
                                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                                    var duplication = output1.DuplicateOutput(device);
                                    return new DesktopDuplicator(device, duplication, bounds.Width, bounds.Height);
                                }
                                catch
                                {
                                    device.Dispose();
                                    throw;
                                }
                            }
                        }
                    }
                }
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                reason = ex.Message;
            }
            return null;
        }

        /// <summary>Waits up to [timeoutMs] for the desktop to change and copies it into [target].</summary>
        public Outcome TryCapture(Bitmap target, int timeoutMs)
        {
            var result = _duplication.AcquireNextFrame((uint)timeoutMs, out var info, out var resource);
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return Outcome.NoChange;
            if (result.Failure) return Outcome.Lost;
            try
            {
                // Only the pointer moved — nothing new to copy (the cursor is drawn separately).
                if (info.LastPresentTime == 0) return Outcome.NoChange;
                using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                    _context.CopyResource(_staging, texture);
                var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var data = target.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        var rowBytes = (nuint)(_width * 4);
                        if ((int)mapped.RowPitch == data.Stride)
                        {
                            CopyMemory(data.Scan0, mapped.DataPointer, (nuint)data.Stride * (nuint)_height);
                        }
                        else
                        {
                            for (var y = 0; y < _height; y++)
                                CopyMemory(data.Scan0 + y * data.Stride, mapped.DataPointer + (nint)(y * mapped.RowPitch), rowBytes);
                        }
                    }
                    finally { target.UnlockBits(data); }
                }
                finally { _context.Unmap(_staging, 0); }
                return Outcome.NewFrame;
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                return Outcome.Lost;
            }
            finally
            {
                resource?.Dispose();
                try { _duplication.ReleaseFrame(); } catch (SharpGen.Runtime.SharpGenException) { }
            }
        }

        public void Dispose()
        {
            _staging.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);
    }

    private const int CURSOR_SHOWING = 0x1;
    private const int DI_NORMAL = 0x3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
