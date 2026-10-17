using System.IO;
using System.Text.Json;
using Cinemana.Desktop.Models;

namespace Cinemana.Desktop.Services;

public sealed class LibraryStore
{
    public string Root { get; }
    public string? LoadWarning { get; private set; }
    private readonly object gate = new();
    public LibraryStore(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cinemana.Desktop");
        Directory.CreateDirectory(Root);
    }
    public T Load<T>(string name, T fallback)
    {
        lock (gate)
        {
            string path = Path.Combine(Root, name + ".json");
            if (!File.Exists(path)) return fallback;
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            { LoadWarning = "تعذّر استرجاع بعض البيانات المحفوظة."; return fallback; }
        }
    }
    public void Save<T>(string name, T value)
    {
        lock (gate)
        {
            string path = Path.Combine(Root, name + ".json");
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, true);
        }
    }
    public List<Movie> Favorites() => Load("favorites", new List<Movie>());
    public List<WatchEntry> History() => Load("history", new List<WatchEntry>());
    public void RemoveProgress(string id)
    {
        lock (gate) { var entries = History(); entries.RemoveAll(e => e.Movie.Id == id); Save("history", entries); }
    }
    public void SaveProgress(Movie movie, double position, double duration)
    {
        if (!double.IsFinite(position) || !double.IsFinite(duration) || position < 0 || duration <= 0 || position > duration + 2) return;
        lock (gate)
        {
            var entries = History();
            entries.RemoveAll(e => e.Movie.Id == movie.Id);
            entries.Insert(0, new(movie, DateTimeOffset.UtcNow, position, duration));
            Save("history", entries.Take(150).ToList());
        }
    }
}
