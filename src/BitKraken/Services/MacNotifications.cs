using System.Runtime.InteropServices;
using System.Text;

namespace BitKraken.Services;

/// <summary>
/// Posts a macOS notification from BitKraken's own process, through AppKit's notification centre.
/// </summary>
/// <remarks>
/// This exists because of what macOS decides an icon from: the bundle of the process that posted the
/// notification, and nothing else. Every earlier attempt here posted from some other process -
/// <c>osascript</c>, whose bundle is Script Editor's, and then a helper applet inside
/// <c>Contents/Helpers</c>, whose bundle LaunchServices never registers properly and which therefore
/// draws the generic applet icon however correct the bundle on disk is.
/// <para>
/// BitKraken's own executable lives at <c>BitKraken.app/Contents/MacOS/BitKraken</c>, so this process's
/// main bundle is BitKraken.app itself - a real, installed, registered application whose icon macOS
/// already draws correctly everywhere else. Posting from here means there is no second bundle to
/// register, no second entry in System Settings, no separate icon cache to go stale, and no subprocess.
/// The icon is the one on the Dock because it is the same bundle.
/// </para>
/// <para>
/// <c>NSUserNotification</c> has been deprecated since 10.14 and may simply be gone on a new enough
/// macOS. Every step is therefore checked rather than assumed, and a false return sends the caller on
/// to the older routes rather than costing the notification.
/// </para>
/// </remarks>
internal static class MacNotifications
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string System = "/usr/lib/libSystem.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    /// <summary>RTLD_LAZY | RTLD_GLOBAL - the classes only have to become resolvable.</summary>
    private const int LoadGlobal = 0x1 | 0x8;

    [DllImport(System, EntryPoint = "dlopen")]
    private static extern IntPtr LoadLibrary(string path, int mode);

    [DllImport(Objc, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, byte[] utf8);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint SendCount(IntPtr receiver, IntPtr selector);

    /// <summary>Loaded once; Foundation is where NSUserNotification lives.</summary>
    private static readonly Lazy<bool> FoundationLoaded =
        new(() => LoadLibrary(Foundation, LoadGlobal) != IntPtr.Zero, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Delivers one notification as BitKraken.app. False means this route is not available - an older
    /// or newer macOS, or a build running outside the bundle - and the caller should try another.
    /// </summary>
    internal static bool TryPost(string title, string body)
    {
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            return Post(title, body);
        }
        catch (Exception)
        {
            // A missing dylib, a class that no longer exists, a selector that no longer answers. None
            // of it is worth an exception: there are two further routes behind this one.
            return false;
        }
    }

    private static bool Post(string title, string body)
    {
        if (!FoundationLoaded.Value) return false;

        var notificationClass = GetClass("NSUserNotification");
        var centreClass = GetClass("NSUserNotificationCenter");
        if (notificationClass == IntPtr.Zero || centreClass == IntPtr.Zero) return false;

        // Nil for a process that is not inside an app bundle, which is exactly when we must not go on.
        var centre = Send(centreClass, GetSelector("defaultUserNotificationCenter"));
        if (centre == IntPtr.Zero) return false;

        var notification = Send(Send(notificationClass, GetSelector("alloc")), GetSelector("init"));
        if (notification == IntPtr.Zero) return false;

        try
        {
            Send(notification, GetSelector("setTitle:"), NewString(title));
            Send(notification, GetSelector("setInformativeText:"), NewString(body));
            Send(centre, GetSelector("deliverNotification:"), notification);

            // Delivered means macOS took it, which is the only part of "did it work" observable from
            // in here - what it then draws is between the notification centre and the user's settings.
            return DeliveredCount(centre) > 0;
        }
        finally
        {
            Send(notification, GetSelector("release"));
        }
    }

    /// <summary>How many of our notifications macOS is currently holding. Zero means it took none.</summary>
    private static nint DeliveredCount(IntPtr centre)
    {
        var delivered = Send(centre, GetSelector("deliveredNotifications"));
        return delivered == IntPtr.Zero ? 0 : SendCount(delivered, GetSelector("count"));
    }

    /// <summary>An autoreleased NSString. UTF-8 throughout, so a torrent named in any script is fine.</summary>
    private static IntPtr NewString(string value)
    {
        var utf8 = new byte[Encoding.UTF8.GetByteCount(value ?? "") + 1];
        Encoding.UTF8.GetBytes(value ?? "", 0, (value ?? "").Length, utf8, 0);

        return SendString(GetClass("NSString"), GetSelector("stringWithUTF8String:"), utf8);
    }
}
