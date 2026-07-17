using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace ScreenRecorder
{
    public static class Updater
    {
        public static readonly string CurrentVersion = "2.1.0";
        private const string RepoUrl = "https://api.github.com/repos/awojidejoseph0714-eng/ScreenRecorder/releases/latest";

        public static event Action<double>? DownloadProgressChanged;
        public static event Action<string>? StatusChanged;

        public static async Task CheckForUpdatesAsync(bool silentOnLatest = false)
        {
            try
            {
                StatusChanged?.Invoke("Checking for updates...");
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5); // 5 seconds max timeout to prevent launch hangs
                // GitHub API requires User-Agent header
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenRecorderUpdater");

                var responseString = await client.GetStringAsync(RepoUrl);
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagProp))
                {
                    if (!silentOnLatest)
                    {
                        MessageBox.Show("Failed to retrieve version information from update server.", "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                string latestTag = tagProp.GetString() ?? "v2.1.0";
                string latestVerStr = latestTag.TrimStart('v', 'V');

                // Strip pre-release suffix (e.g., "2.2.0-beta" -> "2.2.0")
                int dashIndex = latestVerStr.IndexOf('-');
                if (dashIndex > 0)
                {
                    latestVerStr = latestVerStr.Substring(0, dashIndex);
                }

                if (!Version.TryParse(latestVerStr, out var latestVersion) || !Version.TryParse(CurrentVersion, out var currentVersion))
                {
                    if (!silentOnLatest)
                    {
                        MessageBox.Show($"Could not parse version strings (Current: {CurrentVersion}, Latest: {latestVerStr}).", "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                if (latestVersion <= currentVersion)
                {
                    if (!silentOnLatest)
                    {
                        MessageBox.Show($"You are running the latest version (v{CurrentVersion}).", "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                // New version found! Query the setup.exe download link from assets
                string? downloadUrl = null;
                if (root.TryGetProperty("assets", out var assetsProp))
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var nameProp))
                        {
                            string? assetName = nameProp.GetString();
                            if (assetName != null && assetName.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                {
                    if (!silentOnLatest)
                    {
                        MessageBox.Show($"Version v{latestVerStr} is available, but no matching installer binary ('ScreenRecorder_Setup.exe') was found.", "Update Info", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }

                // Prompt user to update
                string body = $"A new version (v{latestVerStr}) of Screen Recorder is available. Your current version is v{CurrentVersion}.\n\nWould you like to download and install it now?";
                var result = MessageBox.Show(body, "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await DownloadAndExecuteUpdate(client, downloadUrl);
                }
            }
            catch (Exception ex)
            {
                if (!silentOnLatest)
                {
                    MessageBox.Show($"Error checking for updates: {ex.Message}", "Update Check Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                StatusChanged?.Invoke("");
            }
        }

        private static async Task DownloadAndExecuteUpdate(HttpClient client, string downloadUrl)
        {
            try
            {
                StatusChanged?.Invoke("Downloading setup...");
                string tempFile = Path.Combine(Path.GetTempPath(), "ScreenRecorder_Setup.exe");

                // Download with progress reporting
                using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int read;

                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read);
                            totalRead += read;

                            if (totalBytes.HasValue)
                            {
                                double progress = (double)totalRead / totalBytes.Value * 100.0;
                                DownloadProgressChanged?.Invoke(progress);
                                StatusChanged?.Invoke($"Downloading setup ({progress:F0}%)...");
                            }
                        }
                    }
                }

                StatusChanged?.Invoke("Launching installer...");
                DownloadProgressChanged?.Invoke(100.0);

                // Start installer: Launch via cmd with a 1-second delay so that this process has time to exit
                string cmdArgs = $"/c timeout /t 1 && start \"\" \"{tempFile}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                // Exit immediately so installer doesn't block on files
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Application.Current is App appInstance)
                    {
                        appInstance.ExitApplication();
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to download update: {ex.Message}", "Update Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
