using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InstagramStoryViewer.Gui.ViewModels;

namespace InstagramStoryViewer.Gui.Views;

/// <summary>
/// Full-screen story player over the current table: images stay 5 s, videos play to the end, then it moves
/// on (like Instagram). Arrow keys follow the right-to-left layout: ← next, → previous.
/// </summary>
public partial class FullscreenViewer : Window
{
    private static readonly TimeSpan ImageDuration = TimeSpan.FromSeconds(5);
    private static readonly ImageSourceConverter Images = new();

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<ProgressBar> _bars = new();
    private CancellationTokenSource _loadCts = new();
    private int _index;
    private bool _isVideo;
    private bool _paused;
    private TimeSpan _imageElapsed;
    private DateTime _imageResumedAt;

    public FullscreenViewer(MainViewModel vm, int startIndex)
    {
        InitializeComponent();
        _vm = vm;
        _index = startIndex;
        _tick.Tick += (_, _) => OnTick();
        SourceInitialized += (_, _) => CoverOwnerScreen();
        Loaded += async (_, _) =>
        {
            BuildSegments();
            await ShowAsync(_index);
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _loadCts.Cancel();
            Player.Stop();
            Player.Source = null;
        };
    }

    /// <summary>
    /// Covers the whole monitor the main window is on. Explicit bounds instead of WindowState.Maximized:
    /// a borderless maximized window can leave gaps (taskbar, mixed-DPI multi-monitor setups).
    /// </summary>
    private void CoverOwnerScreen()
    {
        var ownerHandle = Owner is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(Owner).Handle;
        var screen = ownerHandle == IntPtr.Zero
            ? System.Windows.Forms.Screen.PrimaryScreen!
            : System.Windows.Forms.Screen.FromHandle(ownerHandle);
        var b = screen.Bounds; // device pixels
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = toDip.Transform(new Point(b.Left, b.Top));
        var size = toDip.Transform(new Point(b.Width, b.Height));
        Left = topLeft.X;
        Top = topLeft.Y;
        Width = size.X;
        Height = size.Y;
    }

    private void BuildSegments()
    {
        Segments.Children.Clear();
        _bars.Clear();
        foreach (var _ in _vm.Stories)
        {
            var bar = new ProgressBar
            {
                Maximum = 1, Height = 3, Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), Foreground = Brushes.White
            };
            _bars.Add(bar);
            Segments.Children.Add(bar);
        }
    }

    /// <summary>Fire-and-forget wrapper for key and timer handlers: a failure is logged, never lost or fatal.</summary>
    private async void Show(int index)
    {
        try
        {
            await ShowAsync(index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InstagramStoryViewer.Diagnostics.AppLog.Error("Full-screen viewer failed to show a story", ex);
        }
    }

    private async Task ShowAsync(int index)
    {
        if (_vm.Stories.Count == 0)
        {
            Close();
            return;
        }
        if (index >= _vm.Stories.Count)
        {
            Close(); // finished, like Instagram
            return;
        }
        index = Math.Max(0, index);

        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _index = index;
        _paused = false;
        PausedBadge.Visibility = Visibility.Collapsed;
        var row = _vm.Stories[index];
        _vm.SelectedStory = row; // keep the table in sync

        for (var i = 0; i < _bars.Count; i++) _bars[i].Value = i < index ? 1 : 0;
        UserText.Text = row.Username + (row.IsHighlight ? "  · هايلايت" : "");
        MetaText.Text = $"{row.PostedText}   {index + 1} / {_vm.Stories.Count}";

        Player.Stop();
        Player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        Photo.Source = Images.Convert(row.PosterSource, typeof(ImageSource), null, CultureInfo.InvariantCulture) as ImageSource;
        _isVideo = row.IsVideo;

        if (_isVideo)
        {
            LoadingText.Visibility = Visibility.Visible;
            var path = await _vm.PrepareVideoAsync(row, ct);
            LoadingText.Visibility = Visibility.Collapsed;
            if (ct.IsCancellationRequested || !IsLoaded) return;
            if (path is not null && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                Player.Source = uri;
                Player.Visibility = Visibility.Visible;
                Player.Play();
            }
            else
            {
                _isVideo = false; // could not load the video: show its cover for the image duration
            }
        }

        _imageElapsed = TimeSpan.Zero;
        _imageResumedAt = DateTime.UtcNow;
        _tick.Start();
    }

    private void OnTick()
    {
        if (_index >= _bars.Count) return;
        double progress;
        if (_isVideo)
        {
            if (!Player.NaturalDuration.HasTimeSpan || Player.NaturalDuration.TimeSpan <= TimeSpan.Zero) return;
            progress = Player.Position.TotalMilliseconds / Player.NaturalDuration.TimeSpan.TotalMilliseconds;
        }
        else
        {
            var elapsed = _imageElapsed + (_paused ? TimeSpan.Zero : DateTime.UtcNow - _imageResumedAt);
            progress = elapsed / ImageDuration;
            if (progress >= 1)
            {
                _tick.Stop();
                Show(_index + 1);
                return;
            }
        }
        _bars[_index].Value = Math.Clamp(progress, 0, 1);
    }

    private void TogglePause()
    {
        _paused = !_paused;
        PausedBadge.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        if (_isVideo)
        {
            if (_paused) Player.Pause();
            else Player.Play();
        }
        else if (_paused)
        {
            _imageElapsed += DateTime.UtcNow - _imageResumedAt;
        }
        else
        {
            _imageResumedAt = DateTime.UtcNow;
        }
    }

    private void Next() => Show(_index + 1);
    private void Prev() => Show(Math.Max(0, _index - 1));

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.PageDown: Next(); break;
            case Key.Right or Key.PageUp: Prev(); break;
            case Key.Space: TogglePause(); break;
            case Key.D: Download_Click(this, e); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Stage_Click(object sender, MouseButtonEventArgs e)
    {
        // Right-to-left: the right half goes back, the left half goes forward.
        if (e.GetPosition(Stage).X > Stage.ActualWidth / 2) Prev();
        else Next();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => Next();

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Player.Visibility = Visibility.Collapsed;
        _isVideo = false; // fall back to the cover image for the image duration
        _imageElapsed = TimeSpan.Zero;
        _imageResumedAt = DateTime.UtcNow;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Prev();
    private void Next_Click(object sender, RoutedEventArgs e) => Next();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_index < _vm.Stories.Count) _vm.DownloadStoryCommand.Execute(_vm.Stories[_index]);
    }

    private void Seen_Click(object sender, RoutedEventArgs e)
    {
        if (!_paused) TogglePause(); // a confirmation dialog may appear
        if (_index < _vm.Stories.Count) _vm.MarkSeenCommand.Execute(_vm.Stories[_index]);
    }
}
