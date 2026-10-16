using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cinemana.Desktop.Models;
using Cinemana.Desktop.Services;
using LibVLCSharp.Shared;
using SubtitleTrack = Cinemana.Desktop.Models.SubtitleTrack;

namespace Cinemana.Desktop.Views;

public partial class PlayerView : UserControl, IDisposable
{
    private readonly LibVLC engine;
    private readonly LibVLCSharp.Shared.MediaPlayer player;
    private Media? activeMedia;
    private Uri? currentSource;
    private double PositionSeconds => Math.Max(0, player.Time) / 1000d;
    private readonly LibraryStore store;
    private readonly CinemanaService service;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? request, subtitleRequest;
    private List<SubtitleCue> cues = [];
    private List<Movie> episodes = [];
    private Movie movie;
    private bool disposed, ready, playing, changingControls, dragging, updatingSeek;
    private double pendingPosition, savedPosition, mediaDuration;
    private DateTimeOffset lastSaved = DateTimeOffset.MinValue;
    private DateTimeOffset openedAt;
    private bool timeoutShown;
    private Movie? nextEpisode;
    public event Action? CloseRequested;
    public event Action? FullscreenRequested;
    public event Action? EscapeRequested;
    public event Action<Movie>? DownloadRequested;

    public PlayerView(Movie movie, LibraryStore store, LibVLC engine)
    {
        this.movie = movie; this.store = store; service = new(store.Root);
        this.engine = engine;
        player = new LibVLCSharp.Shared.MediaPlayer(engine) { Volume = 80, EnableKeyInput = false, EnableMouseInput = false };
        InitializeComponent();
        Video.MediaPlayer = player;
        player.Playing += (_, _) => Dispatcher.BeginInvoke(() => { if (!disposed && !ready) VideoOpened(this, new RoutedEventArgs()); });
        player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => { if (!disposed) VideoFailed(); });
        player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => { if (!disposed) VideoEnded(this, new RoutedEventArgs()); });
        player.Buffering += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            if (e.Cache < 100) BufferingStarted(this, new RoutedEventArgs()); else BufferingEnded(this, new RoutedEventArgs());
        });
        timer.Tick += Tick;
        timer.Start();
    }

    public async Task Open(Movie target)
    {
        if (disposed) return;
        SaveCurrent();
        request?.Cancel(); request?.Dispose(); request = new(); var token = request.Token;
        subtitleRequest?.Cancel();
        ready = playing = false; player.Stop();
        movie = target; TitleLabel.Text = target.DisplayTitle;
        TitleLabel.ToolTip = target.Title;
        Notice.Text = "جارٍ جلب مصدر الفيديو…";
        LoadingLabel.Text = "جارٍ تجهيز الفيديو…"; LoadingPanel.Visibility = Visibility.Visible;
        PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        SubtitleBox.Visibility = Visibility.Collapsed; cues = []; episodes = []; nextEpisode = null;
        NextEpisodeButton.Visibility = Visibility.Collapsed;
        CurrentTime.Text = TotalTime.Text = "00:00:00";
        mediaDuration = 0; savedPosition = 0; pendingPosition = 0;
        var old = store.History().FirstOrDefault(e => e.Movie.Id == target.Id);
        if (old is { Position: > 0 } && old.Duration - old.Position > 10) savedPosition = old.Position;
        ResumeButton.IsEnabled = false;
        ResumeButton.Content = savedPosition > 0 ? "استئناف " + Stamp(savedPosition) : "لا يوجد موضع محفوظ";
        changingControls = true; Quality.ItemsSource = null; Subtitles.ItemsSource = null; changingControls = false;
        try
        {
            var playback = await service.Playback(target.Id, token);
            token.ThrowIfCancellationRequested();
            movie = playback.Movie with { LocalPoster = target.LocalPoster };
            changingControls = true;
            Quality.ItemsSource = playback.Sources;
            Quality.SelectedItem = playback.Sources.LastOrDefault(s => s.Height <= 720) ?? playback.Sources[0];
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
            Notice.Text = e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "الخدمة لا تسمح بتشغيل هذا العنوان على الاتصال الحالي."
                : "تعذّر جلب الفيديو. تحقق من الشبكة ثم اضغط إعادة المحاولة.";
        }
    }

    private void OpenSource(VideoSource source)
    {
        ready = false; PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        openedAt = DateTimeOffset.UtcNow; timeoutShown = false;
        LoadingLabel.Text = "جارٍ تحميل الفيديو…"; LoadingPanel.Visibility = Visibility.Visible;
        OverlayRoot.Background = System.Windows.Media.Brushes.Black;
        SubtitleBox.Visibility = Visibility.Collapsed;
        Notice.Text = "جودة " + source.Label + " • Space تشغيل/إيقاف • الأسهم ±10 ثوانٍ";
        player.Stop();
        currentSource = new Uri(source.Url); activeMedia?.Dispose(); activeMedia = new Media(engine, currentSource); activeMedia.AddOption(":network-caching=1200");
        if (!player.Play(activeMedia)) VideoFailed();
    }

    private void VideoOpened(object sender, RoutedEventArgs e)
    {
        if (disposed || currentSource is null) return;
        mediaDuration = player.Length > 0 ? player.Length / 1000d : movie.Duration;
        ready = true; LoadingPanel.Visibility = Visibility.Collapsed;
        OverlayRoot.Background = System.Windows.Media.Brushes.Transparent;
        PlayButton.IsEnabled = true; SeekBar.IsEnabled = mediaDuration > 0;
        updatingSeek = true; SeekBar.Maximum = Math.Max(1, mediaDuration); updatingSeek = false;
        if (pendingPosition > 0 && mediaDuration > 0) player.Time = (long)(Math.Min(pendingPosition, Math.Max(0, mediaDuration - 1)) * 1000);
        pendingPosition = 0;
        if (playing) player.Play(); else player.SetPause(true);
        ResumeButton.IsEnabled = savedPosition > 0;
        PlayButton.Content = playing ? "إيقاف مؤقت" : "تشغيل";
        TotalTime.Text = Stamp(mediaDuration);
        UpdatePosition();
    }

    private void VideoFailed()
    {
        if (disposed) return;
        ready = playing = false; currentSource = null; PlayButton.IsEnabled = SeekBar.IsEnabled = false;
        OverlayRoot.Background = System.Windows.Media.Brushes.Black;
        LoadingPanel.Visibility = Visibility.Collapsed;
        Notice.Text = "تعذّر تشغيل هذه النسخة. جرّب جودة أخرى أو اضغط إعادة المحاولة.";
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
        UpdatePosition();
        if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(5)) SaveCurrent();
    }
    private static string Stamp(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
    private void UpdatePosition()
    {
        double position = PositionSeconds;
        CurrentTime.Text = Stamp(position);
        if (!dragging) { updatingSeek = true; SeekBar.Value = Math.Clamp(position, 0, SeekBar.Maximum); updatingSeek = false; }
        SubtitleLabel.Text = SubtitleParser.At(cues, position);
        SubtitleBox.Visibility = SubtitleLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SaveCurrent(bool completed = false)
    {
        if (!ready || mediaDuration <= 0 || (!completed && PositionSeconds <= 0)) return;
        try { store.SaveProgress(movie, completed ? mediaDuration : PositionSeconds, mediaDuration); lastSaved = DateTimeOffset.UtcNow; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (!disposed) Notice.Text = "تعذّر حفظ موضع المشاهدة."; }
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
            var loaded = await service.Subtitles(track, token); token.ThrowIfCancellationRequested(); cues = loaded;
            SubtitleLabel.FlowDirection = track.Language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            if (loaded.Count == 0) Notice.Text = "لم يتضمن ملف الترجمة نصًا قابلًا للعرض.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or FormatException)
        { if (!disposed && !token.IsCancellationRequested) Notice.Text = "تعذّر تحميل الترجمة. يمكنك اختيار لغة أخرى أو المتابعة بدون ترجمة."; }
    }
    private void TogglePlay()
    {
        if (!ready) return;
        if (playing) { player.SetPause(true); playing = false; SaveCurrent(); }
        else { if (PositionSeconds >= mediaDuration - 0.5) player.Time = 0; player.Play(); playing = true; }
        PlayButton.Content = playing ? "إيقاف مؤقت" : "تشغيل";
    }
    private void Seek(double seconds) { if (!ready) return; player.Time = (long)(Math.Clamp(seconds, 0, Math.Max(0, mediaDuration - 0.1)) * 1000); UpdatePosition(); }
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void VideoClick(object sender, MouseButtonEventArgs e) { if (sender is UIElement element) element.Focus(); if (e.ClickCount == 2) FullscreenRequested?.Invoke(); else TogglePlay(); }
    private void OverlayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { FullscreenRequested?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EscapeRequested?.Invoke(); e.Handled = true; }
        else e.Handled = HandleKey(e);
    }
    private void RewindClick(object sender, RoutedEventArgs e) => Seek(PositionSeconds - 10);
    private void ForwardClick(object sender, RoutedEventArgs e) => Seek(PositionSeconds + 10);
    private void ResumeClick(object sender, RoutedEventArgs e) => Seek(savedPosition);
    private async void RetryClick(object sender, RoutedEventArgs e) => await Open(movie);
    private async void NextClick(object sender, RoutedEventArgs e) { if (nextEpisode is not null) await Open(nextEpisode); }
    private void BackClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void DownloadClick(object sender, RoutedEventArgs e) => DownloadRequested?.Invoke(movie);
    private void FullscreenClick(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
    private void QualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingControls || disposed || Quality.SelectedItem is not VideoSource source) return;
        if (ready) { pendingPosition = PositionSeconds; SaveCurrent(); }
        OpenSource(source);
    }
    private async void SubtitleChanged(object sender, SelectionChangedEventArgs e) { if (!changingControls && !disposed && request is not null) await LoadSubtitle(request.Token); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (player is not null) player.Volume = (int)(e.NewValue * 100); }
    private void MuteClick(object sender, RoutedEventArgs e) { player.Mute = !player.Mute; MuteButton.Content = player.Mute ? "إلغاء الكتم" : "الصوت"; }
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
            default: return false;
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        SaveCurrent(); disposed = true; timer.Stop();
        request?.Cancel(); subtitleRequest?.Cancel();
        player.Stop();
        Video.MediaPlayer = null; Video.Dispose(); activeMedia?.Dispose(); player.Dispose();
        service.Dispose(); request?.Dispose(); subtitleRequest?.Dispose();
    }
}



