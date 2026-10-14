using System.Net;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Models;
using InstagramStoryViewer.Services;

namespace InstagramStoryViewer.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "igsv-tests-" + Guid.NewGuid().ToString("N"));

    public DownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HttpResponseMessage Bytes(int n) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[n]) };

    [Fact]
    public async Task Downloads_to_final_path_without_leftovers()
    {
        var handler = new FakeHandler(_ => Bytes(1000));
        var path = Path.Combine(_dir, "a.jpg");
        await TestClient.Create(handler).DownloadAsync("https://cdn.example/a.jpg", path);

        Assert.Equal(1000, new FileInfo(path).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task Concurrent_downloads_of_the_same_file_share_one_request()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHandler(_ =>
        {
            gate.Task.Wait();
            return Bytes(10);
        });
        var client = TestClient.Create(handler);
        var path = Path.Combine(_dir, "v.mp4");

        // Many simultaneous callers make the GetOrAdd race (factory running twice) likely to show up.
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => client.DownloadAsync("https://cdn.example/v.mp4", path)))
            .ToList();
        await Task.Delay(200);
        gate.SetResult();
        await Task.WhenAll(callers);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_caller_joining_a_running_download_still_gets_progress()
    {
        var gate = new TaskCompletionSource();
        var handler = new FakeHandler(_ =>
        {
            gate.Task.Wait();
            return Bytes(10);
        });
        var client = TestClient.Create(handler);
        var path = Path.Combine(_dir, "joined.mp4");

        var first = Task.Run(() => client.DownloadAsync("https://cdn.example/joined.mp4", path)); // no progress
        await Task.Delay(100);
        var reports = new List<double>();
        var second = client.DownloadAsync("https://cdn.example/joined.mp4", path, progress: new SyncProgress(v => { lock (reports) reports.Add(v); }));
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(handler.Requests);
        Assert.Equal(1.0, reports[^1]);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public async Task Expired_cdn_link_is_reported_as_media_expired()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<InstagramApiException>(() =>
            TestClient.Create(handler).DownloadAsync("https://cdn.example/x.jpg", Path.Combine(_dir, "x.jpg")));
        Assert.Equal(InstagramErrorKind.MediaExpired, ex.Kind);
    }

    [Fact]
    public async Task Truncated_download_is_retried_and_never_saved()
    {
        var handler = new FakeHandler(_ =>
        {
            var res = Bytes(10);
            res.Content.Headers.ContentLength = 100; // server promised more than it sent
            return res;
        });
        var path = Path.Combine(_dir, "t.jpg");
        await Assert.ThrowsAnyAsync<Exception>(() => TestClient.Create(handler).DownloadAsync("https://cdn.example/t.jpg", path));

        Assert.True(handler.Requests.Count > 1); // retried
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    private static StoryItem Item(string pk, long takenAt) => new() { Pk = pk, TakenAt = takenAt, MediaType = 1 };

    [Fact]
    public void File_names_use_utc_so_they_do_not_depend_on_time_zone()
    {
        var downloader = new StoryDownloader(TestClient.Create(new FakeHandler(_ => Bytes(1))), _dir);
        var path = downloader.GetPath("bob", Item("777", 1_700_000_000)); // 2023-11-14 22:13:20 UTC
        Assert.EndsWith(Path.Combine("bob", "bob_20231114_221320Z_777.jpg"), path);
    }

    [Fact]
    public async Task Existing_download_is_found_by_media_id_even_with_an_old_name()
    {
        var handler = new FakeHandler(_ => Bytes(1));
        var downloader = new StoryDownloader(TestClient.Create(handler), _dir);
        Directory.CreateDirectory(Path.Combine(_dir, "bob"));
        var legacy = Path.Combine(_dir, "bob", "bob_20231115_011320_777.jpg"); // local-time name from older versions
        await File.WriteAllTextAsync(legacy, "x");

        Assert.Equal(legacy, downloader.FindExistingPath("bob", Item("777", 1_700_000_000)));
        Assert.Equal(legacy, await downloader.DownloadAsync("bob", Item("777", 1_700_000_000)));
        Assert.Empty(handler.Requests); // nothing re-downloaded
    }
}
