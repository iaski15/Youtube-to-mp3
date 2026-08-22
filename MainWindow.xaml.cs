using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NAudio.Wave;

namespace YoutubeToMp3;

public partial class MainWindow : Window
{
    private const int BitrateKbps = 192;
    private static readonly string YtDlpPath = Path.Combine(AppContext.BaseDirectory, "Tools", "yt-dlp.exe");

    private CancellationTokenSource? _cts;
    private string? _tempFile;
    private string? _lastOutput;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
    }

    // ---- window chrome ----

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- input helpers ----

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText().Trim();
            if (text.Length > 0) UrlBox.Text = text;
        }
        catch { /* clipboard can be locked, ignore */ }

        UrlBox.Focus();
        UrlBox.CaretIndex = UrlBox.Text.Length;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_busy)
            Convert_Click(sender, new RoutedEventArgs());
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy) return;

        if (e.Data.GetData(DataFormats.StringFormat) is string text && text.Trim().Length > 0)
        {
            UrlBox.Text = text.Trim();
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            UrlBox.Text = files[0];
        }
    }

    // ---- main flow ----

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var url = UrlBox.Text.Trim().Trim('"', '\'');
        if (url.Length == 0)
        {
            SetStatus("Paste a YouTube link first.", StatusKind.Info);
            UrlBox.Focus();
            return;
        }

        if (!url.Contains("://"))
            url = "https://" + url;

        SetBusy(true);
        OpenFolderButton.Visibility = Visibility.Collapsed;
        VideoCard.Visibility = Visibility.Collapsed;
        _lastOutput = null;

        try
        {
            _cts = new CancellationTokenSource();

            SetPhase("Looking up video...");
            SetStatus("Fetching video info...", StatusKind.Info);

            var info = await FetchInfoAsync(url, _cts.Token);
            ShowVideoCard(info);

            var dialog = new SaveFileDialog
            {
                Title = "Save MP3",
                Filter = "MP3 audio|*.mp3",
                FileName = SanitizeFileName(info.Title),
                OverwritePrompt = true,
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                SetStatus("Cancelled.", StatusKind.Info);
                return;
            }

            SetPhase("Downloading audio...");
            SetStatus($"Downloading best audio track ({info.Uploader})...", StatusKind.Info);

            var sourceFile = await DownloadBestAudioAsync(url, _cts.Token);

            SetPhase("Encoding MP3 @ 192 kbps...");
            SetStatus("Encoding MP3...", StatusKind.Info);
            DownloadBar.IsIndeterminate = true;
            await Task.Run(() =>
            {
                using var reader = new MediaFoundationReader(sourceFile);
                MediaFoundationEncoder.EncodeToMp3(reader, dialog.FileName, BitrateKbps * 1000);
            });
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = 100;
            PercentText.Text = "100%";

            _lastOutput = dialog.FileName;
            SetStatus($"Done! Saved to {dialog.FileName}", StatusKind.Success);
            OpenFolderButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", StatusKind.Info);
        }
        catch (YtDlpException ex)
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        catch (ArgumentException)
        {
            SetStatus("That doesn't look like a valid YouTube link.", StatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message.Length > 200 ? "Something went wrong while converting." : ex.Message, StatusKind.Error);
        }
        finally
        {
            CleanupTemp();
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            SetPhase("");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Cancelling...", StatusKind.Info);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is null || !File.Exists(_lastOutput)) return;

        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"/select,\"{_lastOutput}\""
        });
    }

    // ---- yt-dlp integration ----

    private sealed record VideoInfo(string Title, string Uploader, string? ThumbnailUrl);

    private sealed class YtDlpException(string message) : Exception(message);

    private async Task<VideoInfo> FetchInfoAsync(string url, CancellationToken ct)
    {
        var (_, stdout, stderr) = await RunYtDlpAsync(
            $"--dump-single-json --no-playlist -- \"{url}\"", ct);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            throw new YtDlpException("Couldn't read video info for this link - it may not be a valid YouTube URL.");
        }

        using (doc)
        {
            var root = doc.RootElement.Clone();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("entries", out var entries) &&
                entries.ValueKind == JsonValueKind.Array &&
                entries.GetArrayLength() > 0)
            {
                root = entries[0].Clone();
            }

            return new VideoInfo(
                root.TryGetProperty("title", out var t) ? t.GetString() ?? "video" : "video",
                root.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" : "",
                root.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.String
                    ? th.GetString()
                    : null);
        }
    }

    private async Task<string> DownloadBestAudioAsync(string url, CancellationToken ct)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"ytmp3_{Guid.NewGuid():N}");
        _tempFile = basePath + ".part";

        var args =
            $"-f bestaudio/best -N 4 --no-playlist --newline " +
            $"-o \"{basePath}.%(ext)s\" -- \"{url}\"";

        var (_, _, stderr) = await RunYtDlpAsync(args, ct, OnDownloadLine);

        var dir = Path.GetDirectoryName(basePath)!;
        var pattern = Path.GetFileName(basePath) + ".*";
        var produced = Directory.GetFiles(dir, pattern)
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (produced is null)
            throw MapYtDlpError(stderr.Length > 0 ? stderr : "Download produced no file.");

        _tempFile = produced;
        return produced;
    }

    private static readonly Regex PercentRegex = new(@"(\d+(?:\.\d+)?)%", RegexOptions.Compiled);

    private void OnDownloadLine(string line)
    {
        if (!line.StartsWith("[download]", StringComparison.Ordinal)) return;

        var match = PercentRegex.Match(line);
        if (!match.Success) return;

        if (double.TryParse(match.Groups[1].Value, out var pct))
        {
            Dispatcher.Invoke(() =>
            {
                DownloadBar.Value = Math.Clamp(pct, 0, 100);
                PercentText.Text = $"{pct:0}%";
            });
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunYtDlpAsync(
        string arguments, CancellationToken ct, Action<string>? onStdoutLine = null)
    {
        if (!File.Exists(YtDlpPath))
            throw new YtDlpException("yt-dlp.exe is missing from the Tools folder next to the app.");

        var psi = new ProcessStartInfo(YtDlpPath)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.ErrorDataReceived += (_, a) =>
        {
            if (a.Data is null) return;
            lock (stderr) stderr.AppendLine(a.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            using (ct.Register(() =>
                   {
                       try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                   }))
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    lock (stdout) stdout.AppendLine(line);
                    onStdoutLine?.Invoke(line);
                }

                await process.WaitForExitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        ct.ThrowIfCancellationRequested();

        string errText;
        lock (stderr) errText = stderr.ToString();

        string outText;
        lock (stdout) outText = stdout.ToString();

        if (process.ExitCode != 0)
            throw MapYtDlpError(errText);

        if (string.IsNullOrWhiteSpace(outText))
            throw MapYtDlpError(errText.Length > 0 ? errText : "yt-dlp returned no data.");

        return (process.ExitCode, outText, errText);
    }

    private static YtDlpException MapYtDlpError(string stderr)
    {
        var s = stderr.ToLowerInvariant();

        if (s.Contains("private video"))
            return new YtDlpException("That video is private.");
        if (s.Contains("members-only") || s.Contains("join this channel"))
            return new YtDlpException("That video is members-only.");
        if (s.Contains("age") && (s.Contains("restrict") || s.Contains("confirm")))
            return new YtDlpException("That video is age-restricted and needs sign-in.");
        if (s.Contains("sign in") || s.Contains("bot"))
            return new YtDlpException("YouTube is asking for a bot-check on this one. Updating Tools\\yt-dlp.exe usually fixes it.");
        if (s.Contains("unavailable"))
            return new YtDlpException("That video is unavailable (deleted or region-locked).");
        if (s.Contains("unsupported url"))
            return new YtDlpException("That doesn't look like a supported YouTube link.");
        if (s.Contains("429") || s.Contains("too many requests"))
            return new YtDlpException("YouTube is rate-limiting this connection. Wait a bit and try again.");
        if (s.Contains("is not a valid url") || s.Contains("invalid url"))
            return new YtDlpException("That doesn't look like a valid YouTube link.");

        var errorLine = stderr.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));

        var detail = errorLine is null
            ? "yt-dlp failed."
            : errorLine.Length > 180 ? errorLine[..180] : errorLine;

        return new YtDlpException(detail);
    }

    // ---- ui state ----

    private void ShowVideoCard(VideoInfo info)
    {
        VideoTitle.Text = info.Title;
        VideoAuthor.Text = info.Uploader;
        ThumbBrush.ImageSource = null;
        VideoCard.Visibility = Visibility.Visible;
        if (info.ThumbnailUrl is not null)
            _ = LoadThumbnailAsync(info.ThumbnailUrl);
    }

    private async Task LoadThumbnailAsync(string url)
    {
        try
        {
            var bytes = await DownloadThumbnailBytesAsync(url);
            using var ms = new MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            ThumbBrush.ImageSource = bmp;
        }
        catch { /* thumbnail is optional */ }
    }

    private async Task<byte[]> DownloadThumbnailBytesAsync(string url)
    {
        using var client = new System.Net.Http.HttpClient();
        return await client.GetByteArrayAsync(url);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ConvertButton.IsEnabled = !busy;
        PasteButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;
        ConvertButton.Content = busy ? "Working..." : "Convert to MP3";
        ProgressSection.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = 0;
            PercentText.Text = "";
        }
    }

    private void SetPhase(string text) => PhaseText.Text = text;

    private enum StatusKind { Info, Success, Error }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusText.Foreground = kind switch
        {
            StatusKind.Success => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80)),
            StatusKind.Error => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x70, 0x8A))
        };
    }

    // ---- misc ----

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 80) clean = clean[..80].Trim();
        return clean.Length == 0 ? "youtube-audio" : clean;
    }

    private void CleanupTemp()
    {
        if (_tempFile is null || !File.Exists(_tempFile)) return;
        try { File.Delete(_tempFile); } catch { /* best effort */ }
        _tempFile = null;
    }
}
