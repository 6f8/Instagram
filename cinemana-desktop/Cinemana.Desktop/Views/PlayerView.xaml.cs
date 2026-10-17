using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;
using SubtitleTrack = Cinemana.Desktop.Models.SubtitleTrack;

namespace Cinemana.Desktop.Views;

// Plays through WPF MediaElement (Windows Media Foundation), so the app ships no video engine of its own.
// Media Foundation plays MP4 (H.264/AAC) and, on Windows 10/11, MKV; HLS playlists are not offered.
public partial class PlayerView : UserControl, IDisposable
{
    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
    public sealed record Match(string Stamp, string Text, double Start);
    private double PositionSeconds => Video.Position.TotalSeconds;
    private readonly LibraryStore store;
    private readonly CinemanaService service;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? request, subtitleRequest, secondRequest;
    private List<SubtitleCue> cues = [], secondCues = [];
    private List<Movie> episodes = [];
    private Movie movie;
    private string? localFile;
    private Uri? currentSource;
    private bool disposed, ready, playing, changingControls, dragging, updatingSeek, timeoutShown, controlsHidden, sleepAtEnd;
    private double pendingPosition, savedPosition, mediaDuration, subtitleOffset, introEnd;
    private int countdownLeft;
    private DateTimeOffset lastSaved = DateTimeOffset.MinValue, openedAt, lastMouse = DateTimeOffset.UtcNow;
    private DateTimeOffset? sleepAt;
    private Movie? nextEpisode;
    private (string Id, Task<PlaybackInfo> Info)? prefetched;
    public event Action? CloseRequested;
    public event Action? FullscreenRequested;
    public event Action? EscapeRequested;
    public event Action? PipRequested;
    public event Action<Movie>? DownloadRequested;

