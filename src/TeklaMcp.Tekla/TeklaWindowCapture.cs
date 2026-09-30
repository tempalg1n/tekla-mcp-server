using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Tekla;

/// <summary>
/// Win32 half of <c>tekla_capture_view</c>: find Tekla's view windows and copy the pixels Tekla
/// rendered into them.
///
/// Probed live (Tekla 2023, Windows 10 19045, 2026-09-29): the model views are MDI children —
/// main <c>HwndWrapper[TeklaStructures.exe;…]</c> → <c>AfxMDIFrame140</c> → <c>MDIClient</c> →
/// one <c>AfxFrameOrView140</c> per view titled "View N - &lt;view name&gt;" → the render surface
/// ("dakit external view for zkit"). <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> returns
/// the DirectX-rendered view in ~25 ms even when other applications' windows or Tekla dialogs
/// overlap it; without the flag the frame is blank. It returns the composition of Tekla's MAIN
/// window in that rectangle, so an MDI view hidden behind another view comes back as whatever
/// covers it — covered views are refused instead of captured.
///
/// Never replace this with a screen copy (BitBlt/CopyFromScreen of the desktop): the same probe
/// captured an overlapping Explorer window listing the user's private files instead of the model.
/// </summary>
internal static class TeklaWindowCapture
{
    internal sealed class ViewWindow
    {
        public IntPtr Frame;
        public IntPtr Surface;
        public string Title = "";
        public bool Active;
        public bool Minimized;
        public bool Covered;
        public string CoveredBy = "";
        public int Width;
        public int Height;
    }

    internal sealed class MainWindow
    {
        public IntPtr Handle;
        public int ProcessId;
        public string Title = "";
        public bool Minimized;
        public IntPtr MdiClient;
        public List<ViewWindow> Views = new List<ViewWindow>();
        public ViewWindow? Active => Views.FirstOrDefault(v => v.Active);
    }

