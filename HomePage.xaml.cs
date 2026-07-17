using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiapStash_Plugin
{
    public partial class HomePage : UserControl
    {
        private LogWindow? _floatingLogWindow;
        private string _cachedTtsUrl = "ws://localhost:8889/";

        public static readonly List<string> LogCacheBacklog = new List<string>();

        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) => {
                _ = CheckPlatformStateAsync();
                await CheckVersionAndShowChangelogAsync();
            };
        }

        private async Task CheckVersionAndShowChangelogAsync()
        {
            try
            {
                string currentVersion = "2.3.0.0";
                string versionFile = Path.Combine(DiapStashClient.AppDataFolder, "last_version.txt");
                string lastSeen = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
                
                if (lastSeen != currentVersion)
                {
                    File.WriteAllText(versionFile, currentVersion);
                    await ShowChangelogDialogAsync();
                }
            }
            catch { }
        }

        private async void OpenChangelog_Click(object sender, RoutedEventArgs e)
        {
            await ShowChangelogDialogAsync();
        }

        private async Task ShowChangelogDialogAsync()
        {
            var stack = new StackPanel { Spacing = 15, Margin = new Thickness(0, 10, 0, 0) };
            
            var currentVersionText = new TextBlock
            {
                Text = "🚀 Major Updates:\n" +
                       "• Optimization: Implemented a local product catalog database and local image caching to heavily reduce external API requests and save data quota.\n" +
                       "• Optimization: Catalog updates are now strictly limited to once every 24 hours, and diaper status checks poll every 15 minutes to preserve network resources.\n" +
                       "• Settings: Added a 'Real-Time Updates on JakeyTTS Trigger' toggle to optionally bypass the 15-minute polling limit down to 1-minute for instant TTS feedback.\n" +
                       "• Optimization: Elapsed time durations are now intelligently calculated locally to reduce redundant server polls.\n" +
                       "• Action Blocks: Added default templates and a 'Restore Defaults' button for quick rule building.\n" +
                       "• Action Blocks: Added a 'Copy Tag' button to advance action blocks to easily port them into JakeyTTS.\n" +
                       "• Canvas Editor: Added a 'Show Seconds' toggle option for the Elapsed Time widget.\n\n" +
                       "🛠 Bug Fixes:\n" +
                       "• Fixed a visual flickering issue on the live canvas preview when streaming real-time elapsed durations.\n" +
                       "• Fixed diaper product images failing to load on the Change Tracker interface when served from the local hard drive cache.\n" +
                       "• Fixed main window and telemetry console title bars not matching your system's dark/light mode preference upon application startup.\n" +
                       "• Translated all source code comments and logic documentation to English.",
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(currentVersionText);

            var pastExpander = new Expander { Header = "View Past Changes (v2.2)", HorizontalAlignment = HorizontalAlignment.Stretch };
            var pastText = new TextBlock
            {
                Text = "🚀 Major Updates:\n" +
                       "• Overlay Editor: Added Progress Rings & Arches! Create circular gauges bound to live data metrics.\n" +
                       "• New Visual Editor: Added alignment options, image custom URL support, precise textboxes for sliders, and better widget properties.\n" +
                       "• Performance: Persistent background disk caching saves quota usage across app restarts.\n" +
                       "• OBS Overlay: Added transition time configurations, image placeholder fixes, and fallback icons.\n" +
                       "• Integration: Rebuilt internal engine orchestration with complete memory footprint reduction.\n\n" +
                       "🛠 Bug Fixes:\n" +
                       "• Resolved the WinRT 0x80073D54 app crash loop when authenticating.\n" +
                       "• Fixed issues with SVG images not loading properly.\n" +
                       "• Stabilized the HTTP overlay streaming server connectivity.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 10)
            };
            pastExpander.Content = pastText;
            stack.Children.Add(pastExpander);

            var dialog = new ContentDialog
            {
                Title = "DiapStash Plugin - What's New v2.3",
                Content = new ScrollViewer { Content = stack },
                CloseButtonText = "Awesome!"
            };
            
            dialog.XamlRoot = this.XamlRoot;
            await dialog.ShowAsync();
        }

        public async Task CheckPlatformStateAsync()
        {
            // FIXED: Stripped all ApplicationData container settings.
            // Pulled configuration tokens strictly out of our local disk credentials file map structure.
            string token = "";

            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                if (File.Exists(credentialsPath))
                {
                    string rawCreds = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawCreds);
                    var root = doc.RootElement;
                    token = root.TryGetProperty("AccessToken", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
                    _cachedTtsUrl = root.TryGetProperty("TtsUrl", out var urlProp) ? urlProp.GetString() ?? _cachedTtsUrl : _cachedTtsUrl;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(token))
            {
                UpdateStatusUi("Authentication token missing. Portal authorization required.", showLogin: true, dotColor: Microsoft.UI.Colors.Yellow);
                AppendLog("⚠️ Platform status: Missing security token context.");
                return;
            }

            DiapStashClient.Instance.ConfigureAuthentication(token, DiapStashCredentials.ClientId);

            if (JakeyTtsClient.Instance.IsConnected)
            {
                UpdateStatusUi("DiapStash Ready. Connected to Platform.", showLogin: false, dotColor: Microsoft.UI.Colors.Green, hideLoading: true);
                AppendLog("✨ Connected to engine passively via background thread orchestration channels.");
                return;
            }

            AppendLog("📡 Verifying token lifetime persistence against secure API infrastructure...");
            var payloadCheck = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync();

            if (DiapStashClient.Instance.IsRateLimited)
            {
                UpdateStatusUi("⚠️ Too many requests! API limit reached. Screen cannot be loaded right now.", showLogin: false, dotColor: Microsoft.UI.Colors.Red, hideLoading: true);
                AppendLog("❌ Platform status: 429 Rate limited. Postponing thread sequence requests.");
                return;
            }

            if (payloadCheck == null)
            {
                AppendLog("⏳ Access token expired or rejected. Attempting automated silent refresh cycle sequence...");

                bool refreshSuccess = await DiapStashClient.Instance.RefreshAccessTokenAsync();
                if (refreshSuccess)
                {
                    AppendLog("✨ Token renewed successfully! Retrying state data synchronization sequence...");
                    payloadCheck = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync();
                }
            }

            if (payloadCheck == null)
            {
                UpdateStatusUi("Session expired (401 Unauthorized). Please Login.", showLogin: true, dotColor: Microsoft.UI.Colors.Red);
                AppendLog("❌ Platform status: Unauthorized API connection context. Awaiting manual login.");
                return;
            }

            UpdateStatusUi("DiapStash Ready. Connected to Platform.", showLogin: false, dotColor: Microsoft.UI.Colors.Green, hideLoading: true);
            AppendLog("✨ Integration bridge authorized. Synchronizing real-time telemetry datasets...");

            await ExecuteTtsConnectionAsync();
        }

        private async Task ExecuteTtsConnectionAsync()
        {
            if (DiapStashClient.Instance.IsRateLimited) return;

            try
            {
                AppendLog($"🔌 Initializing injection loop towards JakeyTTS Core Engine at {_cachedTtsUrl}...");
                await JakeyTtsClient.Instance.StartAsync(_cachedTtsUrl);
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Background connection initialization pipeline aborted: {ex.Message}");
            }
        }

        private void UpdateStatusUi(string message, bool showLogin, Windows.UI.Color dotColor, bool hideLoading = false)
        {
            if (this.DispatcherQueue == null) return;

            this.DispatcherQueue.TryEnqueue(() =>
            {
                StatusMessageLabel.Text = message;
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(dotColor);
                LoginPortalBtn.Visibility = showLogin ? Visibility.Visible : Visibility.Collapsed;

                if (hideLoading) StatusLoadingRing.IsActive = false;
            });
        }

        private void OpenFloatingLog_Click(object sender, RoutedEventArgs e)
        {
            if (_floatingLogWindow == null)
            {
                _floatingLogWindow = new LogWindow();
                _floatingLogWindow.Closed += (s, args) => _floatingLogWindow = null;
            }
            _floatingLogWindow.Activate();
        }

        public void AppendLog(string message)
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";

            lock (LogCacheBacklog)
            {
                LogCacheBacklog.Add(logLine);
                if (LogCacheBacklog.Count > 500) LogCacheBacklog.RemoveAt(0);
            }

            _floatingLogWindow?.AppendLog(message);
            System.Diagnostics.Debug.WriteLine(logLine);
        }



        private void LoginPortal_Click(object sender, RoutedEventArgs e)
        {
            var settingsInstance = MainWindow.Instance?.GetSettingsPageInstance();
            if (settingsInstance != null)
            {
                MainWindow.Instance?.NavigateToPage("Settings");
                settingsInstance.TriggerLogin();
            }
        }
    }
}