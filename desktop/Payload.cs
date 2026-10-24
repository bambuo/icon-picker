using System.IO.Compression;
using System.Reflection;

namespace IconPicker.Desktop;

internal static class Payload
{
    public static void Extract()
    {
        Directory.CreateDirectory(DesktopPaths.Runtime);

        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("desktop-payload.zip", StringComparison.Ordinal));
        if (name is not null)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            zip.ExtractToDirectory(DesktopPaths.Runtime, overwriteFiles: true);
            return;
        }

        var besideExe = Path.Combine(AppContext.BaseDirectory, "desktop-payload.zip");
        if (File.Exists(besideExe))
        {
            ZipFile.ExtractToDirectory(besideExe, DesktopPaths.Runtime, overwriteFiles: true);
            return;
        }

        throw new FileNotFoundException("安装包不完整：缺少 desktop-payload.zip");
    }
}