    /// <summary>True when an MDI frame title ("View 4 - 3D") belongs to the Tekla view named <paramref name="viewName"/>.</summary>
    public static bool TitleMatches(string? title, string? viewName)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(viewName)) return false;
        var t = title!.Trim();
        var n = viewName!.Trim();
        return string.Equals(t, n, StringComparison.OrdinalIgnoreCase) ||
               t.EndsWith(" - " + n, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Locate the Tekla main window showing <paramref name="modelPath"/> (the title carries it) and
    /// describe its model-view windows. Null + <paramref name="error"/> when there is none.
    /// </summary>
    public static MainWindow? Find(string? modelPath, out string error)
    {
        error = "";
        using (DpiScope.PerMonitor())
        {
            var candidates = new List<(int Pid, IntPtr Handle, string Title)>();
            foreach (var process in Process.GetProcessesByName("TeklaStructures"))
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero) candidates.Add((process.Id, handle, process.MainWindowTitle ?? ""));
                }
                catch
                {
                    // Access denied / exited: not ours.
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (candidates.Count == 0)
            {
                error = "No Tekla Structures window was found in this Windows session. The MCP server must run " +
                        "on the desktop where Tekla is open.";
                return null;
            }

            var path = (modelPath ?? "").Trim().TrimEnd('\\', '/');
            var chosen = candidates.FirstOrDefault(c =>
                path.Length > 0 && c.Title.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0);
            if (chosen.Handle == IntPtr.Zero)
            {
                if (candidates.Count > 1)
                {
                    error = "Several Tekla windows are open and none shows the connected model '" + modelPath +
                            "' in its title: " + string.Join("; ", candidates.Select(c => c.Title)) + ".";
                    return null;
                }
                chosen = candidates[0];
            }

            var main = new MainWindow
            {
                Handle = chosen.Handle,
                ProcessId = chosen.Pid,
                Title = chosen.Title,
                Minimized = IsIconic(chosen.Handle),
            };

            main.MdiClient = FindDescendantByClass(chosen.Handle, "MDIClient");
            if (main.MdiClient == IntPtr.Zero)
            {
                error = "Tekla's view area (MDIClient) was not found in window '" + chosen.Title + "'.";
                return main;
            }

            var active = SendMessage(main.MdiClient, WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero);
            foreach (var frame in DirectChildren(main.MdiClient))
            {
                if (!IsWindowVisible(frame)) continue;
                var view = new ViewWindow
                {
                    Frame = frame,
                    Title = GetText(frame),
                    Active = frame == active,
                    Minimized = IsIconic(frame),
                };
                view.Surface = LargestVisibleDescendant(frame);
                if (view.Surface == IntPtr.Zero) view.Surface = frame;
                if (GetWindowRect(view.Surface, out var rect))
                {
                    view.Width = rect.Right - rect.Left;
                    view.Height = rect.Bottom - rect.Top;
                }
                view.Covered = IsCovered(frame, view.Surface, out view.CoveredBy);
                main.Views.Add(view);
            }
            return main;
        }
    }

    /// <summary>Copy the pixels of <paramref name="surface"/> (the verified PrintWindow path). Caller disposes.</summary>
    public static Bitmap? Capture(IntPtr surface, out string error)
    {
        error = "";
        using (DpiScope.PerMonitor())
        {
            if (!GetWindowRect(surface, out var rect))
            {
                error = "The view window is gone (it was closed or re-created).";
                return null;
            }
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 16 || height < 16)
            {
                error = "The view window is too small to capture (" + width + "x" + height + ").";
                return null;
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            bool ok;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    ok = PrintWindow(surface, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            if (!ok)
            {
                bitmap.Dispose();
                error = "PrintWindow failed for the view window (Win32 error " + Marshal.GetLastWin32Error() + ").";
                return null;
            }
            return bitmap;
        }
    }

    /// <summary>Cheap fingerprint (sampled pixels) to tell whether Tekla finished redrawing.</summary>
    public static long Fingerprint(Bitmap bitmap, out bool uniform)
    {
        uniform = true;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var row = new byte[stride];
            unchecked
            {
                var hash = (long)14695981039346656037UL;
                var first = -1;
                for (var y = 0; y < bitmap.Height; y += 6)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                    for (var x = 0; x < bitmap.Width; x += 6)
                    {
                        var i = x * 3;
                        var value = row[i] | (row[i + 1] << 8) | (row[i + 2] << 16);
                        if (first < 0) first = value;
                        else if (value != first) uniform = false;
                        hash = (hash ^ value) * 1099511628211L;
                    }
                }
                return hash;
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Downscale to fit <paramref name="maxWidth"/>×<paramref name="maxHeight"/> and encode as JPEG or PNG.</summary>
    public static RenderedImage Encode(Bitmap bitmap, int maxWidth, int maxHeight, string? format)
    {
        maxWidth = Math.Max(160, Math.Min(maxWidth, 2400));
        maxHeight = Math.Max(120, Math.Min(maxHeight, 2400));
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / bitmap.Width, (double)maxHeight / bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var png = string.Equals((format ?? "").Trim(), "png", StringComparison.OrdinalIgnoreCase);

        Bitmap? scaled = null;
        try
        {
            var source = bitmap;
            if (scale < 1)
            {
                scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height));
                }
                source = scaled;
            }

            using (var stream = new MemoryStream())
            {
                if (png)
                {
                    source.Save(stream, ImageFormat.Png);
                }
                else
                {
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                        source.Save(stream, codec, parameters);
                    }
                }
                return new RenderedImage
                {
                    Bytes = stream.ToArray(),
                    MimeType = png ? "image/png" : "image/jpeg",
                    Width = width,
                    Height = height,
                };
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    // ------------------------------------------------------------------------ window tree ---

    private static IntPtr FindDescendantByClass(IntPtr parent, string className)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (child, _) =>
        {
            if (GetClass(child) != className) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static List<IntPtr> DirectChildren(IntPtr parent)
    {
        var result = new List<IntPtr>();
        for (var child = GetWindow(parent, GW_CHILD); child != IntPtr.Zero; child = GetWindow(child, GW_HWNDNEXT))
            result.Add(child);
        return result;
    }

    private static IntPtr LargestVisibleDescendant(IntPtr parent)
    {
        var best = IntPtr.Zero;
        long bestArea = 0;
        EnumChildWindows(parent, (child, _) =>
        {
            if (!IsWindowVisible(child) || !GetWindowRect(child, out var rect)) return true;
            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = child;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>True when a visible MDI sibling above <paramref name="frame"/> overlaps its render surface.</summary>
    private static bool IsCovered(IntPtr frame, IntPtr surface, out string coveredBy)
    {
        coveredBy = "";
        if (!GetWindowRect(surface, out var target)) return false;
        for (var above = GetWindow(frame, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
        {
            if (!IsWindowVisible(above) || IsIconic(above) || !GetWindowRect(above, out var rect)) continue;
            if (rect.Left < target.Right && target.Left < rect.Right && rect.Top < target.Bottom && target.Top < rect.Bottom)
            {
                coveredBy = GetText(above);
                return true;
            }
        }
        return false;
    }

    private static string GetText(IntPtr handle)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetClass(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>
    /// Per-monitor DPI awareness for the calling thread while measuring/capturing, so window
    /// rectangles and PrintWindow agree on a scaled display. No-op before Windows 10 1607.
    /// </summary>
    private sealed class DpiScope : IDisposable
    {
        private readonly IntPtr _previous;

        private DpiScope(IntPtr previous) => _previous = previous;

        public static DpiScope PerMonitor()
        {
            try
            {
                return new DpiScope(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));
            }
            catch (EntryPointNotFoundException)
            {
                return new DpiScope(IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            if (_previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(_previous); }
            catch (EntryPointNotFoundException) { }
        }
    }

    // --------------------------------------------------------------------------- interop ---

    private const uint PW_RENDERFULLCONTENT = 0x2;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;
    private const uint GW_CHILD = 5;
    private const int WM_MDIGETACTIVE = 0x0229;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
