using System.IO;
using Microsoft.Win32;

namespace JungleLeopardPoc;

internal static class StudioFileDialogDefaults
{
    public static void Configure(Microsoft.Win32.FileDialog dialog, string? preferredPath = null)
    {
        string? directory = TryResolveDirectory(preferredPath);
        if (directory is null)
        {
            Environment.SpecialFolder[] fallbackFolders = dialog is Microsoft.Win32.SaveFileDialog
                ? [Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures]
                : [Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory];
            foreach (Environment.SpecialFolder folder in fallbackFolders)
            {
                string candidate = Environment.GetFolderPath(folder);
                if (Directory.Exists(candidate)) { directory = candidate; break; }
            }
        }
        if (directory is null)
        {
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? Environment.SystemDirectory;
            directory = Directory.Exists(systemRoot) ? systemRoot : Environment.SystemDirectory;
        }
        dialog.InitialDirectory = directory;
    }

    private static string? TryResolveDirectory(string? preferredPath)
    {
        if (string.IsNullOrWhiteSpace(preferredPath)) return null;
        try
        {
            if (Directory.Exists(preferredPath)) return Path.GetFullPath(preferredPath);
            string? directory = Path.GetDirectoryName(Path.GetFullPath(preferredPath));
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
