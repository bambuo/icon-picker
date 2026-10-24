using System.Runtime.InteropServices;

namespace IconPicker.Desktop;

internal static class AppIcon
{
    public static void ApplyMacIdentity()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = Send(GetClass("NSApplication"), Sel("sharedApplication"));

            var namePtr = Marshal.StringToCoTaskMemUTF8("图标筛选工作台");
            try
            {
                var nsName = Send(GetClass("NSString"), Sel("stringWithUTF8String:"), namePtr);
                var processInfo = Send(GetClass("NSProcessInfo"), Sel("processInfo"));
                Send(processInfo, Sel("setProcessName:"), nsName);

                var mainMenu = Send(app, Sel("mainMenu"));
                if (mainMenu != IntPtr.Zero)
                {
                    var appItem = Send(mainMenu, Sel("itemAtIndex:"), IntPtr.Zero);
                    if (appItem != IntPtr.Zero) Send(appItem, Sel("setTitle:"), nsName);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(namePtr);
            }

            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AppIcon.png");
            if (stream is null) return;

            var path = Path.Combine(Path.GetTempPath(), "IconPicker-AppIcon.png");
            using (var file = File.Create(path)) stream.CopyTo(file);

            var pathPtr = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                var nsPath = Send(GetClass("NSString"), Sel("stringWithUTF8String:"), pathPtr);
                var image = Send(Send(GetClass("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), nsPath);
                Send(app, Sel("setApplicationIconImage:"), image);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
        catch
        {
            // 图标设置失败不影响启动
        }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
}
