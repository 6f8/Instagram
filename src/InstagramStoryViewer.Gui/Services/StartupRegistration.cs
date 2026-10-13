using Microsoft.Win32;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "InstagramStoryViewer";
    public const string MinimizedArg = "--minimized";

    /// <summary>Adds or removes the entry. Re-adding also fixes the path if the exe was moved.</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" {MinimizedArg}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
