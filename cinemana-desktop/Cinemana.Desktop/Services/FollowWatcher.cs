using System.Net.Http;
using Cinemana.Desktop.Models;

namespace Cinemana.Desktop.Services;

/// <summary>
/// Followed series: at start and every six hours their episode lists are compared with the episodes seen at
/// the last check; new ones are queued (best quality, Arabic subtitles) when auto-download is on.
/// </summary>
public sealed class FollowWatcher(LibraryStore store, Func<IEnumerable<DownloadSpec>, int> enqueue, Func<string, string> folderFor)
{
    public sealed record Followed(string Id, string Title, string DisplayTitle, List<string> Known, string Poster = "");
    public List<Followed> All() => store.Load("followed", new List<Followed>());
    public bool IsFollowed(string id) => All().Any(f => f.Id == id);
    public void Toggle(Movie series, IEnumerable<Movie> episodes)
    {
        var list = All();
        if (list.RemoveAll(f => f.Id == series.Id) == 0) list.Add(new(series.Id, series.Title, series.DisplayTitle, episodes.Select(e => e.Id).ToList(), series.Poster));
        store.Save("followed", list);
    }
    /// <summary>Returns a line per series with new episodes, e.g. "رمال الشمال: 2 حلقة جديدة".</summary>
    public async Task<List<string>> Check(bool download, CancellationToken ct)
    {
        var news = new List<string>();
        var list = All();
        using var api = new CinemanaService(store.Root);
        for (int i = 0; i < list.Count; i++)
        {
            var f = list[i];
            try
            {
                var episodes = await api.Episodes(f.Id, ct);
                var fresh = episodes.Where(e => !f.Known.Contains(e.Id)).ToList();
                if (fresh.Count == 0) continue;
                news.Add($"{f.DisplayTitle}: {fresh.Count} حلقة جديدة");
                if (download)
                {
                    var planner = new DownloadPlanner((m, t) => api.Playback(m.Id, t));
                    var plan = await planner.Build(fresh, f.Title, f.Id, folderFor(f.Title), -2, "ar", null, ct);
                    enqueue(plan.Files);
                }
                list[i] = f with { Known = episodes.Select(e => e.Id).ToList() };
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or System.IO.InvalidDataException)
            { Log.Error("follow", e); }
        }
        store.Save("followed", list);
        if (news.Count > 0) Log.Info($"follow: {news.Count} series with new episodes");
        return news;
    }
}
