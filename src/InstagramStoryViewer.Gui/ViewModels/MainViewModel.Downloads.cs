using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstagramStoryViewer.Api;
using InstagramStoryViewer.Diagnostics;
using InstagramStoryViewer.Gui.Services;
using InstagramStoryViewer.Services;

namespace InstagramStoryViewer.Gui.ViewModels;

/// <summary>Downloading stories and the downloads page.</summary>
public sealed partial class MainViewModel
{
    // ================================================================== downloads

    [RelayCommand]
    private async Task DownloadStoryAsync(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row is null) return;
        await DownloadRowAsync(row);
        RefreshDownloads();
    }

    [RelayCommand(CanExecute = nameof(HasCurrentUser))]
    private async Task DownloadAllAsync()
    {
        var pending = Stories.Where(s => s.State is DownloadState.None or DownloadState.Failed).ToList();
        if (pending.Count == 0)
        {
            ShowToast("كل الستوريات منزّلة مسبقاً");
            return;
        }
        foreach (var r in pending) r.State = DownloadState.Queued;
        var ok = 0;
        foreach (var r in pending)
            if (await DownloadRowAsync(r)) ok++;
        RefreshDownloads();
        ShowToast($"تم تنزيل {ok} من {pending.Count}", isError: ok < pending.Count);
    }

    private async Task<bool> DownloadRowAsync(StoryRowViewModel row)
    {
        if (_downloader is null || row.State is DownloadState.Downloading or DownloadState.Done) return row.State == DownloadState.Done;
        row.State = DownloadState.Downloading;
        row.Progress = 0;
        row.Error = null;
        try
        {
            var progress = new Progress<double>(p => row.Progress = p * 100);
            string path;
            try
            {
                path = await _downloader.DownloadAsync(row.Username, row.Item, _lifetimeCts.Token, progress);
            }
            catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.MediaExpired)
            {
                // The CDN link expired while the list was open: fetch a fresh one and retry once.
                if (!await RefreshRowAsync(row, _lifetimeCts.Token)) throw;
                path = await _downloader.DownloadAsync(row.Username, row.Item, _lifetimeCts.Token, progress);
            }
            row.LocalPath = path;
            row.SizeText = Format.Size(new FileInfo(path).Length);
            row.State = DownloadState.Done;
            AppLog.Info($"Downloaded {row.Username}/{row.Item.Pk} ({row.SizeText})");
            return true;
        }
        catch (OperationCanceledException)
        {
            row.State = DownloadState.None; // cancelled by sign-out / exit, not a failure
            return false;
        }
        catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or IOException
                                       or UnauthorizedAccessException)
        {
            row.State = DownloadState.Failed;
            row.Error = ex.Message;
            AppLog.Warn($"Download failed {row.Username}/{row.Item.Pk}: {ex.Message}");
            ShowToast($"فشل التنزيل: {ex.Message}", isError: true);
            return false;
        }
    }

    private int _downloadsScanVersion;

    /// <summary>Rescans the download folder on a worker thread so large folders do not freeze the window.</summary>
    public async void RefreshDownloads()
    {
        var version = ++_downloadsScanVersion;
        var folder = DownloadFolder;
        try
        {
            var files = await Task.Run(() =>
            {
                if (!Directory.Exists(folder)) return new List<DownloadedFileViewModel>();
                return new DirectoryInfo(folder).EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.Extension is ".jpg" or ".mp4")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new DownloadedFileViewModel(f, folder))
                    .ToList();
            });
            if (version != _downloadsScanVersion) return; // a newer scan superseded this one

            Downloads.Clear();
            foreach (var f in files) Downloads.Add(f);
            DownloadedCount = files.Count;
            DownloadedSize = Format.Size(files.Sum(f => f.SizeBytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"تعذّر قراءة مجلد التنزيل: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Shell(path);
    }

    [RelayCommand]
    private void ShowInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowToast(ex.Message, true); }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(DownloadFolder);
        Shell(DownloadFolder);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var picked = _dialogs.PickFolder(DownloadFolder);
        if (picked is not null) DownloadFolder = picked;
    }

    [RelayCommand]
    private void OpenInBrowser(StoryRowViewModel? row)
    {
        row ??= SelectedStory;
        if (row?.MediaUrl is not null) Shell(row.MediaUrl);
    }
}