    public PlayerView(Movie movie, LibraryStore store)
    {
        this.movie = movie; this.store = store; service = new(store.Root);
        InitializeComponent();
        var s = Settings;
        changingControls = true;
        Speed.ItemsSource = Speeds.Select(x => x.ToString("0.##") + "×").ToList();
        int speedIndex = Array.IndexOf(Speeds, s.PlaybackSpeed);
        Speed.SelectedIndex = speedIndex >= 0 ? speedIndex : 2;
        SubtitleSize.Value = Math.Clamp(s.SubtitleSize, SubtitleSize.Minimum, SubtitleSize.Maximum);
        SubtitleFont.SelectedItem = SubtitleFont.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == s.SubtitleFont) ?? SubtitleFont.Items[0];
        SubtitleColor.SelectedItem = SubtitleColor.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.SubtitleColor) ?? SubtitleColor.Items[0];
        SubtitleBackground.Value = s.SubtitleBackground;
        AutoNextBox.IsChecked = s.AutoNext;
        VolumeBar.Value = s.Volume; Video.Volume = s.Volume; Video.IsMuted = s.Muted;
        MuteButton.Content = s.Muted ? "إلغاء الكتم" : "الصوت";
        changingControls = false;
        ApplySubtitleStyle(); PlaceSubtitles(s.SubtitleTop);
        timer.Tick += Tick; timer.Start();
        countdown.Tick += CountdownTick;
    }

    private AppSettings Settings => store.Load("settings", new AppSettings());
    private void SaveSettings(Func<AppSettings, AppSettings> change)
    {
        try { store.Save("settings", change(Settings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("player-settings", e); }
    }
    private double SelectedSpeed => Speed.SelectedIndex >= 0 ? Speeds[Speed.SelectedIndex] : 1;
    // Intro length is kept per series (episodes share it); subtitle timing per episode.
    private string SeriesKey => movie.Kind == "2" ? "s:" + movie.Title : "m:" + movie.Id;

    private void Reset(Movie target)
    {
        SaveCurrent();
        request?.Cancel(); request?.Dispose(); request = new();
        subtitleRequest?.Cancel(); secondRequest?.Cancel(); StopCountdown();
        ready = playing = false; Video.Stop(); Video.Source = null;
        movie = target; TitleLabel.Text = target.DisplayTitle + (target.Kind == "2" ? " • " + target.EpisodeLabel : "");
        TitleLabel.ToolTip = target.Title;
        LoadingLabel.Text = "جارٍ تجهيز الفيديو…"; LoadingPanel.Visibility = Visibility.Visible;
        PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        SubtitleBox.Visibility = SecondBox.Visibility = SkipIntroButton.Visibility = Visibility.Collapsed;
        cues = []; secondCues = []; episodes = []; nextEpisode = null;
        NextEpisodeButton.Visibility = Visibility.Collapsed;
        CurrentTime.Text = TotalTime.Text = "00:00:00";
        mediaDuration = 0; savedPosition = 0; pendingPosition = 0;
        subtitleOffset = store.Load("subtitle-offsets", new Dictionary<string, double>()).GetValueOrDefault(target.Id); UpdateOffsetLabel();
        introEnd = store.Load("intros", new Dictionary<string, double>()).GetValueOrDefault(SeriesKey);
        var old = store.History().FirstOrDefault(e => e.Movie.Id == target.Id);
        if (old is { Position: > 0 } && old.Duration - old.Position > 10) savedPosition = old.Position;
        ResumeButton.IsEnabled = false;
        ResumeButton.Content = savedPosition > 0 ? "استئناف " + Stamp(savedPosition) : "لا يوجد موضع محفوظ";
        changingControls = true; Quality.ItemsSource = null; Subtitles.ItemsSource = null; SecondSubtitles.ItemsSource = null; changingControls = false;
        if (sleepAtEnd) Notice.Text = "مؤقت النوم: سيتوقف التشغيل في نهاية الحلقة.";
    }

    public async Task Open(Movie target)
    {
        if (disposed) return;
        Reset(target); localFile = null; DownloadButton.Visibility = Visibility.Visible;
        var token = request!.Token;
        Notice.Text = "جارٍ جلب مصدر الفيديو…";
        try
        {
            // The next episode's sources may already have been fetched near the end of the previous one.
            var playbackTask = prefetched is { } p && p.Id == target.Id ? p.Info : service.Playback(target.Id, token);
            prefetched = null;
            var playback = await playbackTask;
            token.ThrowIfCancellationRequested();
            movie = playback.Movie with { LocalPoster = target.LocalPoster };
            introEnd = store.Load("intros", new Dictionary<string, double>()).GetValueOrDefault(SeriesKey);
            var playable = playback.Sources.Where(s => s.Container is "mp4" or "mkv").ToList();
            if (playable.Count == 0) throw new InvalidDataException("لا تتوفر نسخة MP4 قابلة للتشغيل لهذا العنوان. يمكنك تنزيله إن توفّر ملف مباشر.");
            changingControls = true;
            Quality.ItemsSource = playable;
            Quality.SelectedItem = playable.LastOrDefault(s => s.Height <= 720 && s.Container == "mp4") ?? playable.LastOrDefault(s => s.Height <= 720) ?? playable[0];
            SetTracks(playback.Subtitles, playback.Subtitles.FirstOrDefault(s => s.Language == "ar"));
            changingControls = false;
            pendingPosition = savedPosition;
            playing = true;
            OpenSource((VideoSource)Quality.SelectedItem);
            _ = LoadSubtitle(token);
            if (movie.Kind == "2") _ = LoadEpisodes(target.Id, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        {
            if (disposed || token.IsCancellationRequested) return;
            Log.Error("play", e);
            changingControls = false;
            LoadingPanel.Visibility = Visibility.Collapsed;
            Notice.Text = e switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "الخدمة لا تسمح بتشغيل هذا العنوان على الاتصال الحالي.",
                InvalidDataException => e.Message,
                _ => "تعذّر جلب الفيديو. تحقق من الشبكة ثم اضغط إعادة المحاولة."
            };
        }
    }

    /// <summary>Plays a downloaded file offline, with the .srt saved beside it when present.</summary>
    public void OpenLocal(Movie target, string file)
    {
        if (disposed) return;
        Reset(target); localFile = file; DownloadButton.Visibility = Visibility.Collapsed;
        var source = new VideoSource("ملف محلي", new Uri(file).AbsoluteUri, 0) { Container = Path.GetExtension(file).TrimStart('.').ToLowerInvariant() };
        var tracks = new List<SubtitleTrack> { new("بدون ترجمة", "", "") };
        foreach (var srt in FileDownloader.SubtitlesBeside(file))
            tracks.Add(new(Path.GetFileName(srt).Contains(".en.", StringComparison.OrdinalIgnoreCase) ? "English (محفوظة)" : "الترجمة المحفوظة", srt, "local"));
        changingControls = true;
        Quality.ItemsSource = new List<VideoSource> { source }; Quality.SelectedIndex = 0;
        SetTracks(tracks, tracks.Count > 1 ? tracks[1] : null);
        changingControls = false;
        pendingPosition = savedPosition; playing = true;
        OpenSource(source);
        _ = LoadSubtitle(request!.Token);
    }
    private void SetTracks(List<SubtitleTrack> tracks, SubtitleTrack? selected)
    {
        Subtitles.ItemsSource = tracks; Subtitles.SelectedItem = selected ?? tracks[0];
        SecondSubtitles.ItemsSource = tracks.ToList(); SecondSubtitles.SelectedIndex = 0;
    }

    private void OpenSource(VideoSource source)
    {
        ready = false; PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        openedAt = DateTimeOffset.UtcNow; timeoutShown = false;
        LoadingLabel.Text = "جارٍ تحميل الفيديو…"; LoadingPanel.Visibility = Visibility.Visible;
        SubtitleBox.Visibility = SecondBox.Visibility = Visibility.Collapsed;
        Notice.Text = (localFile is null ? "جودة " + source.Label : "تشغيل بدون إنترنت") + " • Space تشغيل/إيقاف • الأسهم ±10 ثوانٍ • [ ] توقيت الترجمة • P نافذة صغيرة";
        Video.Stop();
        currentSource = new Uri(source.Url);
        Video.Source = currentSource;
        Video.Play();
    }

    private void VideoOpened(object sender, RoutedEventArgs e)
    {
        if (disposed || currentSource is null) return;
        mediaDuration = Video.NaturalDuration.HasTimeSpan ? Video.NaturalDuration.TimeSpan.TotalSeconds : movie.Duration;
        ready = true; LoadingPanel.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true; SeekBar.IsEnabled = mediaDuration > 0;
        updatingSeek = true; SeekBar.Maximum = Math.Max(1, mediaDuration); updatingSeek = false;
        if (pendingPosition > 0 && mediaDuration > 0) Video.Position = TimeSpan.FromSeconds(Math.Min(pendingPosition, Math.Max(0, mediaDuration - 1)));
        pendingPosition = 0;
        Video.SpeedRatio = SelectedSpeed;
        if (playing) Video.Play(); else Video.Pause();
        ResumeButton.IsEnabled = savedPosition > 0;
        PlayButton.Content = playing ? "إيقاف مؤقت" : "تشغيل";
        TotalTime.Text = Stamp(mediaDuration);
        UpdatePosition();
    }

    private void VideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (disposed) return;
        Log.Error("media", e.ErrorException);
        ready = playing = false; currentSource = null; PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        Notice.Text = localFile is not null
            ? "تعذّر تشغيل الملف. قد يحتاج Windows إلى ملحق الترميز (HEVC/MKV) من متجر Microsoft."
            : "تعذّر تشغيل هذه النسخة. جرّب جودة أخرى أو اضغط إعادة المحاولة.";
    }

    private void VideoEnded(object sender, RoutedEventArgs e)
    {
        SaveCurrent(completed: true); playing = false; PlayButton.Content = "إعادة التشغيل"; ShowControls();
        if (sleepAtEnd) { sleepAtEnd = false; SleepTimer.SelectedIndex = 0; Notice.Text = "مؤقت النوم: انتهت الحلقة وتوقف التشغيل."; return; }
        if (nextEpisode is null) { Notice.Text = "انتهت المشاهدة."; return; }
        if (AutoNextBox.IsChecked == true) StartCountdown();
        else Notice.Text = "انتهت الحلقة. يمكنك الانتقال إلى الحلقة التالية.";
    }
    // --- Next episode countdown ---
    private void StartCountdown()
    {
        countdownLeft = 10; NextTitle.Text = nextEpisode!.EpisodeLabel + (nextEpisode.Title.Length > 0 ? " · " + nextEpisode.Title : "");
        NextCountdown.Text = $"الحلقة التالية خلال {countdownLeft} ثوانٍ";
        NextPanel.Visibility = Visibility.Visible; countdown.Start();
    }
    private void StopCountdown() { countdown.Stop(); NextPanel.Visibility = Visibility.Collapsed; }
    private async void CountdownTick(object? sender, EventArgs e)
    {
        if (--countdownLeft > 0) { NextCountdown.Text = $"الحلقة التالية خلال {countdownLeft} ثوانٍ"; return; }
        StopCountdown();
        if (nextEpisode is not null && !disposed) await Open(nextEpisode);
    }
    private void CancelNextClick(object sender, RoutedEventArgs e) { StopCountdown(); Notice.Text = "أُلغي التشغيل التلقائي للحلقة التالية."; }

    private void BufferingStarted(object sender, RoutedEventArgs e) { if (!disposed) { LoadingLabel.Text = "جارٍ التخزين المؤقت…"; LoadingPanel.Visibility = Visibility.Visible; } }
    private void BufferingEnded(object sender, RoutedEventArgs e) { if (ready) LoadingPanel.Visibility = Visibility.Collapsed; }

    private void Tick(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (!ready)
        {
            if (!timeoutShown && currentSource is not null && DateTimeOffset.UtcNow - openedAt > TimeSpan.FromSeconds(30))
            {
                timeoutShown = true; LoadingPanel.Visibility = Visibility.Collapsed;
                Notice.Text = "يستغرق الفيديو وقتًا أطول من المعتاد. جرّب جودة أقل أو إعادة المحاولة.";
            }
            return;
        }
        if (LoadingPanel.Visibility == Visibility.Visible && !Video.IsBuffering) LoadingPanel.Visibility = Visibility.Collapsed;
        UpdatePosition();
        if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(2)) SaveCurrent();
        // Controls fade out after three still seconds while playing; any mouse move brings them back.
        if (playing && !controlsHidden && DateTimeOffset.UtcNow - lastMouse > TimeSpan.FromSeconds(3) && !IsKeyboardFocusWithinControls()) HideControls();
        if (sleepAt is { } at && DateTimeOffset.UtcNow >= at) { sleepAt = null; SleepTimer.SelectedIndex = 0; if (playing) TogglePlay(); Notice.Text = "مؤقت النوم: توقف التشغيل."; ShowControls(); }
        // Fetch the next episode's sources ahead of time near the end.
        if (nextEpisode is not null && prefetched is null && mediaDuration > 0 && PositionSeconds > mediaDuration * 0.85)
            prefetched = (nextEpisode.Id, service.Playback(nextEpisode.Id, request?.Token ?? CancellationToken.None));
    }
    private bool IsKeyboardFocusWithinControls() => BottomBar.IsKeyboardFocusWithin && Keyboard.FocusedElement is ComboBox or Slider or TextBox;
    private void HideControls() { controlsHidden = true; TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed; VideoSurface.Cursor = Cursors.None; }
    private void ShowControls() { controlsHidden = false; TopBar.Visibility = BottomBar.Visibility = Visibility.Visible; VideoSurface.Cursor = null; }
    private void AnyMouseMove(object sender, MouseEventArgs e) { lastMouse = DateTimeOffset.UtcNow; if (controlsHidden) ShowControls(); }
    private static string Stamp(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
    private void UpdatePosition()
    {
        double position = PositionSeconds;
        CurrentTime.Text = Stamp(position);
        if (!dragging) { updatingSeek = true; SeekBar.Value = Math.Clamp(position, 0, SeekBar.Maximum); updatingSeek = false; }
        // A positive offset delays the subtitles; a negative one shows them earlier.
        SubtitleLabel.Text = SubtitleParser.At(cues, position - subtitleOffset);
        SubtitleBox.Visibility = SubtitleLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SecondLabel.Text = SubtitleParser.At(secondCues, position - subtitleOffset);
        SecondBox.Visibility = SecondLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipIntroButton.Visibility = introEnd > 5 && position > 1 && position < introEnd - 2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveCurrent(bool completed = false)
    {
        if (!ready || mediaDuration <= 0 || (!completed && PositionSeconds <= 0)) return;
        try { store.SaveProgress(movie, completed ? mediaDuration : PositionSeconds, mediaDuration); lastSaved = DateTimeOffset.UtcNow; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (!disposed) Notice.Text = "تعذّر حفظ موضع المشاهدة."; }
    }
    private void SaveDictionary(string name, string key, double value)
    {
        try { var map = store.Load(name, new Dictionary<string, double>()); if (value == 0) map.Remove(key); else map[key] = value; store.Save(name, map); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("player-store", e); }
    }
    private async Task LoadEpisodes(string id, CancellationToken token)
    {
        try
        {
            episodes = await service.Episodes(id, token); token.ThrowIfCancellationRequested();
            int index = episodes.FindIndex(e => e.Id == movie.Id);
            nextEpisode = index >= 0 && index + 1 < episodes.Count ? episodes[index + 1] : null;
            NextEpisodeButton.Visibility = nextEpisode is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException) { }
    }
    private async Task<List<SubtitleCue>> ReadTrack(SubtitleTrack track, CancellationToken token)
        => track.Language == "local"
            ? SubtitleParser.Parse(SubtitleParser.Decode(await File.ReadAllBytesAsync(track.Url, token)))
            : await service.Subtitles(track, token);
    private async Task LoadSubtitle(CancellationToken parent)
    {
        subtitleRequest?.Cancel(); subtitleRequest?.Dispose(); subtitleRequest = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var token = subtitleRequest.Token;
        cues = []; SubtitleBox.Visibility = Visibility.Collapsed;
        if (Subtitles.SelectedItem is not SubtitleTrack track || track.Url.Length == 0) return;
        try
        {
            var loaded = await ReadTrack(track, token); token.ThrowIfCancellationRequested(); cues = loaded;
            SubtitleLabel.FlowDirection = IsArabic(track, loaded) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            if (loaded.Count == 0) Notice.Text = "لم يتضمن ملف الترجمة نصًا قابلًا للعرض.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        { Log.Error("subtitle", e); if (!disposed && !token.IsCancellationRequested) Notice.Text = "تعذّر تحميل الترجمة. يمكنك اختيار لغة أخرى أو المتابعة بدون ترجمة."; }
    }
    private async Task LoadSecondSubtitle(CancellationToken parent)
    {
        secondRequest?.Cancel(); secondRequest?.Dispose(); secondRequest = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var token = secondRequest.Token;
        secondCues = []; SecondBox.Visibility = Visibility.Collapsed;
        if (SecondSubtitles.SelectedItem is not SubtitleTrack track || track.Url.Length == 0) return;
        try
        {
            var loaded = await ReadTrack(track, token); token.ThrowIfCancellationRequested(); secondCues = loaded;
            SecondLabel.FlowDirection = IsArabic(track, loaded) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        { Log.Error("subtitle2", e); }
    }
    private static bool IsArabic(SubtitleTrack track, List<SubtitleCue> loaded)
        => track.Language == "ar" || track.Language == "local" && loaded.Take(20).Any(c => c.Text.Any(ch => ch is >= '؀' and <= 'ۿ'));
    private void TogglePlay()
    {
        if (!ready) return;
        if (playing) { Video.Pause(); playing = false; SaveCurrent(); ShowControls(); }
        else { StopCountdown(); if (PositionSeconds >= mediaDuration - 0.5) Video.Position = TimeSpan.Zero; Video.Play(); Video.SpeedRatio = SelectedSpeed; playing = true; lastMouse = DateTimeOffset.UtcNow; }
        PlayButton.Content = playing ? "إيقاف مؤقت" : "تشغيل";
    }
    private void Seek(double seconds) { if (!ready) return; Video.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, mediaDuration - 0.1))); UpdatePosition(); }
    private void ShiftSubtitle(double delta)
    {
        subtitleOffset = Math.Round(subtitleOffset + delta, 1); UpdateOffsetLabel(); UpdatePosition();
        SaveDictionary("subtitle-offsets", movie.Id, subtitleOffset);
    }
    private void UpdateOffsetLabel() => OffsetLabel.Text = $"توقيت {subtitleOffset:+0.0;-0.0;0.0}ث";
    // --- Subtitle style ---
    private void ApplySubtitleStyle()
    {
        if (SubtitleLabel is null || SecondLabel is null || SubtitleBox is null || SecondBox is null) return;
        if (SubtitleFont.SelectedItem is ComboBoxItem font) SubtitleLabel.FontFamily = SecondLabel.FontFamily = new FontFamily((string)font.Content);
        if (SubtitleColor.SelectedItem is ComboBoxItem { Tag: string hex }) SubtitleLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        var background = new SolidColorBrush(Color.FromArgb((byte)(SubtitleBackground.Value * 255), 0, 0, 0));
        SubtitleBox.Background = SecondBox.Background = background;
        SubtitleLabel.FontSize = SubtitleSize.Value; SecondLabel.FontSize = Math.Max(14, SubtitleSize.Value * 0.75);
    }
    private void PlaceSubtitles(bool top)
    {
        SubtitleStack.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        SubtitleStack.Margin = top ? new Thickness(30, 24, 30, 0) : new Thickness(30, 0, 30, 24);
        PositionButton.Content = top ? "الترجمة للأسفل" : "الترجمة للأعلى";
    }
    private void SubtitleStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySubtitleStyle();
        if (changingControls || SubtitleFont is null || SubtitleColor is null) return;
        string font = (SubtitleFont.SelectedItem as ComboBoxItem)?.Content as string ?? "Segoe UI";
        string color = (SubtitleColor.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FFFFFF";
        SaveSettings(s => s with { SubtitleFont = font, SubtitleColor = color });
    }
    private void SubtitleBackgroundChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { ApplySubtitleStyle(); if (!changingControls && SubtitleLabel is not null) SaveSettings(s => s with { SubtitleBackground = e.NewValue }); }
    private void SubtitleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { ApplySubtitleStyle(); if (!changingControls && SubtitleLabel is not null) SaveSettings(s => s with { SubtitleSize = e.NewValue }); }
    private void SubtitlePositionClick(object sender, RoutedEventArgs e)
    { bool top = SubtitleStack.VerticalAlignment != VerticalAlignment.Top; PlaceSubtitles(top); SaveSettings(s => s with { SubtitleTop = top }); }
    private void LoadSubtitleFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "اختر ملف ترجمة", Filter = "ملفات الترجمة (*.srt;*.vtt)|*.srt;*.vtt" };
        if (dialog.ShowDialog() != true || Subtitles.ItemsSource is not List<SubtitleTrack> tracks) return;
        var track = new SubtitleTrack(Path.GetFileName(dialog.FileName), dialog.FileName, "local");
        tracks.Add(track); changingControls = true; Subtitles.Items.Refresh(); Subtitles.SelectedItem = track; changingControls = false;
        if (request is not null) _ = LoadSubtitle(request.Token);
    }
    // --- Search and export of the subtitle text ---
    private void SearchSubtitleClick(object sender, RoutedEventArgs e) { SearchPanel.Visibility = Visibility.Visible; SubtitleQuery.Focus(); }
    private void CloseSearchClick(object sender, RoutedEventArgs e) { SearchPanel.Visibility = Visibility.Collapsed; VideoSurface.Focus(); }
    private void SubtitleQueryChanged(object sender, TextChangedEventArgs e)
    {
        string q = SubtitleQuery.Text.Trim();
        SubtitleMatches.ItemsSource = q.Length < 2 ? null : cues.Where(c => c.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(100).Select(c => new Match(Stamp(c.Start), c.Text, c.Start)).ToList();
    }
    private void MatchPicked(object sender, SelectionChangedEventArgs e) { if (SubtitleMatches.SelectedItem is Match m) Seek(m.Start + subtitleOffset - 0.5); }
    private void ExportSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (cues.Count == 0) { Notice.Text = "لا توجد ترجمة محمّلة لحفظ نصها."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "حفظ نص الترجمة", Filter = "ملف نصي (*.txt)|*.txt", FileName = DownloadPaths.SafeName(movie.DisplayTitle) + ".txt" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllLines(dialog.FileName, cues.Select(c => c.Text.Replace("\n", " ")), new System.Text.UTF8Encoding(true)); Notice.Text = "حُفظ نص الترجمة."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice.Text = "تعذّر حفظ الملف."; }
    }
    // --- Intro ---
    private void SkipIntroClick(object sender, RoutedEventArgs e) => Seek(introEnd);
    private void MarkIntroClick(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        introEnd = PositionSeconds > 5 && PositionSeconds < 600 ? Math.Round(PositionSeconds) : 0;
        SaveDictionary("intros", SeriesKey, introEnd);
        Notice.Text = introEnd > 0 ? $"حُفظت نهاية المقدمة عند {Stamp(introEnd)}. سيظهر زر التخطي في كل حلقات هذا العنوان." : "أُزيل وقت المقدمة.";
    }
    // --- Other controls ---
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void VideoClick(object sender, MouseButtonEventArgs e) { VideoSurface.Focus(); if (e.ClickCount == 2) FullscreenRequested?.Invoke(); else TogglePlay(); }
    private void VideoWheel(object sender, MouseWheelEventArgs e) { VolumeBar.Value = Math.Clamp(VolumeBar.Value + (e.Delta > 0 ? 0.05 : -0.05), 0, 1); Notice.Text = $"الصوت {VolumeBar.Value * 100:0}%"; e.Handled = true; }
    private void OverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { FullscreenRequested?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Escape) { if (SearchPanel.Visibility == Visibility.Visible) CloseSearchClick(this, new()); else EscapeRequested?.Invoke(); e.Handled = true; }
        else e.Handled = HandleKey(e);
    }
    private void RewindClick(object sender, RoutedEventArgs e) => Seek(PositionSeconds - 10);
    private void ForwardClick(object sender, RoutedEventArgs e) => Seek(PositionSeconds + 10);
    private void ResumeClick(object sender, RoutedEventArgs e) => Seek(savedPosition);
    private async void RetryClick(object sender, RoutedEventArgs e) { if (localFile is not null) OpenLocal(movie, localFile); else await Open(movie); }
    private async void NextClick(object sender, RoutedEventArgs e) { StopCountdown(); if (nextEpisode is not null) await Open(nextEpisode); }
    private void BackClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void DownloadClick(object sender, RoutedEventArgs e) => DownloadRequested?.Invoke(movie);
    private void FullscreenClick(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
    private void PipClick(object sender, RoutedEventArgs e) => PipRequested?.Invoke();
    private void ZoomClick(object sender, RoutedEventArgs e)
    {
        bool fill = Video.Stretch != Stretch.UniformToFill;
        Video.Stretch = fill ? Stretch.UniformToFill : Stretch.Uniform; ZoomButton.Content = fill ? "الحجم الأصلي" : "ملء الإطار";
    }
    private void BrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessVeil is null) return;
        BrightnessVeil.Fill = e.NewValue < 0 ? Brushes.Black : Brushes.White;
        BrightnessVeil.Opacity = Math.Abs(e.NewValue);
    }
    private void SleepChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Notice is null) return;
        sleepAt = null; sleepAtEnd = false;
        switch (SleepTimer.SelectedIndex)
        {
            case 1: sleepAt = DateTimeOffset.UtcNow.AddMinutes(30); Notice.Text = "سيتوقف التشغيل بعد 30 دقيقة."; break;
            case 2: sleepAt = DateTimeOffset.UtcNow.AddMinutes(60); Notice.Text = "سيتوقف التشغيل بعد ساعة."; break;
            case 3: sleepAtEnd = true; Notice.Text = "سيتوقف التشغيل في نهاية الحلقة."; break;
        }
    }
    private void AutoNextChanged(object sender, RoutedEventArgs e) { if (!changingControls) { bool on = AutoNextBox.IsChecked == true; SaveSettings(s => s with { AutoNext = on }); } }
    private void SubtitleEarlierClick(object sender, RoutedEventArgs e) => ShiftSubtitle(-0.5);
    private void SubtitleLaterClick(object sender, RoutedEventArgs e) => ShiftSubtitle(0.5);
    private void QualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingControls || disposed || Quality.SelectedItem is not VideoSource source) return;
        if (ready) { pendingPosition = PositionSeconds; SaveCurrent(); }
        OpenSource(source);
    }
    private void SpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingControls || disposed) return;
        double speed = SelectedSpeed;
        if (ready) Video.SpeedRatio = speed;
        SaveSettings(s => s with { PlaybackSpeed = speed });
    }
    private async void SubtitleChanged(object sender, SelectionChangedEventArgs e) { if (!changingControls && !disposed && request is not null) await LoadSubtitle(request.Token); }
    private async void SecondSubtitleChanged(object sender, SelectionChangedEventArgs e) { if (!changingControls && !disposed && request is not null) await LoadSecondSubtitle(request.Token); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Video is null) return;
        Video.Volume = e.NewValue;
        if (!changingControls) SaveSettings(s => s with { Volume = e.NewValue });
    }
    private void MuteClick(object sender, RoutedEventArgs e)
    {
        Video.IsMuted = !Video.IsMuted; MuteButton.Content = Video.IsMuted ? "إلغاء الكتم" : "الصوت";
        bool muted = Video.IsMuted; SaveSettings(s => s with { Muted = muted });
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => dragging = true;
    private void SeekFinished(object sender, MouseButtonEventArgs e) { if (!dragging) return; dragging = false; Seek(SeekBar.Value); }
    private void SeekLostCapture(object sender, MouseEventArgs e) { if (dragging) { dragging = false; Seek(SeekBar.Value); } }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready && !updatingSeek && !dragging) Seek(e.NewValue); }
    private void SeekHover(object sender, MouseEventArgs e)
    {
        // Time under the pointer, shown as a tooltip while hovering the bar.
        if (!ready || SeekBar.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(SeekBar).X / SeekBar.ActualWidth, 0, 1);
        SeekBar.ToolTip = Stamp(ratio * mediaDuration);
    }
    public bool HandleKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { SearchSubtitleClick(this, new()); return true; }
        if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is ComboBox or ComboBoxItem or Slider or TextBox or System.Windows.Controls.Primitives.ButtonBase) return false;
        lastMouse = DateTimeOffset.UtcNow; if (controlsHidden && e.Key is not Key.Space) ShowControls();
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); return true;
            case Key.Left: Seek(PositionSeconds - 10); return true;
            case Key.Right: Seek(PositionSeconds + 10); return true;
            case Key.Up: VolumeBar.Value = Math.Min(1, VolumeBar.Value + 0.05); return true;
            case Key.Down: VolumeBar.Value = Math.Max(0, VolumeBar.Value - 0.05); return true;
            case Key.M: MuteClick(this, new()); return true;
            case Key.P: PipRequested?.Invoke(); return true;
            case Key.Z: ZoomClick(this, new()); return true;
            case Key.S when SkipIntroButton.Visibility == Visibility.Visible: Seek(introEnd); return true;
            case Key.N when nextEpisode is not null: NextClick(this, new()); return true;
            case Key.OemOpenBrackets: ShiftSubtitle(-0.5); return true;
            case Key.OemCloseBrackets: ShiftSubtitle(0.5); return true;
            default: return false;
        }
    }
    /// <summary>Compact layout for the small always-on-top window: picture only, controls on hover.</summary>
    public void SetCompact(bool compact)
    {
        TopBar.Visibility = BottomBar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        controlsHidden = compact;
    }
    public void Dispose()
    {
        if (disposed) return;
        SaveCurrent(); disposed = true; timer.Stop(); countdown.Stop();
        request?.Cancel(); subtitleRequest?.Cancel(); secondRequest?.Cancel();
        Video.Stop(); Video.Close(); Video.Source = null;
        service.Dispose(); request?.Dispose(); subtitleRequest?.Dispose(); secondRequest?.Dispose();
    }
}
