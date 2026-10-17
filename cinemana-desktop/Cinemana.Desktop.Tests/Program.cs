using System.IO;
using System.Text.Json;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;
using Cinemana.Desktop.ViewModels;

// Fast checks of the parts that break silently: service parsing, subtitles, file names, filters.
// Run: dotnet run --project Cinemana.Desktop.Tests -c Release
int failed = 0, passed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) passed++; else { failed++; Console.Error.WriteLine($"FAIL {name} {detail}"); }
}
string temp = Path.Combine(Path.GetTempPath(), "CinemanaTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);

// --- Movie and playback parsing (shapes the Android API returns) ---
var details = JsonDocument.Parse("""
{"nb":"123","en_title":"North Sands","ar_title":"رمال الشمال","kind":"2","season":"1","episodeNummer":"4","year":"2024","stars":"8.1","duration":"2700",
 "categories":[{"ar_title":"مغامرة"},{"ar_title":"دراما"}],
 "translations":[{"type":"ar","name":"Arabic","extention":"vtt","file":"https://cnth2.shabakaty.com/t/ar.vtt"},
                 {"type":"ar","name":"Arabic","extention":"srt","file":"https://cnth2.shabakaty.com/t/ar.srt"},
                 {"type":"en","name":"English","extention":"srt","file":"https://evil.example.com/en.srt"}]}
""").RootElement;
var movie = Movie.Parse(details);
Check("movie.id", movie.Id == "123");
Check("movie.episode", movie.Kind == "2" && movie.Season == 1 && movie.Episode == 4);
Check("movie.genres", movie.FirstGenre == "مغامرة");
Check("movie.meta", movie.MetaLine == "2024 • مغامرة • 45 د", movie.MetaLine);
var streams = JsonDocument.Parse("""
[{"videoUrl":"https://cnth2.shabakaty.com/v/720.mp4?sig=1","container":"mp4","resolution":"720p"},
 {"videoUrl":"https://cnth2.shabakaty.com/v/1080.mp4?sig=1","container":"mp4","resolution":"1080p"},
 {"videoUrl":"https://cnth2.shabakaty.com/v/hls.m3u8","container":"m3u8","resolution":"1080p"},
 {"videoUrl":"https://evil.example.com/v.mp4","container":"mp4","resolution":"480p"}]
""").RootElement;
var playback = PlaybackInfo.Parse(streams, details);
Check("playback.sources", playback.Sources.Select(s => s.Height).SequenceEqual([720, 1080]), string.Join(",", playback.Sources.Select(s => s.Height)));
Check("playback.foreign-host-rejected", playback.Sources.All(s => PlaybackInfo.IsServiceUrl(s.Url)));
Check("playback.subtitle-prefers-srt", playback.Subtitles.Single(t => t.Language == "ar").Url.EndsWith("ar.srt"));
Check("playback.subtitle-foreign-host-rejected", playback.Subtitles.All(t => t.Language != "en"));

// --- Subtitles ---
var cues = SubtitleParser.Parse("WEBVTT\n\n00:01.500 --> 00:03.000\n<i>مرحبا</i>\n\n2\n00:00:04.000 --> 00:00:05.250 align:start\nhi\nthere\n");
Check("subs.count", cues.Count == 2, cues.Count.ToString());
Check("subs.tags-stripped", cues[0].Text == "مرحبا");
string srt = SubtitleParser.ToSrt(cues);
Check("subs.srt-format", srt.StartsWith("1\r\n00:00:01,500 --> 00:00:03,000\r\nمرحبا\r\n\r\n2\r\n00:00:04,000 --> 00:00:05,250\r\nhi\r\nthere"), srt);
Check("subs.roundtrip", SubtitleParser.Parse(srt).Select(c => c.Text).SequenceEqual(cues.Select(c => c.Text)));
Check("subs.at", SubtitleParser.At(cues, 2) == "مرحبا" && SubtitleParser.At(cues, 3.5) == "");

// --- File names ---
var mp4 = new VideoSource("1080p", "https://cnth2.shabakaty.com/v.mp4", 1080);
string episodePath = DownloadPaths.FilePath(temp, "رمال: الشمال", "1", movie, mp4);
Check("names.episode", episodePath == Path.Combine(temp, "مسلسلات", "رمال_ الشمال", "الموسم 01", "رمال_ الشمال - S01E04.mp4"), episodePath);
var film = new Movie("9", "The Traveler", "", "", "2023", "7") { Kind = "1" };
string filmPath = DownloadPaths.FilePath(temp, "المسافر", "9", film, mp4);
Check("names.film", filmPath == Path.Combine(temp, "أفلام", "المسافر (2023)", "المسافر (2023).mp4"), filmPath);
Check("names.with-quality", DownloadPaths.FilePath(temp, "المسافر", "9", film, mp4, withQuality: true).EndsWith("المسافر (2023) [1080p].mp4"));
Check("names.reserved", DownloadPaths.SafeName("CON") == "_CON");
Check("names.subtitle", FileDownloader.SubtitlePath(filmPath, "ar").EndsWith("المسافر (2023).ar.srt"));
Directory.CreateDirectory(Path.GetDirectoryName(filmPath)!);
File.WriteAllText(FileDownloader.SubtitlePath(filmPath, "en"), "x");
File.WriteAllText(FileDownloader.SubtitlePath(filmPath, "ar"), "x");
File.WriteAllText(Path.Combine(Path.GetDirectoryName(filmPath)!, "Other.srt"), "x");
var beside = FileDownloader.SubtitlesBeside(filmPath).Select(Path.GetFileName).ToList();
Check("names.subtitles-beside", beside.Count == 2 && beside[0]!.Contains(".ar."), string.Join(",", beside));

// --- Library discovery reads the downloader's side file ---
string found = Path.Combine(temp, "أفلام", "Found (2020)", "Found (2020).mp4");
Directory.CreateDirectory(Path.GetDirectoryName(found)!);
File.WriteAllBytes(found, new byte[10]);
File.WriteAllText(found + ".download.json", JsonSerializer.Serialize(new DownloadMetadata("77", 720, "https://x/y.mp4", 10, null, null, true)));
var discovered = LibraryScanner.Scan(temp, []).ToList();
Check("scan.found", discovered.Any(d => d.Spec.Movie.Id == "77" && d.Spec.Quality == 720 && d.State == DownloadState.Completed), discovered.Count.ToString());
Check("scan.skips-known", !LibraryScanner.Scan(temp, [found]).Any(d => d.Spec.Destination == found));

// --- Filters ---
var list = new[] { film with { Rating = "8", Genres = "دراما" }, movie with { Rating = "6" } };
Check("filter.rating", MainViewModel.Filter(list, "الكل", "الكل", 7, 0).Count() == 1);
Check("filter.genre", MainViewModel.Filter(list, "مغامرة", "الكل", 0, 0).Single().Id == "123");

try { Directory.Delete(temp, true); } catch (IOException) { }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
