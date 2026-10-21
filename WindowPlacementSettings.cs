using System.IO;
using System.Security;
using System.Text.Json;

namespace JungleLeopardPoc;

internal sealed record SavedWindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

internal static class WindowPlacementSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JungleLeopardStudio",
        "window-placement.json");

    internal static SavedWindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            SavedWindowPlacement? placement = JsonSerializer.Deserialize<SavedWindowPlacement>(File.ReadAllText(SettingsPath));
            return placement is { Width: > 0, Height: > 0 }
                && double.IsFinite(placement.Left) && double.IsFinite(placement.Top)
                && double.IsFinite(placement.Width) && double.IsFinite(placement.Height)
                ? placement : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            return null;
        }
    }

    internal static void Save(SavedWindowPlacement placement)
    {
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (directory is null) return;
        string temporaryPath = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(placement));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException or ArgumentException)
        {
            // Window placement is optional; keep the app usable if the profile cannot be written.
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { }
        }
    }
}
