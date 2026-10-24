using Avalonia;

namespace IconPicker.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions
            {
                DisableNativeMenus = true,
                DisableSetProcessName = true,
            })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
}
