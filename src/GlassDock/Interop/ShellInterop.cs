using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GlassDock.Interop;

/// <summary>Shell COM interfaces used for icons, shortcut resolution and AppUserModelIDs.</summary>
internal static class ShellInterop
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; }
    }

    public static readonly PROPERTYKEY PKEY_AppUserModel_ID = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    public static readonly PROPERTYKEY PKEY_ItemNameDisplay = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);

    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public nint p;
        [FieldOffset(16)] public nint p2;
    }
    public const ushort VT_LPWSTR = 31;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    public static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { PropVariantClear(ref pv); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, nint findData, uint flags);
        void GetIDList(out nint pidl);
        void SetIDList(nint pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLink { }

    [Flags]
    public enum SIIGBF : uint { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 0x10, ScaleUp = 0x100 }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Native.SIZE size, SIIGBF flags, out nint hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, nint bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    /// <param name="size">LOWORD: large icon size in px; HIWORD: small icon size.</param>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHDefExtractIconW(string iconFile, int index, uint flags, out nint large, out nint small, uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

    /// <summary>The folder's real location (it may have been moved to another drive), or null.</summary>
    public static string? TryGetKnownFolder(Guid id)
    {
        int hr = SHGetKnownFolderPath(ref id, 0, 0, out nint p);
        try { return hr == 0 ? Marshal.PtrToStringUni(p) : null; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    public const uint SIGDN_NORMALDISPLAY = 0;

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(nint bindCtx, ref Guid bhid, ref Guid riid, out nint ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out nint name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    // ---- DIB extraction -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(nint hdc, nint hbmp, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hwnd, nint hdc);

    /// <summary>Copies a 32-bit HBITMAP into a top-down premultiplied BGRA buffer.</summary>
    public static byte[]? ReadBitmap(nint hbmp, out int width, out int height)
    {
        width = height = 0;
        if (Native.GetObject(hbmp, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
        width = bm.bmWidth;
        height = bm.bmHeight;
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var bits = new byte[width * height * 4];
        nint dc = GetDC(0);
        try
        {
            return GetDIBits(dc, hbmp, 0, (uint)height, bits, ref bmi, 0) == height ? bits : null;
        }
        finally { ReleaseDC(0, dc); }
    }

    public static void ReleaseCom(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}
