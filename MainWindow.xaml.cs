using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace My5
{
    public partial class MainWindow : Window
    {
        private string _tempDownloadFolder;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Allow WPF overlays above WebView2 (future use)
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            // Fully qualify RenderMode to avoid missing type errors
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            // WebView2 user data folder
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "My5_App",
                "WebView2UserData");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);

            // Create temp folder for downloads
            _tempDownloadFolder = Path.Combine(Path.GetTempPath(), "My5Downloads");
            Directory.CreateDirectory(_tempDownloadFolder);

            // Hook navigation
            webView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;

            // Start at My5
            webView.CoreWebView2.Navigate("https://www.channel5.com/");
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Cleanup temp downloads (best-effort)
            try
            {
                if (Directory.Exists(_tempDownloadFolder))
                    Directory.Delete(_tempDownloadFolder, true);
            }
            catch { }
        }

        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                string rawUrl = await webView.CoreWebView2.ExecuteScriptAsync("document.location.href");
                string url = rawUrl.Trim('"');

                if (IsEpisodeUrl(url))
                {
                    // Fire-and-forget the download/play task so navigation isn't blocked
                    _ = HandleEpisodeAsync(url);
                }
            }
            catch
            {
                // ignore navigation errors
            }
        }

        private bool IsEpisodeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.ToLowerInvariant();

            // Tweak these patterns to match Channel 5 episode pages you use
            return url.Contains("channel5.com/show/") ||
                   url.Contains("channel5.com/episode/") ||
                   url.Contains("/episode-");
        }

        private async Task HandleEpisodeAsync(string pageUrl)
        {
            // Show progress UI
            await Dispatcher.InvokeAsync(() =>
            {
                DownloadPanel.Visibility = Visibility.Visible;
                DownloadProgress.Value = 0;
                DownloadText.Text = "Preparing download...";
            });

            try
            {
                // Build output template in temp folder
                string outputTemplate = Path.Combine(_tempDownloadFolder, "%(title)s.%(ext)s");

                // Prepare yt-dlp arguments:
                // -f bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best  -> prefer mp4 and merge if needed
                // -o "outputTemplate"
                // --no-playlist
                // --merge-output-format mp4
                string args = $"-f \"bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best\" --no-playlist --merge-output-format mp4 -o \"{outputTemplate}\" \"{pageUrl}\"";

                // Start yt-dlp process
                var psi = new ProcessStartInfo
                {
                    FileName = "yt-dlp", // must be in PATH
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                await Task.Run(() =>
                {
                    using var proc = Process.Start(psi);
                    if (proc == null)
                        throw new InvalidOperationException("Failed to start yt-dlp process.");

                    // Read stderr to update progress heuristically (yt-dlp prints progress to stderr)
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // yt-dlp progress lines often contain a percentage like " 45.3%"
                        var line = e.Data;
                        var percent = ParsePercentFromYtDlpLine(line);
                        if (percent.HasValue)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                DownloadProgress.Value = percent.Value;
                                DownloadText.Text = $"Downloading: {percent.Value:F0}%";
                            });
                        }
                    };

                    proc.BeginErrorReadLine();
                    proc.WaitForExit();
                });

                // Find the newest file in temp folder
                var files = Directory.GetFiles(_tempDownloadFolder);
                if (files.Length == 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        DownloadText.Text = "No downloaded file found. Playing in browser.";
                    });
                    await Task.Delay(1000);
                    await Dispatcher.InvokeAsync(() => DownloadPanel.Visibility = Visibility.Collapsed);
                    return;
                }

                string downloadedFile = GetMostRecentFile(files);

                // Play the local file in MediaElement
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        webView.Visibility = Visibility.Collapsed;
                        player.Visibility = Visibility.Visible;

                        player.Stop();
                        player.Source = new Uri(downloadedFile);
                        player.LoadedBehavior = System.Windows.Controls.MediaState.Manual;
                        player.UnloadedBehavior = System.Windows.Controls.MediaState.Stop;
                        player.Play();

                        DownloadPanel.Visibility = Visibility.Collapsed;
                    }
                    catch
                    {
                        // fallback: show browser
                        player.Visibility = Visibility.Collapsed;
                        webView.Visibility = Visibility.Visible;
                        DownloadPanel.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadText.Text = "Error during download. Playing in browser.";
                    DownloadPanel.Visibility = Visibility.Collapsed;
                    player.Visibility = Visibility.Collapsed;
                    webView.Visibility = Visibility.Visible;
                });
            }
        }

        private static double? ParsePercentFromYtDlpLine(string line)
        {
            try
            {
                // Look for a pattern like " 45.3%" or "100.0%"
                int p = line.IndexOf('%');
                if (p > 0)
                {
                    // find start of number before %
                    int start = p - 1;
                    while (start >= 0 && (char.IsDigit(line[start]) || line[start] == '.' || line[start] == ' '))
                        start--;
                    start++;
                    string num = line.Substring(start, p - start).Trim();
                    if (double.TryParse(num, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                        return Math.Max(0, Math.Min(100, val));
                }
            }
            catch { }
            return null;
        }

        private static string GetMostRecentFile(string[] files)
        {
            return files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
        }
    }
}
