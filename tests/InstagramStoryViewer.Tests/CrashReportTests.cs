using InstagramStoryViewer.Diagnostics;

namespace InstagramStoryViewer.Tests;

public sealed class CrashReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igsv-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Writes_a_sanitized_report()
    {
        var ex = new InvalidOperationException("failed with sessionid=12345%3Asecret%3A1 in the header");
        var path = CrashReport.Write(ex, "test", _dir, new DateTime(2026, 10, 13, 9, 30, 0));
        Assert.NotNull(path);
        Assert.EndsWith("crash-20261013-093000.txt", path);
        var text = File.ReadAllText(path);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("Source: test", text);
        Assert.DoesNotContain("secret", text);
    }
}
