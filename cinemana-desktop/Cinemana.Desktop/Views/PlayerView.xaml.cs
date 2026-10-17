using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private double PositionSeconds => Video.Position.TotalSeconds;
    private readonly LibraryStore store;
    private readonly CinemanaService service;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? request, subtitleRequest;
    private List<SubtitleCue> cues = [];
    private List<Movie> episodes = [];
    private Movie movie;
    private string? localFile;
    private Uri? currentSource;
    private bool disposed, ready, playing, changingControls, dragging, updatingSeek;
    private double pendingPosition, savedPosition, mediaDuration, subtitleOffset;
    private DateTimeOffset lastSaved = DateTimeOffset.MinValue;
    private DateTimeOffset openedAt;
    private bool timeoutShown;
    private Movie? nextEpisode;
    public event Action? CloseRequested;
    public event Action? FullscreenRequested;
    public event Action? EscapeRequested;
    public event Action<Movie>? DownloadRequested;

    public PlayerView(Movie movie, LibraryStore store)
    {
        this.movie = movie; this.store = store; service = new(store.Root);
        InitializeComponent();
        var settings = store.Load("settings", new AppSettings());
        changingControls = true;
        Speed.ItemsSource = Speeds.Select(s => s.ToString("0.##") + "×").ToList();
        int speedIndex = Array.IndexOf(Speeds, settings.PlaybackSpeed);
        Speed.SelectedIndex = speedIndex >= 0 ? speedIndex : 2;
        SubtitleSize.Value = Math.Clamp(settings.SubtitleSize, SubtitleSize.Minimum, SubtitleSize.Maximum);
        SubtitleLabel.FontSize = SubtitleSize.Value;
        changingControls = false;
        Video.Volume = VolumeBar.Value;
        timer.Tick += Tick;
        timer.Start();
    }

    private double SelectedSpeed => Speed.SelectedIndex >= 0 ? Speeds[Speed.SelectedIndex] : 1;

    private void Reset(Movie target)
    {
        SaveCurrent();
        request?.Cancel(); request?.Dispose(); request = new();
        subtitleRequest?.Cancel();
        ready = playing = false; Video.Stop(); Video.Source = null;
        movie = target; TitleLabel.Text = target.DisplayTitle + (target.Kind == "2" ? " • " + target.EpisodeLabel : "");
        TitleLabel.ToolTip = target.Title;
        LoadingLabel.Text = "جارٍ تجهيز الفيديو…"; LoadingPanel.Visibility = Visibility.Visible;
        PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        SubtitleBox.Visibility = Visibility.Collapsed; cues = []; episodes = []; nextEpisode = null;
        NextEpisodeButton.Visibility = Visibility.Collapsed;
        CurrentTime.Text = TotalTime.Text = "00:00:00";
        mediaDuration = 0; savedPosition = 0; pendingPosition = 0; subtitleOffset = 0; UpdateOffsetLabel();
        var old = store.History().FirstOrDefault(e => e.Movie.Id == target.Id);
        if (old is { Position: > 0 } && old.Duration - old.Position > 10) savedPosition = old.Position;
        ResumeButton.IsEnabled = false;
        ResumeButton.Content = savedPosition > 0 ? "استئناف " + Stamp(savedPosition) : "لا يوجد موضع محفوظ";
        changingControls = true; Quality.ItemsSource = null; Subtitles.ItemsSource = null; changingControls = false;
    }

    public async Task Open(Movie target)
    {
        if (disposed) return;
        Reset(target); localFile = null; DownloadButton.Visibility = Visibility.Visible;
        var token = request!.Token;
        Notice.Text = "جارٍ جلب مصدر الفيديو…";
        try
        {
            var playback = await service.Playback(target.Id, token);
            token.ThrowIfCancellationRequested();
            movie = playback.Movie with { LocalPoster = target.LocalPoster };
            var playable = playback.Sources.Where(s => s.Container is "mp4" or "mkv").ToList();
            if (playable.Count == 0) throw new InvalidDataException("لا تتوفر نسخة MP4 قابلة للتشغيل لهذا العنوان. يمكنك تنزيله إن توفّر ملف مباشر.");
            changingControls = true;
            Quality.ItemsSource = playable;
            Quality.SelectedItem = playable.LastOrDefault(s => s.Height <= 720 && s.Container == "mp4") ?? playable.LastOrDefault(s => s.Height <= 720) ?? playable[0];
            Subtitles.ItemsSource = playback.Subtitles;
            Subtitles.SelectedItem = playback.Subtitles.FirstOrDefault(s => s.Language == "ar") ?? playback.Subtitles[0];
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
        string srt = FileDownloader.SubtitlePath(file);
        if (File.Exists(srt)) tracks.Add(new("الترجمة المحفوظة", srt, "local"));
        changingControls = true;
        Quality.ItemsSource = new List<VideoSource> { source }; Quality.SelectedIndex = 0;
        Subtitles.ItemsSource = tracks; Subtitles.SelectedItem = tracks.Last();
        changingControls = false;
        pendingPosition = savedPosition; playing = true;
        OpenSource(source);
        _ = LoadSubtitle(request!.Token);
    }

    private void OpenSource(VideoSource source)
    {
        ready = false; PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        openedAt = DateTimeOffset.UtcNow; timeoutShown = false;
        LoadingLabel.Text = "جارٍ تحميل الفيديو…"; LoadingPanel.Visibility = Visibility.Visible;
        SubtitleBox.Visibility = Visibility.Collapsed;
        Notice.Text = (localFile is null ? "جودة " + source.Label : "تشغيل بدون إنترنت") + " • Space تشغيل/إيقاف • الأسهم ±10 ثوانٍ • [ ] توقيت الترجمة";
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
        ready = playing = false; currentSource = null; PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        Notice.Text = localFile is not null
            ? "تعذّر تشغيل الملف. قد يحتاج Windows إلى ملحق الترميز (HEVC/MKV) من متجر Microsoft."
            : "تعذّر تشغيل هذه النسخة. جرّب جودة أخرى أو اضغط إعادة المحاولة.";
    }

    private void VideoEnded(object sender, RoutedEventArgs e)
    {
        SaveCurrent(completed: true); playing = false; PlayButton.Content = "إعادة التشغيل";
        Notice.Text = nextEpisode is null ? "انتهت المشاهدة." : "انتهت الحلقة. يمكنك الانتقال إلى الحلقة التالية.";
    }
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
        if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(5)) SaveCurrent();
    }
    private static string Stamp(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
    private void UpdatePosition()
    {
        double position = PositionSeconds;
        CurrentTime.Text = Stamp(position);
        if (!dragging) { updatingSeek = true; SeekBar.Value = Math.Clamp(position, 0, SeekBar.Maximum); updatingSeek = false; }
        // A positive offset delays the subtitle; a negative one shows it earlier.
        SubtitleLabel.Text = SubtitleParser.At(cues, position - subtitleOffset);
        SubtitleBox.Visibility = SubtitleLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveCurrent(bool completed = false)
    {
        if (!ready || mediaDuration <= 0 || (!completed && PositionSeconds <= 0)) return;
        try { store.SaveProgress(movie, completed ? mediaDuration : PositionSeconds, mediaDuration); lastSaved = DateTimeOffset.UtcNow; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (!disposed) Notice.Text = "تعذّر حفظ موضع المشاهدة."; }
    }
    private void SaveSettings(Func<AppSettings, AppSettings> change)
    {
        try { store.Save("settings", change(store.Load("settings", new AppSettings()))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
    private async Task LoadSubtitle(CancellationToken parent)
    {
        subtitleRequest?.Cancel(); subtitleRequest?.Dispose(); subtitleRequest = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var token = subtitleRequest.Token;
        cues = []; SubtitleBox.Visibility = Visibility.Collapsed;
        if (Subtitles.SelectedItem is not SubtitleTrack track || track.Url.Length == 0) return;
        try
        {
            var loaded = track.Language == "local"
                ? SubtitleParser.Parse(await File.ReadAllTextAsync(track.Url, token))
                : await service.Subtitles(track, token);
            token.ThrowIfCancellationRequested(); cues = loaded;
            bool rtl = track.Language == "ar" || (track.Language == "local" && loaded.Take(20).Any(c => c.Text.Any(ch => ch is >= '؀' and <= 'ۿ')));
            SubtitleLabel.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            if (loaded.Count == 0) Notice.Text = "لم يتضمن ملف الترجمة نصًا قابلًا للعرض.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        { if (!disposed && !token.IsCancellationRequested) Notice.Text = "تعذّر تحميل الترجمة. يمكنك اختيار لغة أخرى أو المتابعة بدون ترجمة."; }
    }
    private void TogglePlay()
    {
        if (!ready) return;
        if (playing) { Video.Pause(); playing = false; SaveCurrent(); }
        else { if (PositionSeconds >= mediaDuration - 0.5) Video.Position = TimeSpan.Zero; Video.Play(); Video.SpeedRatio = SelectedSpeed; playing = true; }
        PlayButton.Content = playing ? "إيقاف مؤقت" : "تشغيل";
    }
    private void Seek(double seconds) { if (!ready) return; Video.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, mediaDuration - 0.1))); UpdatePosition(); }
    private void ShiftSubtitle(double delta) { subtitleOffset = Math.Round(subtitleOffset + delta, 1); UpdateOffsetLabel(); UpdatePosition(); }
    private void UpdateOffsetLabel() => OffsetLabel.Text = $"توقيت {subtitleOffset:+0.0;-0.0;0.0}ث";
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void VideoClick(object sender, MouseButtonEventArgs e) { VideoSurface.Focus(); if (e.ClickCount == 2) FullscreenRequested?.Invoke(); else TogglePlay(); }
    private void OverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { FullscreenRequested?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EscapeRequested?.Invoke(); e.Handled = true; }
        else e.Handled = HandleKey(e);
    }
    private void RewindClick(object sender, RoutedEventArgs e) => Seek(PositionSeconds - 10);
    private void ForwardClick(object sender, RoutedEventArgs e) => Seek(PositionSeconds + 10);
    private void ResumeClick(object sender, RoutedEventArgs e) => Seek(savedPosition);
    private async void RetryClick(object sender, RoutedEventArgs e) { if (localFile is not null) OpenLocal(movie, localFile); else await Open(movie); }
    private async void NextClick(object sender, RoutedEventArgs e) { if (nextEpisode is not null) await Open(nextEpisode); }
    private void BackClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void DownloadClick(object sender, RoutedEventArgs e) => DownloadRequested?.Invoke(movie);
    private void FullscreenClick(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
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
    private void SubtitleSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SubtitleLabel is null) return;
        SubtitleLabel.FontSize = e.NewValue;
        if (!changingControls) SaveSettings(s => s with { SubtitleSize = e.NewValue });
    }
    private async void SubtitleChanged(object sender, SelectionChangedEventArgs e) { if (!changingControls && !disposed && request is not null) await LoadSubtitle(request.Token); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Video is not null) Video.Volume = e.NewValue; }
    private void MuteClick(object sender, RoutedEventArgs e) { Video.IsMuted = !Video.IsMuted; MuteButton.Content = Video.IsMuted ? "إلغاء الكتم" : "الصوت"; }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => dragging = true;
    private void SeekFinished(object sender, MouseButtonEventArgs e) { if (!dragging) return; dragging = false; Seek(SeekBar.Value); }
    private void SeekLostCapture(object sender, MouseEventArgs e) { if (dragging) { dragging = false; Seek(SeekBar.Value); } }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (ready && !updatingSeek && !dragging) Seek(e.NewValue); }
    public bool HandleKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is ComboBox or ComboBoxItem or Slider or System.Windows.Controls.Primitives.ButtonBase) return false;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); return true;
            case Key.Left: Seek(PositionSeconds - 10); return true;
            case Key.Right: Seek(PositionSeconds + 10); return true;
            case Key.M: MuteClick(this, new()); return true;
            case Key.OemOpenBrackets: ShiftSubtitle(-0.5); return true;
            case Key.OemCloseBrackets: ShiftSubtitle(0.5); return true;
            default: return false;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        SaveCurrent(); disposed = true; timer.Stop();
        request?.Cancel(); subtitleRequest?.Cancel();
        Video.Stop(); Video.Close(); Video.Source = null;
        service.Dispose(); request?.Dispose(); subtitleRequest?.Dispose();
    }
}
