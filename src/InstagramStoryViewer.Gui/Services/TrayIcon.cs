using System.Windows;
using WinForms = System.Windows.Forms;

namespace InstagramStoryViewer.Gui.Services;

/// <summary>Notification-area icon: reopens the window, toggles auto-watch, exits, and shows notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _autoWatchItem;
    private bool _syncing;

    public event Action? OpenRequested;
    public event Action? RefreshRequested;
    public event Action<bool>? AutoWatchToggled;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip { RightToLeft = WinForms.RightToLeft.Yes };
        menu.Items.Add("فتح البرنامج", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("تحديث الآن", null, (_, _) => RefreshRequested?.Invoke());
        _autoWatchItem = new WinForms.ToolStripMenuItem("المشاهدة التلقائية") { CheckOnClick = true };
        _autoWatchItem.CheckedChanged += (_, _) =>
        {
            if (!_syncing) AutoWatchToggled?.Invoke(_autoWatchItem.Checked);
        };
        menu.Items.Add(_autoWatchItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("خروج", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Text = "IG Story Viewer Pro",
            Icon = LoadAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Keeps the menu's check mark in sync with the setting (without raising the event).</summary>
    public void SetAutoWatch(bool on)
    {
        _syncing = true;
        _autoWatchItem.Checked = on;
        _syncing = false;
    }

    /// <summary>Windows notification (a toast on Windows 10/11).</summary>
    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.None);

    private static System.Drawing.Icon LoadAppIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        return info is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(info.Stream);
    }

    public void Dispose()
    {
        _icon.Visible = false; // otherwise a ghost icon stays until the mouse passes over it
        _icon.Dispose();
    }
}
