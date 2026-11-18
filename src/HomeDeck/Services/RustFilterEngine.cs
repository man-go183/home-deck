using System.Runtime.InteropServices;
using System.Text;

namespace HomeDeck.Services;

/// <summary>
/// <see cref="AdBlockService.IFilterEngine"/> backed by Brave's adblock-rust
/// via the hdblock native wrapper (src/native/hdblock).
///
/// Request-type codes must match `request_type_name` in lib.rs:
/// 0 = other, 1 = script, 2 = image, 3 = stylesheet, 4 = xmlhttprequest,
/// 5 = subdocument, 6 = font, 7 = media, 8 = websocket.
/// Top-level document/main-frame loads are never sent across (C# skips them).
/// </summary>
sealed class RustFilterEngine : AdBlockService.IFilterEngine
{
    const string Lib = "hdblock";
    const int AbiVersion = 1;

    [DllImport(Lib, EntryPoint = "hdblock_version", CallingConvention = CallingConvention.Cdecl)]
    static extern int Version();

    [DllImport(Lib, EntryPoint = "hdblock_create", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr Create();

    [DllImport(Lib, EntryPoint = "hdblock_load_rules", CallingConvention = CallingConvention.Cdecl)]
    static extern int LoadRulesNative(IntPtr handle, byte[] rules, int len);

    [DllImport(Lib, EntryPoint = "hdblock_should_block", CallingConvention = CallingConvention.Cdecl)]
    static extern int ShouldBlockNative(IntPtr handle, byte[] url, int urlLen, byte[] pageHost, int pageLen, int type);

    [DllImport(Lib, EntryPoint = "hdblock_cosmetic_css", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr CosmeticCssNative(IntPtr handle, byte[] url, int len);

    [DllImport(Lib, EntryPoint = "hdblock_free_string", CallingConvention = CallingConvention.Cdecl)]
    static extern void FreeString(IntPtr s);

    static IntPtr s_handle = IntPtr.Zero;
    static bool s_ready;

    public static int TypeCode(string t) => t switch
    {
        "script" => 1,
        "image" => 2,
        "stylesheet" => 3,
        "xmlhttprequest" => 4,
        "subdocument" => 5,
        "font" => 6,
        "media" => 7,
        "websocket" => 8,
        _ => 0,
    };

    /// <summary>Load (or reload) the engine from concatenated list text.</summary>
    public static bool LoadAllRules(string allText)
    {
        try
        {
            if (s_handle == IntPtr.Zero)
            {
                s_handle = Create();
                if (s_handle == IntPtr.Zero) return false;
                if (Version() != AbiVersion) { s_handle = IntPtr.Zero; return false; }
            }
            var b = Encoding.UTF8.GetBytes(allText);
            s_ready = LoadRulesNative(s_handle, b, b.Length) == 1;
            return s_ready;
        }
        catch { s_ready = false; return false; }
    }

    public bool ShouldBlock(string url, string pageHost, string resourceType)
    {
        try
        {
            if (!s_ready || s_handle == IntPtr.Zero) return false;
            if (resourceType is "document" or "mainframe") return false;
            var ub = Encoding.UTF8.GetBytes(url);
            var pb = Encoding.UTF8.GetBytes(pageHost ?? "");
            return ShouldBlockNative(s_handle, ub, ub.Length, pb, pb.Length, TypeCode(resourceType)) == 1;
        }
        catch { return false; } // fail open: never break a page on matcher error
    }

    /// <summary>Element-hide CSS for a page URL, or "" when none. Never throws.</summary>
    public static string GetCosmeticCss(string pageUrl)
    {
        try
        {
            if (!s_ready || s_handle == IntPtr.Zero) return "";
            var b = Encoding.UTF8.GetBytes(pageUrl);
            var p = CosmeticCssNative(s_handle, b, b.Length);
            if (p == IntPtr.Zero) return "";
            var s = Marshal.PtrToStringUTF8(p) ?? "";
            FreeString(p);
            return s;
        }
        catch { return ""; }
    }
}
