namespace IconPicker.Desktop;

internal static class DesktopPaths
{
    public static string Home { get; } = ResolveHome();
    public static string Runtime => Path.Combine(Home, "runtime");
    public static string Data => Path.Combine(Home, "data");
    public static string Logs => Path.Combine(Home, "logs");
    public static string LogFile => Path.Combine(Logs, "desktop.log");

    private static string ResolveHome()
    {
        var custom = Environment.GetEnvironmentVariable("ICON_PICKER_HOME");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;

        if (OperatingSystem.IsMacOS())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "IconPicker");

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IconPicker");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : xdg;
        return Path.Combine(root, "IconPicker");
    }
}
