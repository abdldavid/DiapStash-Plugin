using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiapStash_Plugin
{
    public class JakeyTtsClient
    {
        private static JakeyTtsClient? _instance;
        public static JakeyTtsClient Instance => _instance ??= new JakeyTtsClient();

        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _connectionLock = new SemaphoreSlim(1, 1);
        private bool _isReconnecting = false;

        public event Action<string>? LogReceived;

        public ObservableCollection<TtsComplexRuleCard> ComplexRuleCards { get; } = new ObservableCollection<TtsComplexRuleCard>();

        public bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

        private JakeyTtsClient() { }

        public void LoadRulesFromSettings()
        {
            try
            {
                string fallbackPath = Path.Combine(DiapStashClient.AppDataFolder, "rules_matrix.json");
                if (File.Exists(fallbackPath))
                {
                    string fileRaw = File.ReadAllText(fallbackPath);
                    ParseRulesJson(fileRaw);
                }
            }
            catch { }
        }

        private void ParseRulesJson(string json)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<TtsComplexRuleCard>>(json);
                ComplexRuleCards.Clear();
                if (list != null)
                {
                    foreach (var card in list)
                    {
                        foreach (var clause in card.Clauses)
                        {
                            clause.ParentCard = card;
                        }
                        ComplexRuleCards.Add(card);
                    }
                }
            }
            catch { }
        }

        public void SaveRulesToSettings()
        {
            try
            {
                var json = JsonSerializer.Serialize(ComplexRuleCards);
                string fallbackPath = Path.Combine(DiapStashClient.AppDataFolder, "rules_matrix.json");
                File.WriteAllText(fallbackPath, json);

                string backupsDir = Path.Combine(DiapStashClient.AppDataFolder, "backups");
                if (!Directory.Exists(backupsDir)) Directory.CreateDirectory(backupsDir);
                string backupPath = Path.Combine(backupsDir, $"rules_matrix_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(backupPath, json);
            }
            catch { }
        }

        private Task<bool> IsPortOpenAsync(Uri uri)
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                var host = uri.Host;
                if (host.ToLower() == "localhost") host = "127.0.0.1";
                
                if (!System.Net.IPAddress.TryParse(host, out var ip))
                {
                    var entry = System.Net.Dns.GetHostEntry(host);
                    ip = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? entry.AddressList.FirstOrDefault();
                }

                if (ip == null) 
                {
                    tcs.TrySetResult(false);
                    return tcs.Task;
                }

                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                var args = new SocketAsyncEventArgs { RemoteEndPoint = new System.Net.IPEndPoint(ip, uri.Port) };

                args.Completed += (s, e) =>
                {
                    bool success = e.SocketError == SocketError.Success;
                    socket.Dispose();
                    args.Dispose();
                    tcs.TrySetResult(success);
                };

                if (!socket.ConnectAsync(args))
                {
                    bool success = args.SocketError == SocketError.Success;
                    socket.Dispose();
                    args.Dispose();
                    tcs.TrySetResult(success);
                }
            }
            catch
            {
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        [System.Diagnostics.DebuggerNonUserCode]
        public async Task StartAsync(string bridgeUrl, bool isAutoReconnect = false)
        {
            if (IsConnected) return;
            if (_isReconnecting && !isAutoReconnect) return;

            await _connectionLock.WaitAsync();
            try
            {
                if (IsConnected) return;

                var targetUri = new Uri(bridgeUrl);

                // FIXED: Pre-flight check to prevent Visual Studio from breaking on HttpRequestException internally.
                if (!await IsPortOpenAsync(targetUri))
                {
                    if (!isAutoReconnect) LogReceived?.Invoke("⚠️ Local engine is offline. Enabling background auto-reconnect.");
                    CleanupSocket();
                    HandleAutomaticRecovery(bridgeUrl);
                    return;
                }

                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();

                if (!isAutoReconnect)
                {
                    LogReceived?.Invoke($"🔌 Attempting connection to JakeyTTS Core Engine at {bridgeUrl}...");
                }

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token))
                {
                    await _webSocket.ConnectAsync(targetUri, linkedCts.Token);
                }

                LogReceived?.Invoke("✅ Network pipe connected! Authorizing version handshake parameters...");
                _isReconnecting = false;
                await SendHandshakeAsync();

                _ = Task.Run(async () => await SynchronizeJakeyGlobalVariablesAsync(forceRefresh: true));
                _ = Task.Run(ReceiveLoopAsync);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is HttpRequestException || ex is SocketException || ex.InnerException is SocketException || ex.InnerException is HttpRequestException)
            {
                if (!isAutoReconnect) LogReceived?.Invoke("⚠️ Local engine at port 8889 is offline. Enabling background auto-reconnect.");
                CleanupSocket();
                HandleAutomaticRecovery(bridgeUrl);
            }
            catch (OperationCanceledException)
            {
                if (!isAutoReconnect) LogReceived?.Invoke("⚠️ Connection attempt timed out. JakeyTTS server might not be running.");
                CleanupSocket();
                HandleAutomaticRecovery(bridgeUrl);
            }
            catch (Exception ex)
            {
                if (!isAutoReconnect) LogReceived?.Invoke($"❌ Connection anomaly: {ex.Message}. Enabling background auto-reconnect.");
                CleanupSocket();
                HandleAutomaticRecovery(bridgeUrl);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                _isReconnecting = false;
                if (_cts != null) _cts.Cancel();

                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing structural plugin context", CancellationToken.None);
                    }
                    catch { }
                }
                CleanupSocket();
                LogReceived?.Invoke("🛑 Core connection interface closed cleanly.");
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private void CleanupSocket()
        {
            try { _webSocket?.Dispose(); } catch { }
            _webSocket = null;
        }

        [System.Diagnostics.DebuggerNonUserCode]
        private void HandleAutomaticRecovery(string bridgeUrl)
        {
            if (_isReconnecting) return;
            _isReconnecting = true;

            Task.Run(async () =>
            {
                LogReceived?.Invoke("⏳ Auto-reconnect thread active. Retrying background connection in 10 seconds...");
                while (_isReconnecting && !IsConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    if (!_isReconnecting) break;
                    await StartAsync(bridgeUrl, isAutoReconnect: true);
                }
                _isReconnecting = false;
            });
        }

        private async Task SendHandshakeAsync()
        {
            string iconBase64String = "";
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                if (File.Exists(iconPath))
                {
                    byte[] iconBytes = await File.ReadAllBytesAsync(iconPath);
                    iconBase64String = Convert.ToBase64String(iconBytes);
                }
            }
            catch { }

            string currentBinPath = "";
            try
            {
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    currentBinPath = currentProcess.MainModule?.FileName ?? "";
                }
            }
            catch { }

            var handshake = new
            {
                type = "register",
                payload = new
                {
                    id = "diapstash-automation-plugin",
                    name = "DiapStash Integration Bridge",
                    version = "1.0.0",
                    protocol_version = "1.0",
                    icon = iconBase64String,
                    executable_path = currentBinPath,
                    launch_invisible = false,
                    subscriptions = new[] { "redeems", "commands" },
                    triggers = new[] { "diapstash_show" }
                }
            };

            await SendJsonAsync(handshake);
        }

        public async Task SynchronizeJakeyGlobalVariablesAsync(bool forceRefresh = false)
        {
            if (!IsConnected) return;

            try
            {
                var state = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync(forceRefresh, true);

                if (state == null)
                {
                    LogReceived?.Invoke("⏳ Access token expired inside background context. Retrying headless refresh...");
                    bool refreshSuccess = await DiapStashClient.Instance.RefreshAccessTokenHeadlessAsync();
                    if (refreshSuccess)
                    {
                        LogReceived?.Invoke("✨ Token successfully renewed. Syncing telemetry channels...");
                        state = await DiapStashClient.Instance.FetchLatestChangeStateObjectAsync(forceRefresh = true, true);
                    }
                }

                if (state == null)
                {
                    LogReceived?.Invoke("❌ Synchronize skipped: Background token evaluation rejected by server.");
                    return;
                }

                try
                {
                    var overlay = OverlayServer.Instance;
                    if (overlay != null)
                    {
                        overlay.LiveProductName = state.ProductName ?? "Standard Diaper Product";
                        overlay.LiveSize = state.Size ?? "N/A";
                        overlay.LiveWetPercentage = state.WetnessPercentage;
                        overlay.LiveMessPercentage = state.MessyPercentage;
                        overlay.LiveStatusMessage = state.IsActiveSession ? "Active" : "Completed";
                        overlay.LiveImageUrl = state.ImageUrl ?? "";
                    }
                }
                catch { }

                string elapsedString = "0 minutes";
                if (state.StartTime != DateTime.MinValue)
                {
                    DateTime localStart = state.StartTime.ToLocalTime();
                    TimeSpan diff = state.IsActiveSession ? (DateTime.Now - localStart) : (state.EndTime.Value.ToLocalTime() - localStart);
                    elapsedString = $"{(int)diff.TotalHours} hours and {diff.Minutes} minutes";
                }

                var dynamicCardTokens = new Dictionary<string, string>();

                foreach (var card in ComplexRuleCards)
                {
                    if (card.Clauses.Count == 0) continue;

                    bool blockEvaluatedAndPassed = false;
                    bool currentChainIsMatch = false;
                    string finalCardPhrase = string.Empty;

                    foreach (var clause in card.Clauses)
                    {
                        if (clause.LogicalOperator == "ELSE")
                        {
                            if (!blockEvaluatedAndPassed)
                            {
                                blockEvaluatedAndPassed = true;
                                finalCardPhrase = clause.OutputMessage ?? string.Empty;
                            }
                            break;
                        }

                        bool clauseIsTrue = false;
                        string actualValue = clause.TargetVariable switch
                        {
                            "Leak" => state.HasLeak ? "YES" : "NO",
                            "Blowout" => (state.HasLeak && state.MessyLevel >= 2) ? "YES" : "NO",
                            "Status" => state.IsActiveSession ? "Active" : "Completed",
                            "Wetness" => state.Wetness.ToString(),
                            "Messy" => state.MessyLevel.ToString(),
                            _ => ""
                        };

                        if (int.TryParse(actualValue, out int currentNum) && int.TryParse(clause.TargetValue, out int targetNum))
                        {
                            clauseIsTrue = clause.ConditionType switch
                            {
                                "Equals" => currentNum == targetNum,
                                "GreaterThan" => currentNum > targetNum,
                                "LessThan" => currentNum < targetNum,
                                _ => false
                            };
                        }
                        else
                        {
                            clauseIsTrue = string.Equals(actualValue, clause.TargetValue, StringComparison.OrdinalIgnoreCase);
                        }

                        if (clause.LogicalOperator == "IF" || clause.LogicalOperator == "ELSE IF")
                        {
                            if (blockEvaluatedAndPassed) continue;
                            currentChainIsMatch = clauseIsTrue;
                        }
                        else if (clause.LogicalOperator == "AND")
                        {
                            if (blockEvaluatedAndPassed) continue;
                            currentChainIsMatch = currentChainIsMatch && clauseIsTrue;
                        }

                        if (clause.IsLastInLogicalBlock && !blockEvaluatedAndPassed)
                        {
                            if (currentChainIsMatch)
                            {
                                blockEvaluatedAndPassed = true;
                                finalCardPhrase = clause.OutputMessage ?? string.Empty;
                            }
                        }
                    }

                    string cleanCardKey = "diapstash_rule_" + card.CardName.Trim().ToLower().Replace(" ", "_");
                    dynamicCardTokens.Add(cleanCardKey, finalCardPhrase.Trim());
                }

                string defaultTemplate = "Current diaper status is {diapstash_status}. Product in use: {diapstash_product}, size {diapstash_size}. [if:diapstash_leak==YES]Warning: Leak detected. [/if]Wetness level: {diapstash_wetness}, mess level: {diapstash_messy}. Elapsed runtime: {diapstash_elapsed}.";
                string userCustomTemplate = defaultTemplate;

                try
                {
                    string templatePath = Path.Combine(DiapStashClient.AppDataFolder, "saved_template.txt");
                    if (File.Exists(templatePath))
                    {
                        userCustomTemplate = File.ReadAllText(templatePath);
                    }
                }
                catch
                {
                    userCustomTemplate = defaultTemplate;
                }

                userCustomTemplate = userCustomTemplate.Replace("{diapstash_product}", state.ProductName);
                userCustomTemplate = userCustomTemplate.Replace("{diapstash_size}", state.Size);
                userCustomTemplate = userCustomTemplate.Replace("{diapstash_wetness}", state.WetnessDisplay);
                userCustomTemplate = userCustomTemplate.Replace("{diapstash_messy}", state.MessyDisplay);
                userCustomTemplate = userCustomTemplate.Replace("{diapstash_elapsed}", elapsedString);
                userCustomTemplate = userCustomTemplate.Replace("{diapstash_leak}", state.HasLeak ? "YES" : "NO");
                userCustomTemplate = userCustomTemplate.Replace("{diapstash_status}", state.IsActiveSession ? "Active" : "Completed");

                foreach (var token in dynamicCardTokens)
                {
                    userCustomTemplate = userCustomTemplate.Replace("{" + token.Key + "}", token.Value);
                }

                await UpdateGlobalVariableAsync("diapstash_change", userCustomTemplate);

                foreach (var token in dynamicCardTokens)
                {
                    await UpdateGlobalVariableAsync(token.Key, token.Value);
                }

                await UpdateGlobalVariableAsync("diapstash_product", state.ProductName);
                await UpdateGlobalVariableAsync("diapstash_size", state.Size);
                await UpdateGlobalVariableAsync("diapstash_wetness", state.WetnessDisplay);
                await UpdateGlobalVariableAsync("diapstash_wetness_percent", $"{state.WetnessPercentage}%");
                await UpdateGlobalVariableAsync("diapstash_messy", state.MessyDisplay);
                await UpdateGlobalVariableAsync("diapstash_messy_percent", $"{state.MessyPercentage}%");
                await UpdateGlobalVariableAsync("diapstash_elapsed", elapsedString);
                await UpdateGlobalVariableAsync("diapstash_leak", state.HasLeak ? "YES" : "NO");
                await UpdateGlobalVariableAsync("diapstash_status", state.IsActiveSession ? "Active" : "Completed");

                string stockSummary = await DiapStashClient.Instance.FetchCurrentStockSummaryAsync(forceRefresh);
                await UpdateGlobalVariableAsync("diapstash_stock", stockSummary);

                LogReceived?.Invoke("Base variable individualization macros updated successfully.");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"⚠️ Telemetry synchronization error: {ex.Message}. Retrying later.");
            }
        }

        private async Task UpdateGlobalVariableAsync(string key, string value)
        {
            var packet = new
            {
                type = "set_global_variable",
                payload = new
                {
                    variable_name = key,
                    variable_value = value
                }
            };
            await SendJsonAsync(packet);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[1024 * 16];
            string currentTtsUrl = "ws://localhost:8889/";

            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                if (File.Exists(credentialsPath))
                {
                    string rawCreds = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawCreds);
                    var root = doc.RootElement;
                    currentTtsUrl = root.TryGetProperty("TtsUrl", out var urlProp) ? urlProp.GetString() ?? currentTtsUrl : currentTtsUrl;
                }
            }
            catch { }

            try
            {
                while (IsConnected && !_cts!.Token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    string message = Encoding.UTF8.GetString(ms.ToArray());
                    if (string.IsNullOrWhiteSpace(message)) continue;

                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "auth_status")
                    {
                        bool approved = root.GetProperty("approved").GetBoolean();
                        LogReceived?.Invoke(approved ? "✨ Access approved by JakeyTTS!" : "⚠️ Access rejected or pending approval in JakeyTTS.");
                    }
                    else if (type == "error")
                    {
                        string errMsg = root.GetProperty("message").GetString() ?? "Unknown API error";
                        LogReceived?.Invoke($"❌ Server error envelope: {errMsg}");

                        if (errMsg.Contains("version", StringComparison.OrdinalIgnoreCase))
                        {
                            LogReceived?.Invoke("🛑 Incompatible core protocol mismatch. Stopping connection.");
                            _isReconnecting = false;
                            CleanupSocket();
                            return;
                        }
                    }
                    else if (type == "event_broadcast")
                    {
                        string scope = root.TryGetProperty("scope", out var sProp) ? sProp.GetString() ?? "" : "";
                        if (scope == "commands" || scope == "redeems")
                        {
                            _ = Task.Run(async () => await SynchronizeJakeyGlobalVariablesAsync(forceRefresh: false));
                        }
                    }
                    else if (type == "variable_triggered")
                    {
                        string varName = root.TryGetProperty("variable", out var vProp) ? vProp.GetString() ?? "" : "";
                        if (varName.Equals("diapstash_show", StringComparison.OrdinalIgnoreCase))
                        {
                            LogReceived?.Invoke("🎬 JakeyTTS triggered overlay via variable placeholder.");
                            OverlayServer.Instance.ForcePreviewTrigger = true;
                        }
                    }
                    else if (type == "trigger_event")
                    {
                        try
                        {
                            var payload = root.GetProperty("payload");
                            string source = payload.TryGetProperty("source", out var sProp) ? sProp.GetString() ?? "" : "";
                            string name = payload.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                            
                            string triggerName = payload.TryGetProperty("trigger", out var tProp) ? tProp.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(triggerName))
                            {
                                triggerName = payload.TryGetProperty("variable", out var vProp) ? vProp.GetString() ?? "" : "";
                            }

                            _ = Task.Run(async () => await SynchronizeJakeyGlobalVariablesAsync(forceRefresh: true));

                            LogReceived?.Invoke($"🎬 JakeyTTS triggered overlay from {source} '{name}' with trigger '{triggerName}'.");

                            if (triggerName.Equals("diapstash_show", StringComparison.OrdinalIgnoreCase))
                            {
                                OverlayServer.Instance.ForcePreviewTrigger = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogReceived?.Invoke($"⚠️ Error parsing trigger_event payload: {ex.Message}");
                        }
                    }
                }
            }
            // FIXED: Protected the read loop against abrupt socket disconnections or broken pipes during streaming
            catch (WebSocketException wsex)
            {
                LogReceived?.Invoke($"🔌 WebSocket channel dropped: {wsex.Message}");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"🔌 WebSocket connection lost: {ex.Message}");
            }
            finally
            {
                bool dynamicRecoveryNeeded = _isReconnecting == false && _cts?.IsCancellationRequested == false;
                CleanupSocket();

                if (dynamicRecoveryNeeded)
                {
                    HandleAutomaticRecovery(currentTtsUrl);
                }
            }
        }

        private async Task SendJsonAsync(object data)
        {
            if (!IsConnected) return;
            try
            {
                string json = JsonSerializer.Serialize(data);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }
    }
}