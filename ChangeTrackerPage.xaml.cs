using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiapStash_Plugin
{
    public partial class ChangeTrackerPage : UserControl
    {
        private string _cachedTtsUrl = "ws://localhost:8889/";

        public ChangeTrackerPage()
        {
            this.InitializeComponent();

            // FIXED: Replaced Windows.Storage.ApplicationData container lookups with a raw filesystem fallback.
            // This stops WinRT runtime crashes inside unpackaged desktop environments.
            string defaultTemplate = "Current diaper status is {diapstash_status}. Product in use: {diapstash_product}, size {diapstash_size}. [if:diapstash_leak==YES]Warning: Leak detected. [/if]Wetness level: {diapstash_wetness}, mess level: {diapstash_messy}. Elapsed runtime: {diapstash_elapsed}.";
            string currentTemplate = defaultTemplate;

            try
            {
                string templatePath = Path.Combine(DiapStashClient.AppDataFolder, "saved_template.txt");
                if (File.Exists(templatePath))
                {
                    currentTemplate = File.ReadAllText(templatePath);
                }
                else
                {
                    // Fallback to checking inside credentials configuration file matrix
                    string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                    if (File.Exists(credentialsPath))
                    {
                        string rawCreds = File.ReadAllText(credentialsPath);
                        using var doc = JsonDocument.Parse(rawCreds);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("CustomTtsTemplate", out var templateProp))
                        {
                            currentTemplate = templateProp.GetString() ?? defaultTemplate;
                        }
                    }
                }
            }
            catch { }

            CustomTtsTemplateBox.Text = currentTemplate;

            // Pull custom multi-level rule matrix parameters cleanly matching file fallbacks
            JakeyTtsClient.Instance.LoadRulesFromSettings();
            RulesListView.ItemsSource = JakeyTtsClient.Instance.ComplexRuleCards;
        }

        public async Task RefreshChangeAsync(bool forceRefresh = false)
        {
            string currentToken = string.Empty;
            string currentClientId = string.Empty;
            _cachedTtsUrl = "ws://localhost:8889/";

            // FIXED: Isolated configuration loading pipeline strictly to local disk metrics files
            string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
            if (File.Exists(credentialsPath))
            {
                try
                {
                    string rawCreds = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawCreds);
                    var root = doc.RootElement;
                    currentClientId = DiapStashCredentials.ClientId;
                    currentToken = root.TryGetProperty("AccessToken", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
                    _cachedTtsUrl = root.TryGetProperty("TtsUrl", out var urlProp) ? urlProp.GetString() ?? _cachedTtsUrl : _cachedTtsUrl;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(currentToken) || string.IsNullOrEmpty(currentClientId))
            {
                StatusCardFrame.Visibility = Visibility.Collapsed;
                NoActiveSessionInfoBar.Title = "Authentication Required";
                NoActiveSessionInfoBar.Message = "⚠️ Not logged in. Please verify your Client parameters and execute Portal Login initialization routines.";
                NoActiveSessionInfoBar.Severity = InfoBarSeverity.Error;
                NoActiveSessionInfoBar.IsOpen = true;
                return;
            }

            DiapStashClient.Instance.ConfigureAuthentication(currentToken, currentClientId);
            var statePayload = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync(forceRefresh);

            if (DiapStashClient.Instance.IsRateLimited)
            {
                StatusCardFrame.Visibility = Visibility.Collapsed;
                NoActiveSessionInfoBar.Title = "Rate Limited (429)";
                NoActiveSessionInfoBar.Message = "⚠️ Too many requests! API limit reached. Core endpoint cooldown threshold active. Try again later.";
                NoActiveSessionInfoBar.Severity = InfoBarSeverity.Warning;
                NoActiveSessionInfoBar.IsOpen = true;
                return;
            }

            if (statePayload == null)
            {
                MainWindow.Instance?.Log("⏳ Access token rejected on Changelog layer. Triggering headless renewal sequence...");
                bool refreshSuccess = await DiapStashClient.Instance.RefreshAccessTokenHeadlessAsync();

                if (refreshSuccess)
                {
                    MainWindow.Instance?.Log("✨ Token renewed successfully! Syncing change matrix layouts...");
                    statePayload = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync(forceRefresh = true);
                }
            }

            if (statePayload == null)
            {
                StatusCardFrame.Visibility = Visibility.Collapsed;
                NoActiveSessionInfoBar.Title = "Session Unauthorized";
                NoActiveSessionInfoBar.Message = "❌ API authorization failed. Token context rejected or expired. Execute Portal Login to re-authenticate.";
                NoActiveSessionInfoBar.Severity = InfoBarSeverity.Error;
                NoActiveSessionInfoBar.IsOpen = true;
                return;
            }

            NoActiveSessionInfoBar.IsOpen = false;
            StatusCardFrame.Visibility = Visibility.Visible;

            CardProductName.Text = statePayload.ProductName;
            CardProductSize.Text = $"Size: {statePayload.Size}";
            CardNotesDisplay.Text = string.IsNullOrEmpty(statePayload.Note) ? "No session context notes attached." : statePayload.Note;

            if (statePayload.StartTime != DateTime.MinValue)
            {
                DateTime localStartTime = statePayload.StartTime.ToLocalTime();
                CardStartTimeDisplay.Text = localStartTime.ToString("dd/MM/yyyy HH:mm");

                if (statePayload.IsActiveSession)
                {
                    TimeSpan elapsed = DateTime.Now - localStartTime;
                    CardElapsedTimeDisplay.Text = $"Elapsed: {(int)elapsed.TotalHours}h {elapsed.Minutes}m";
                    CardStatusBadgeText.Text = "ACTIVE";
                    CardStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 147, 112, 219));
                }
                else
                {
                    DateTime localEndTime = statePayload.EndTime.Value.ToLocalTime();
                    TimeSpan duration = localEndTime - localStartTime;
                    CardElapsedTimeDisplay.Text = $"Total Duration: {(int)duration.TotalHours}h {duration.Minutes}m";
                    CardStatusBadgeText.Text = "COMPLETED";
                    CardStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
                }
            }

            if (!string.IsNullOrEmpty(statePayload.ImageUrl) && statePayload.ImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                bool isSvg = false;
                try
                {
                    var uri = new Uri(statePayload.ImageUrl);
                    isSvg = uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
                }
                catch { }

                if (isSvg)
                {
                    CardProductImage.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(statePayload.ImageUrl));
                }
                else
                {
                    CardProductImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(statePayload.ImageUrl));
                }
            }
            else
            {
                CardProductImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("https://diapstash.com/diapstash/assets/icons/Diaper.png"));
            }

            BadgeLeak.Visibility = statePayload.HasLeak ? Visibility.Visible : Visibility.Collapsed;

            if (statePayload.Wetness > 0)
            {
                BadgeWet.Visibility = Visibility.Visible;
                BadgeWetText.Text = $"💧 WET ({statePayload.WetnessDisplay} - {statePayload.WetnessPercentage}%)";
            }
            else { BadgeWet.Visibility = Visibility.Collapsed; }

            if (statePayload.MessyLevel > 0)
            {
                BadgeMessy.Visibility = Visibility.Visible;
                BadgeMessyText.Text = $"💩 MESSY ({statePayload.MessyDisplay} - {statePayload.MessyPercentage}%)";
            }
            else { BadgeMessy.Visibility = Visibility.Collapsed; }

            if (JakeyTtsClient.Instance != null && JakeyTtsClient.Instance.IsConnected)
            {
                _ = Task.Run(async () => await JakeyTtsClient.Instance.SynchronizeJakeyGlobalVariablesAsync(forceRefresh));
            }
        }

        private void RefreshChange_Click(object sender, RoutedEventArgs e) => _ = RefreshChangeAsync(forceRefresh: true);

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            JakeyTtsClient.Instance.ComplexRuleCards.Add(new TtsComplexRuleCard());
        }

        private void DeleteCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TtsComplexRuleCard cardToRemove)
            {
                JakeyTtsClient.Instance.ComplexRuleCards.Remove(cardToRemove);
            }
        }

        private void AddClause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TtsComplexRuleCard targetCard)
            {
                int insertionIndex = Math.Max(0, targetCard.Clauses.Count - 1);

                string logicalOp = "ELSE IF";
                if (insertionIndex > 0)
                {
                    string previousOp = targetCard.Clauses[insertionIndex - 1].LogicalOperator;
                    logicalOp = (previousOp == "IF" || previousOp == "AND") ? "AND" : "ELSE IF";
                }

                var newClause = new TtsClause { LogicalOperator = logicalOp, ParentCard = targetCard };
                targetCard.Clauses.Insert(insertionIndex, newClause);
                targetCard.NotifyAllClausesVisibilityChanged();
            }
        }

        private void DeleteClause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TtsClause clauseToRemove)
            {
                var card = clauseToRemove.ParentCard;
                if (card != null)
                {
                    card.Clauses.Remove(clauseToRemove);
                    card.NotifyAllClausesVisibilityChanged();
                }
            }
        }

        private void MoveClauseUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TtsClause clause)
            {
                var card = clause.ParentCard;
                if (card == null) return;

                int currentIndex = card.Clauses.IndexOf(clause);
                if (currentIndex <= 0) return;

                card.Clauses.RemoveAt(currentIndex);
                card.Clauses.Insert(currentIndex - 1, clause);
                card.NotifyAllClausesVisibilityChanged();
            }
        }

        private void MoveClauseDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TtsClause clause)
            {
                var card = clause.ParentCard;
                if (card == null) return;

                int currentIndex = card.Clauses.IndexOf(clause);
                if (currentIndex >= card.Clauses.Count - 1 || clause.LogicalOperator == "ELSE") return;
                if (currentIndex == card.Clauses.Count - 2) return;

                card.Clauses.RemoveAt(currentIndex);
                card.Clauses.Insert(currentIndex + 1, clause);
                card.NotifyAllClausesVisibilityChanged();
            }
        }

        private void SaveRules_Click(object sender, RoutedEventArgs e)
        {
            JakeyTtsClient.Instance.SaveRulesToSettings();
            MainWindow.Instance?.Log("💾 Persisted multi-level conditional tree matrix hierarchy safely straight onto disk.");
            _ = JakeyTtsClient.Instance.SynchronizeJakeyGlobalVariablesAsync(forceRefresh: true);
        }

        private void SaveTtsTemplate_Click(object sender, RoutedEventArgs e)
        {
            string customText = CustomTtsTemplateBox.Text.Trim();
            if (string.IsNullOrEmpty(customText)) return;

            // FIXED: Cleanly serialize parameters to raw local files to bypass sandboxed app setting containers
            try
            {
                string templatePath = Path.Combine(DiapStashClient.AppDataFolder, "saved_template.txt");
                File.WriteAllText(templatePath, customText);

                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                if (File.Exists(credentialsPath))
                {
                    string rawJson = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;

                    var updatedBackup = new
                    {
                        ClientId = root.TryGetProperty("ClientId", out var ci) ? ci.GetString() : "",
                        ClientSecret = root.TryGetProperty("ClientSecret", out var cs) ? cs.GetString() : "",
                        AccessToken = root.TryGetProperty("AccessToken", out var at) ? at.GetString() : "",
                        RefreshToken = root.TryGetProperty("RefreshToken", out var rt) ? rt.GetString() : "",
                        TtsUrl = root.TryGetProperty("TtsUrl", out var tu) ? tu.GetString() : "ws://localhost:8889/",
                        CustomTtsTemplate = customText
                    };

                    File.WriteAllText(credentialsPath, JsonSerializer.Serialize(updatedBackup, new JsonSerializerOptions { WriteIndented = true }));
                }

                MainWindow.Instance?.Log("💾 Saved custom template context directly to disk configurations.");
                _ = JakeyTtsClient.Instance.SynchronizeJakeyGlobalVariablesAsync(forceRefresh: true);
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.Log($"❌ File IO template persistence threw an error: {ex.Message}");
            }
        }

        private async void InspectRawChange_Click(object sender, RoutedEventArgs e)
        {
            if (RawChangeDiagnosticBox == null) return;
            RawChangeDiagnosticBox.Visibility = Visibility.Visible;
            RawChangeDiagnosticBox.Text = "⌛ Requesting raw timeline frame payload stream...";
            string rawJson = await DiapStashClient.Instance.GetRawEndpointDataAsync("api/v1/history/changes");
            RawChangeDiagnosticBox.Text = rawJson;
        }
    }
}