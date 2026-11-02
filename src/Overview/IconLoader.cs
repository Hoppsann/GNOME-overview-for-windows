using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overview;

/// <summary>Loads crisp, high-resolution shell icons (exe files, shortcuts, shell:AppsFolder items). UI thread only.</summary>
internal static class IconLoader
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    const int SIIGBF_ICONONLY = 0x4;

    // Filled from the UI thread and from the background preloader; images are frozen so either can use them.
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- async loading
    // Loading an icon the first time goes through the shell and can take a while (or hang on a
    // slow drive), so the UI asks for it here and gets it a moment later, on the UI thread.

    static readonly object Gate = new();
    static readonly Dictionary<string, List<Action<ImageSource?>>> Pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<(string Key, Func<ImageSource?> Load)> Queue = new();
    static Thread? _worker;

    /// <summary>True if this icon was loaded before (the result may be null: "no icon").</summary>
    public static bool TryGetCached(string parsingName, int px, out ImageSource? icon) =>
        Cache.TryGetValue(parsingName + "|" + px, out icon);

    /// <summary>Gives the icon to <paramref name="done"/> on the UI thread: at once if cached, else when loaded.</summary>
    public static void GetAsync(string parsingName, int px, Action<ImageSource?> done)
    {
        if (TryGetCached(parsingName, px, out var cached)) { done(cached); return; }
        LoadAsync(parsingName + "|" + px, () => ForParsingName(parsingName, px), done);
    }

    /// <summary>Runs <paramref name="load"/> on the icon thread (once per key, even if asked many times)
    /// and hands the result to <paramref name="done"/> on the calling (UI) thread.</summary>
    public static void LoadAsync(string key, Func<ImageSource?> load, Action<ImageSource?> done)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        Action<ImageSource?> callback = icon => dispatcher.InvokeAsync(() =>
        {
            try { done(icon); } catch (Exception e) { Log.Write(e); }
        });
        lock (Gate)
        {
            if (Pending.TryGetValue(key, out var waiting)) { waiting.Add(callback); return; }
            Pending[key] = new List<Action<ImageSource?>> { callback };
            if (_worker == null)
            {
                _worker = new Thread(Work) { IsBackground = true, Name = "Overview icons" };
                _worker.SetApartmentState(ApartmentState.STA); // shell image APIs want STA
                _worker.Start();
            }
        }
        Queue.Add((key, load));
    }

    static void Work()
    {
        foreach (var (key, load) in Queue.GetConsumingEnumerable())
        {
            ImageSource? icon = null;
            try { icon = load(); } catch (Exception e) { Log.Write($"icon {key}: {e.Message}"); }
            List<Action<ImageSource?>>? waiting;
            lock (Gate)
            {
                Pending.Remove(key, out waiting);
            }
            if (waiting != null) foreach (var cb in waiting) cb(icon);
        }
    }

    /// <summary>Must be called on an STA thread.</summary>
    public static ImageSource? ForParsingName(string parsingName, int px)
    {
        string key = parsingName + "|" + px;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? img = null;
        try { img = Load(parsingName, px); }
        catch (Exception e) { Log.Write($"icon {parsingName}: {e.Message}"); }
        Cache[key] = img;
        return img;
    }

    static ImageSource? Load(string parsingName, int px)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory == null) return null;
        try
        {
            hr = factory.GetImage(new Native.SIZE { cx = px, cy = px }, SIIGBF_ICONONLY, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { Native.DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    /// <summary>Copies a 32-bit HBITMAP keeping its alpha channel (CreateBitmapSourceFromHBitmap drops it).</summary>
    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };
        var px = new byte[w * h * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, hbm, 0, (uint)h, px, ref bmi, 0) == 0) return null;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        bool hasAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }

    /// <summary>Fallback: the icon a window reports about itself.</summary>
    public static ImageSource? FromWindow(IntPtr hwnd)
    {
        try
        {
            IntPtr icon = IntPtr.Zero;
            foreach (var type in new[] { 1, 2, 0 }) // ICON_BIG, ICON_SMALL2, ICON_SMALL
            {
                Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)type, IntPtr.Zero, 0x2 /*ABORTIFHUNG*/, 40, out icon);
                if (icon != IntPtr.Zero) break;
            }
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICONSM);
            if (icon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
