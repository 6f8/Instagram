using InstagramStoryViewer.Diagnostics;

namespace InstagramStoryViewer.Tests;

[Collection("AppLog")] // AppLog is static: keep these tests sequential
public sealed class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igsv-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        AppLog.Initialize(Path.Combine(Path.GetTempPath(), "igsv-log-null")); // detach from _dir
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_daily_file_with_secrets_masked()
    {
        var now = new DateTime(2026, 10, 13, 9, 30, 0);
        AppLog.Initialize(_dir, () => now);
        AppLog.Info("login with sessionid=SECRET123");

        var text = File.ReadAllText(Path.Combine(_dir, "2026-10-13.log"));
        Assert.Contains("[INFO]", text);
        Assert.Contains("sessionid=***", text);
        Assert.DoesNotContain("SECRET123", text);
    }

    [Fact]
    public void Removes_logs_older_than_retention()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "2026-09-01.log"), "old");
        File.WriteAllText(Path.Combine(_dir, "2026-10-10.log"), "recent");

        AppLog.Initialize(_dir, () => new DateTime(2026, 10, 13));

        Assert.False(File.Exists(Path.Combine(_dir, "2026-09-01.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "2026-10-10.log")));
    }
}
