using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiapStash_Plugin
{
    public partial class SettingsPage : UserControl
    {
        private HttpListener? _oauthListener;
        private readonly HttpClient _tokenHttpClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true,
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
        });

        public SettingsPage()
        {
            this.InitializeComponent();

            string clientId = "";
            string token = "";
            string ttsUrl = "ws://localhost:8889/";

            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                if (File.Exists(credentialsPath))
                {
                    string rawJson = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    token = root.TryGetProperty("AccessToken", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
                    ttsUrl = root.TryGetProperty("TtsUrl", out var urlProp) ? urlProp.GetString() ?? ttsUrl : ttsUrl;
                    if (root.TryGetProperty("ForceRealTimeTTSUpdates", out var rtuProp))
                        RealTimeTtsToggle.IsOn = rtuProp.GetBoolean();
                    else
                        RealTimeTtsToggle.IsOn = false;
                }
            }
            catch { }

            StashTokenBox.Text = token;
            TtsServerBox.Text = ttsUrl;

            // Initialize Theme ComboBox state
            ThemeComboBox.SelectedIndex = 0; // Default to System Default on boot
            if (MainWindow.Instance?.Content is FrameworkElement fe)
            {
                if (fe.RequestedTheme == ElementTheme.Light) ThemeComboBox.SelectedIndex = 1;
                else if (fe.RequestedTheme == ElementTheme.Dark) ThemeComboBox.SelectedIndex = 2;
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                ElementTheme theme = ElementTheme.Default;
                if (tag == "Light") theme = ElementTheme.Light;
                if (tag == "Dark") theme = ElementTheme.Dark;
                
                MainWindow.Instance?.SetTheme(theme);
            }
        }

        private void RealTimeTtsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                string clientId = "", token = "", clientSecret = "", refreshToken = "", customTemplate = "", ttsUrl = "ws://localhost:8889/";

                if (File.Exists(credentialsPath))
                {
                    string rawJson = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    clientId = root.TryGetProperty("ClientId", out var ci) ? ci.GetString() ?? "" : "";
                    clientSecret = root.TryGetProperty("ClientSecret", out var cs) ? cs.GetString() ?? "" : "";
                    token = root.TryGetProperty("AccessToken", out var at) ? at.GetString() ?? "" : "";
                    refreshToken = root.TryGetProperty("RefreshToken", out var rt) ? rt.GetString() ?? "" : "";
                    customTemplate = root.TryGetProperty("CustomTtsTemplate", out var ct) ? ct.GetString() ?? "" : "";
                    ttsUrl = root.TryGetProperty("TtsUrl", out var u) ? u.GetString() ?? ttsUrl : ttsUrl;
                }

                var updatedBackup = new
                {
                    AccessToken = token,
                    RefreshToken = refreshToken,
                    TtsUrl = ttsUrl,
                    CustomTtsTemplate = customTemplate,
                    ForceRealTimeTTSUpdates = RealTimeTtsToggle.IsOn
                };

                File.WriteAllText(credentialsPath, JsonSerializer.Serialize(updatedBackup, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private async void ConnectTts_Click(object sender, RoutedEventArgs e)
        {
            string ttsUrl = TtsServerBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ttsUrl)) return;

            SaveTtsUrl(ttsUrl);

            ConnectTtsBtn.IsEnabled = false;
            TtsServerBox.IsEnabled = false;
            DisconnectTtsBtn.IsEnabled = true;

            await JakeyTtsClient.Instance.StartAsync(ttsUrl);
        }

        private async void DisconnectTts_Click(object sender, RoutedEventArgs e)
        {
            ConnectTtsBtn.IsEnabled = true;
            TtsServerBox.IsEnabled = true;
            DisconnectTtsBtn.IsEnabled = false;

            await JakeyTtsClient.Instance.StopAsync();
            MainWindow.Instance?.Log("🛑 Core connection pipeline closed manually.");
        }

        private void SaveTtsUrl(string ttsUrl)
        {
            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                string clientId = "", token = "", clientSecret = "", refreshToken = "", customTemplate = "";

                if (File.Exists(credentialsPath))
                {
                    string rawJson = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    clientId = root.TryGetProperty("ClientId", out var ci) ? ci.GetString() ?? "" : "";
                    clientSecret = root.TryGetProperty("ClientSecret", out var cs) ? cs.GetString() ?? "" : "";
                    token = root.TryGetProperty("AccessToken", out var at) ? at.GetString() ?? "" : "";
                    refreshToken = root.TryGetProperty("RefreshToken", out var rt) ? rt.GetString() ?? "" : "";
                    customTemplate = root.TryGetProperty("CustomTtsTemplate", out var ct) ? ct.GetString() ?? "" : "";
                }

                var updatedBackup = new
                {
                    AccessToken = token,
                    RefreshToken = refreshToken,
                    TtsUrl = ttsUrl,
                    CustomTtsTemplate = customTemplate,
                    ForceRealTimeTTSUpdates = RealTimeTtsToggle.IsOn
                };

                File.WriteAllText(credentialsPath, JsonSerializer.Serialize(updatedBackup, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void TriggerLogin(bool forceNewLogin = false)
        {
            if (_oauthListener != null && _oauthListener.IsListening) return;
            // Optionally clear existing tokens to force a fresh login
            if (forceNewLogin)
            {
                StashTokenBox.Text = "";
            }
            OpenPortal_Click(this, new RoutedEventArgs());
        }

        private void ChangeAccount_Click(object sender, RoutedEventArgs e)
        {
            TriggerLogin(forceNewLogin: true);
        }

        private async void OpenPortal_Click(object sender, RoutedEventArgs e)
        {
            string clientId = DiapStashCredentials.ClientId;
            string clientSecret = DiapStashCredentials.ClientSecret;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                MainWindow.Instance?.Log("⚠️ Settings validation mismatch. Client ID or Secret is not configured.");
                return;
            }

            AuthBtn.IsEnabled = false;
            ChangeAccountBtn.IsEnabled = false;
            MainWindow.Instance?.Log("🌐 Initializing loopback server on http://localhost:8888/ ...");

            try
            {
                _oauthListener = new HttpListener();
                _oauthListener.Prefixes.Add("http://localhost:8888/");
                _oauthListener.Start();

                string redirectUri = Uri.EscapeDataString("http://localhost:8888/");
                string scope = Uri.EscapeDataString("cloud-sync.stock cloud-sync.history cloud-sync.types offline_access");
                // Added prompt=login to force account selection if needed
                string loginUrl = $"https://account.diapstash.com/oidc/auth?client_id={clientId}&redirect_uri={redirectUri}&response_type=code&scope={scope}&prompt=login";

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(loginUrl) { UseShellExecute = true });

                HttpListenerContext context;
                try
                {
                    context = await _oauthListener.GetContextAsync();
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is HttpListenerException)
                {
                    return;
                }

                if (context.Request.Url?.AbsolutePath.Contains("favicon.ico") == true)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                string error = context.Request.QueryString["error"];
                if (!string.IsNullOrEmpty(error))
                {
                    MainWindow.Instance?.Log($"❌ DiapStash Server rejected authorization request: {error}");
                    
                    string errorHtml = @"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Authentication Failed</title>
    <style>
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background-color: #121212; color: #ffffff; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; }
        .card { background-color: #1e1e1e; border: 1px solid #ff4d4d; border-radius: 12px; padding: 40px; text-align: center; box-shadow: 0 8px 32px rgba(255, 77, 77, 0.2); max-width: 500px; }
        .icon { width: 80px; height: 80px; margin-bottom: 20px; border-radius: 16px; }
        h2 { color: #ff4d4d; margin-top: 0; font-size: 24px; }
        p { color: #a0a0a0; font-size: 15px; line-height: 1.5; margin-bottom: 0; }
        .error-reason { color: #ffb3b3; font-family: monospace; background: rgba(255, 77, 77, 0.1); padding: 10px; border-radius: 6px; margin-top: 15px; }
    </style>
</head>
<body>
    <div class='card'>
        <img class='icon' src='https://raw.githubusercontent.com/abdldavid/DiapStash-Plugin/master/Assets/StoreLogo.scale-400.png' alt='App Icon'>
        <h2>Authentication Failed</h2>
        <p>The authorization request was rejected or cancelled.</p>
        <div class='error-reason'>" + WebUtility.HtmlEncode(error) + @"</div>
    </div>
</body>
</html>";
                    byte[] htmlError = Encoding.UTF8.GetBytes(errorHtml);
                    context.Response.OutputStream.Write(htmlError, 0, htmlError.Length);
                    context.Response.Close();
                    return;
                }

                string code = context.Request.QueryString["code"] ?? "";

                string successHtml = @"
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Link Successful</title>
    <style>
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background-color: #121212; color: #ffffff; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0; }
        .card { background-color: #1e1e1e; border: 1px solid #00BFFF; border-radius: 12px; padding: 40px; text-align: center; box-shadow: 0 8px 32px rgba(0, 191, 255, 0.15); max-width: 500px; }
        .icon { width: 80px; height: 80px; margin-bottom: 20px; border-radius: 16px; }
        h2 { color: #00BFFF; margin-top: 0; font-size: 28px; font-weight: 600; }
        p { color: #a0a0a0; font-size: 16px; line-height: 1.5; margin-bottom: 25px; }
        .btn { background-color: #00BFFF; color: #000; border: none; padding: 10px 24px; border-radius: 6px; font-size: 15px; font-weight: 600; cursor: pointer; text-decoration: none; }
    </style>
</head>
<body>
    <div class='card'>
        <img class='icon' src='https://raw.githubusercontent.com/abdldavid/DiapStash-Plugin/master/Assets/StoreLogo.scale-400.png' alt='App Icon'>
        <h2>Authentication Successful</h2>
        <p>You have successfully linked your DiapStash account. The app should now appear automatically.</p>
        <button class='btn' onclick='window.close();'>Return to App (Close Window)</button>
    </div>
</body>
</html>";
                byte[] htmlFeedback = Encoding.UTF8.GetBytes(successHtml);
                context.Response.OutputStream.Write(htmlFeedback, 0, htmlFeedback.Length);

                await context.Response.OutputStream.FlushAsync();
                context.Response.Close();

                if (_oauthListener != null && _oauthListener.IsListening)
                {
                    _oauthListener.Stop();
                }

                if (!string.IsNullOrEmpty(code))
                {
                    MainWindow.Instance?.Log("📡 Code retrieved. Redeeming security payload for Access Tokens...");
                    await ExchangeCodeForTokenAsync(code, clientId, clientSecret);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.Log($"❌ Auth Handshake Exception: {ex.Message}");
            }
            finally
            {
                ShutdownServer();
                AuthBtn.IsEnabled = true;
                ChangeAccountBtn.IsEnabled = true;
            }
        }

        private async Task ExchangeCodeForTokenAsync(string code, string clientId, string clientSecret)
        {
            try
            {
                var bodyParams = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "redirect_uri", "http://localhost:8888/" }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://account.diapstash.com/oidc/token");
                request.Content = new FormUrlEncodedContent(bodyParams);

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("User-Agent", "JakeyTTS-DiapStash-Plugin/1.0.0 (WinUI3; .NET)");

                string rawCredentials = $"{clientId}:{clientSecret}";
                string base64Credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Credentials);

                using var response = await _tokenHttpClient.SendAsync(request);
                string rawResponseText = await response.Content.ReadAsStringAsync();

                if (rawResponseText.Trim().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                    rawResponseText.Trim().StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                    !response.IsSuccessStatusCode)
                {
                    return;
                }

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(rawResponseText);
                    string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
                    string refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rfProp) ? rfProp.GetString() ?? "" : "";

                    DiapStashClient.Instance.ConfigureAuthentication(accessToken, clientId);

                    try
                    {
                        string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                        string existingTtsUrl = "ws://localhost:8889/";
                        string existingTemplate = "";

                        if (File.Exists(credentialsPath))
                        {
                            try
                            {
                                string existingRaw = File.ReadAllText(credentialsPath);
                                using var existingDoc = JsonDocument.Parse(existingRaw);
                                var r = existingDoc.RootElement;
                                existingTtsUrl = r.TryGetProperty("TtsUrl", out var urlProp) ? urlProp.GetString() ?? existingTtsUrl : existingTtsUrl;
                                existingTemplate = r.TryGetProperty("CustomTtsTemplate", out var tmpProp) ? tmpProp.GetString() ?? existingTemplate : existingTemplate;
                            }
                            catch { }
                        }

                        var credentialBackup = new
                        {
                            AccessToken = accessToken,
                            RefreshToken = refreshToken,
                            TtsUrl = existingTtsUrl,
                            CustomTtsTemplate = existingTemplate,
                            ForceRealTimeTTSUpdates = RealTimeTtsToggle.IsOn
                        };
                        File.WriteAllText(credentialsPath, JsonSerializer.Serialize(credentialBackup, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }

                    if (this.DispatcherQueue != null)
                    {
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            StashTokenBox.Text = accessToken;
                            MainWindow.Instance?.NavigateToPage("Home");
                            _ = MainWindow.Instance?.GetHomePageInstance().CheckPlatformStateAsync();
                            MainWindow.Instance?.Activate();
                        });
                    }

                    MainWindow.Instance?.Log("✨ Handshake finalized. DiapStash active token persistent across future runtime sessions.");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.Log($"❌ HTTP token request execution exception critically thrown: {ex.Message}");
            }
        }

        public async Task<bool> RefreshAccessTokenAsync()
        {
            string clientId = DiapStashCredentials.ClientId;
            string clientSecret = DiapStashCredentials.ClientSecret;
            string refreshToken = "";
            string ttsUrl = "ws://localhost:8889/";
            string template = "";

            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                if (File.Exists(credentialsPath))
                {
                    string rawJson = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    refreshToken = root.TryGetProperty("RefreshToken", out var refProp) ? refProp.GetString() ?? "" : "";
                    ttsUrl = root.TryGetProperty("TtsUrl", out var urlProp) ? urlProp.GetString() ?? ttsUrl : ttsUrl;
                    template = root.TryGetProperty("CustomTtsTemplate", out var tmpProp) ? tmpProp.GetString() ?? template : template;
                }
            }
            catch { return false; }

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refreshToken))
            {
                return false;
            }

            try
            {
                var bodyParams = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refreshToken }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://account.diapstash.com/oidc/token");
                request.Content = new FormUrlEncodedContent(bodyParams);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                string rawCredentials = $"{clientId}:{clientSecret}";
                string base64Credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Credentials);

                using var response = await _tokenHttpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string rawJson = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(rawJson);

                    string newAccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
                    string newRefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rfProp) ? rfProp.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(newRefreshToken))
                    {
                        refreshToken = newRefreshToken;
                    }

                    DiapStashClient.Instance.ConfigureAuthentication(newAccessToken, clientId);

                    try
                    {
                        string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                        var updatedBackup = new
                        {
                            AccessToken = newAccessToken,
                            RefreshToken = refreshToken,
                            TtsUrl = ttsUrl,
                            CustomTtsTemplate = template,
                            ForceRealTimeTTSUpdates = RealTimeTtsToggle.IsOn
                        };
                        File.WriteAllText(credentialsPath, JsonSerializer.Serialize(updatedBackup, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }

                    if (this.DispatcherQueue != null)
                    {
                        this.DispatcherQueue.TryEnqueue(() =>
                        {
                            StashTokenBox.Text = newAccessToken;
                        });
                    }

                    MainWindow.Instance?.Log("🔄 Access token refreshed successfully using background persistent refresh tokens.");
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void ShutdownServer()
        {
            try
            {
                if (_oauthListener != null)
                {
                    if (_oauthListener.IsListening)
                    {
                        _oauthListener.Stop();
                    }
                    _oauthListener.Close();
                    _oauthListener = null;
                }
            }
            catch { }
        }
    }
}
