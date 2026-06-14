using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiapStash_Plugin
{
    public class DiaperStockItem
    {
        public string Name { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public int Left { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public int DiaperTypeId { get; set; }
        public string VariantId { get; set; } = string.Empty;
    }

    public class DiapStashChangeState
    {
        public int Id { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool IsActiveSession => EndTime == null;
        public string Note { get; set; } = string.Empty;
        public string ProductName { get; set; } = "Standard Diaper Product";
        public string Size { get; set; } = "N/A";
        public string VariantId { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public int TypeId { get; set; }
        public int Wetness { get; set; }
        public int MessyLevel { get; set; }
        public bool HasLeak { get; set; }

        public string WetnessDisplay => Wetness > 0 ? $"{Wetness}/5" : "Dry";
        public int WetnessPercentage => Wetness > 0 ? (int)Math.Round((Wetness / 5.0) * 100) : 0;

        public string MessyDisplay => MessyLevel > 0 ? $"{MessyLevel}/3" : "Clean";
        public int MessyPercentage => MessyLevel > 0 ? (int)Math.Round((MessyLevel / 3.0) * 100) : 0;
    }

    public class DiapStashClient
    {
        private static DiapStashClient? _instance;
        public static DiapStashClient Instance => _instance ??= new DiapStashClient();

        public static string AppDataFolder
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiapStashPlugin");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                return folder;
            }
        }

        private readonly HttpClient _httpClient;
        private string _accessToken = string.Empty;
        private string _clientId = string.Empty;

        public bool IsRateLimited { get; private set; }

        private DiapStashChangeState? _cachedChangeState;
        private DateTime _changeStateCacheExpiration = DateTime.MinValue;

        private DateTime _lastChangeStateFetchTime = DateTime.MinValue;

        private List<DiaperStockItem>? _cachedStockItems;
        private DateTime _lastStockItemsFetchTime = DateTime.MinValue;

        private Dictionary<string, (string FullName, string ImageUrl)> _typeMetadataCache = new();

        public class DiskCacheState
        {
            public DateTime LastStockItemsFetchTime { get; set; }
            public List<DiaperStockItem>? CachedStockItems { get; set; }
            public Dictionary<string, MetadataTuple> TypeMetadata { get; set; } = new();
        }

        public class MetadataTuple
        {
            public string FullName { get; set; } = "";
            public string ImageUrl { get; set; } = "";
        }

        private void SaveCacheToDisk()
        {
            try
            {
                var state = new DiskCacheState
                {
                    LastStockItemsFetchTime = _lastStockItemsFetchTime,
                    CachedStockItems = _cachedStockItems,
                    TypeMetadata = _typeMetadataCache.ToDictionary(kvp => kvp.Key, kvp => new MetadataTuple { FullName = kvp.Value.FullName, ImageUrl = kvp.Value.ImageUrl })
                };
                string json = JsonSerializer.Serialize(state);
                File.WriteAllText(Path.Combine(AppDataFolder, "api_cache.json"), json);
            }
            catch { }
        }

        private void LoadCacheFromDisk()
        {
            try
            {
                string path = Path.Combine(AppDataFolder, "api_cache.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var state = JsonSerializer.Deserialize<DiskCacheState>(json);
                    if (state != null)
                    {
                        _lastStockItemsFetchTime = state.LastStockItemsFetchTime;
                        _cachedStockItems = state.CachedStockItems;
                        if (state.TypeMetadata != null)
                        {
                            foreach (var kvp in state.TypeMetadata)
                            {
                                _typeMetadataCache[kvp.Key] = (kvp.Value.FullName, kvp.Value.ImageUrl);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private DiapStashClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true,
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.diapstash.com/"),
                Timeout = TimeSpan.FromSeconds(8)
            };
            LoadCacheFromDisk();
        }

        public void ConfigureAuthentication(string token, string clientId)
        {
            _accessToken = token?.Trim() ?? string.Empty;
            _clientId = clientId?.Trim() ?? string.Empty;
        }

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string relativeUrl)
        {
            var request = new HttpRequestMessage(method, relativeUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("User-Agent", "JakeyTTS-DiapStash-Plugin/1.0.0 (WinUI3; .NET)");

            if (!string.IsNullOrEmpty(_clientId))
            {
                request.Headers.TryAddWithoutValidation("DS-API-CLIENT-ID", _clientId);
            }

            if (!string.IsNullOrEmpty(_accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            }

            return request;
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
                string credentialsPath = Path.Combine(AppDataFolder, "credentials.json");
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

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refreshToken)) return false;

            try
            {
                var bodyParams = new Dictionary<string, string>
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

                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string rawJson = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(rawJson);
                    string newAccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
                    string newRefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rfProp) ? rfProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(newRefreshToken)) refreshToken = newRefreshToken;

                    ConfigureAuthentication(newAccessToken, clientId);

                    try
                    {
                        var updatedBackup = new { AccessToken = newAccessToken, RefreshToken = refreshToken, TtsUrl = ttsUrl, CustomTtsTemplate = template };
                        File.WriteAllText(Path.Combine(AppDataFolder, "credentials.json"), JsonSerializer.Serialize(updatedBackup, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { }
                    return true;
                }
            }
            catch { }
            return false;
        }

        public async Task<string> GetRawEndpointDataAsync(string endpointUrl)
        {
            if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_clientId))
                return "⚠️ Diagnostic Error: Credentials missing. Link account first.";

            try
            {
                using var request = CreateAuthenticatedRequest(HttpMethod.Get, endpointUrl);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if ((int)response.StatusCode == 429)
                {
                    IsRateLimited = true;
                    return "⚠️ Cooldown Active (429): Too many requests. Wait a moment.";
                }

                string rawJson;
                try
                {
                    rawJson = await response.Content.ReadAsStringAsync();
                }
                catch (IOException) { return "{}"; }

                if (!response.IsSuccessStatusCode)
                    return $"❌ HTTP Error {(int)response.StatusCode}: {response.ReasonPhrase}\nDetails: {rawJson}";

                using var jsonDoc = JsonDocument.Parse(rawJson);
                return JsonSerializer.Serialize(jsonDoc, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return "❌ Connection refused by the local integration endpoint.";
            }
            catch (Exception ex) { return $"❌ Network exception: {ex.Message}"; }
        }

        public async Task<List<DiaperStockItem>> FetchCurrentStockItemsAsync(bool forceRefresh = false)
        {
            var allStockItems = new List<DiaperStockItem>();
            if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_clientId)) return allStockItems;

            bool cacheValid = _cachedStockItems != null && (DateTime.Now - _lastStockItemsFetchTime).TotalHours < 1;

            if (cacheValid && (!forceRefresh || (DateTime.Now - _lastStockItemsFetchTime).TotalHours < 1))
            {
                return _cachedStockItems;
            }

            try
            {
                var disposablesTask = ParseStockEndpointCollectionAsync("api/v1/stock/disposables");
                var reusablesTask = ParseStockEndpointCollectionAsync("api/v1/stock/reusables");

                await Task.WhenAll(disposablesTask, reusablesTask);

                allStockItems.AddRange(disposablesTask.Result);
                allStockItems.AddRange(reusablesTask.Result);

                _cachedStockItems = allStockItems;
                _lastStockItemsFetchTime = DateTime.Now;
                SaveCacheToDisk();
                return _cachedStockItems;
            }
            catch { }
            return allStockItems;
        }

        private async Task<List<DiaperStockItem>> ParseStockEndpointCollectionAsync(string targetEndpoint)
        {
            var list = new List<DiaperStockItem>();
            try
            {
                using var request = CreateAuthenticatedRequest(HttpMethod.Get, targetEndpoint);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if ((int)response.StatusCode == 429)
                {
                    IsRateLimited = true;
                    return list;
                }

                if (!response.IsSuccessStatusCode) return list;

                IsRateLimited = false;

                string rawJson;
                try
                {
                    rawJson = await response.Content.ReadAsStringAsync();
                }
                catch (IOException) { return list; }

                using var doc = JsonDocument.Parse(rawJson);

                if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    var processingTasks = new List<Task<DiaperStockItem>>();

                    foreach (var item in dataArray.EnumerateArray())
                    {
                        int diaperTypeId = item.TryGetProperty("diaperTypeId", out var dtProp) ? dtProp.GetInt32() : 0;
                        string variantId = item.TryGetProperty("variantId", out var vProp) && vProp.ValueKind == JsonValueKind.String ? vProp.GetString() ?? "" : "";
                        string size = item.TryGetProperty("size", out var sProp) ? sProp.GetString() ?? "N/A" : "N/A";
                        int itemsLeft = item.TryGetProperty("left", out var leftProp) ? leftProp.GetInt32() : 0;

                        processingTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var metadata = await FetchDiaperTypeMetadataAsync(diaperTypeId, variantId);
                                string cleanName = metadata.FullName;
                                if (cleanName.Contains("http")) cleanName = $"Product #{diaperTypeId}";

                                return new DiaperStockItem
                                {
                                    Name = cleanName,
                                    Size = size,
                                    Left = itemsLeft,
                                    ImageUrl = string.IsNullOrEmpty(metadata.ImageUrl) ? "https://diapstash.com/diapstash/assets/icons/Stack.png" : metadata.ImageUrl,
                                    DiaperTypeId = diaperTypeId,
                                    VariantId = variantId
                                };
                            }
                            catch
                            {
                                return new DiaperStockItem
                                {
                                    Name = $"Product #{diaperTypeId}",
                                    Size = size,
                                    Left = itemsLeft,
                                    ImageUrl = "https://diapstash.com/diapstash/assets/icons/Stack.png",
                                    DiaperTypeId = diaperTypeId,
                                    VariantId = variantId
                                };
                            }
                        }));
                    }

                    var results = await Task.WhenAll(processingTasks);
                    list.AddRange(results);
                }
            }
            // FIXED: Interceptamos errores de rechazo del pool HTTP para evitar crashes de UI
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                System.Diagnostics.Debug.WriteLine("⚠️ [DiapStashClient] Connection refused on stock collection pipeline.");
            }
            catch { }
            return list;
        }

        public async Task<string> FetchCurrentStockSummaryAsync(bool forceRefresh = false)
        {
            var items = await FetchCurrentStockItemsAsync(forceRefresh);
            if (items.Count == 0) return "📦 DiapStash Inventory is currently empty.";

            var sb = new StringBuilder("📦 Current DiapStash Stock: ");
            var details = items.Select(i => $"{i.Name} ({i.Size}): {i.Left} left");
            sb.Append(string.Join(", ", details) + ".");
            return sb.ToString();
        }

        private string SanitizeImageUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            url = System.Text.RegularExpressions.Regex.Replace(url, @"format=[a-zA-Z0-9]+", "format=png", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return url;
        }

        private string ExtractImageUrl(JsonElement element)
        {
            string[] imageFields = { "primaryImage", "frontImage", "backImage", "image", "thumbnail" };
            foreach (var field in imageFields)
            {
                if (element.TryGetProperty(field, out var imgObj))
                {
                    if (imgObj.ValueKind == JsonValueKind.Object && imgObj.TryGetProperty("url", out var urlProp))
                    {
                        string url = urlProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(url)) return SanitizeImageUrl(url);
                    }
                    else if (imgObj.ValueKind == JsonValueKind.String)
                    {
                        string url = imgObj.GetString() ?? "";
                        if (!string.IsNullOrEmpty(url)) return SanitizeImageUrl(url);
                    }
                }
            }
            
            if (element.TryGetProperty("images", out var imagesArray) && imagesArray.ValueKind == JsonValueKind.Array && imagesArray.GetArrayLength() > 0)
            {
                var firstImage = imagesArray[0];
                if (firstImage.ValueKind == JsonValueKind.Object && firstImage.TryGetProperty("url", out var urlProp))
                {
                    return SanitizeImageUrl(urlProp.GetString() ?? "");
                }
                else if (firstImage.ValueKind == JsonValueKind.String)
                {
                    return SanitizeImageUrl(firstImage.GetString() ?? "");
                }
            }
            
            return "";
        }

        public async Task<(string FullName, string ImageUrl)> FetchDiaperTypeMetadataAsync(int typeId, string targetVariantId)
        {
            string fallbackName = $"Variant #{typeId}";

            if (typeId <= 0) return (fallbackName, "");

            string cacheKey = $"{typeId}_{targetVariantId}";
            lock (_typeMetadataCache)
            {
                if (_typeMetadataCache.ContainsKey(cacheKey)) return _typeMetadataCache[cacheKey];
            }

            try
            {
                using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"api/v1/type/types/{typeId}");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if ((int)response.StatusCode == 429)
                {
                    IsRateLimited = true;
                    return (fallbackName, "");
                }

                if (!response.IsSuccessStatusCode) return (fallbackName, "");

                string rawJson;
                try
                {
                    rawJson = await response.Content.ReadAsStringAsync();
                }
                catch (IOException) { return (fallbackName, ""); }

                using var doc = JsonDocument.Parse(rawJson);

                if (!doc.RootElement.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.Object)
                {
                    return (fallbackName, "");
                }

                string brand = typeNode.TryGetProperty("brand_code", out var bProp) ? bProp.GetString() ?? "" : "";
                string modelName = typeNode.TryGetProperty("name", out var mProp) ? mProp.GetString() ?? "Standard Product" : "Standard Product";

                string fullProductName = string.IsNullOrEmpty(brand) ? modelName : $"{brand} {modelName}";
                string remoteCdnImageUrl = ExtractImageUrl(typeNode);

                if (typeNode.TryGetProperty("variants", out var variantsArray) && variantsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var variant in variantsArray.EnumerateArray())
                    {
                        string currentVariantId = variant.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

                        if (string.Equals(currentVariantId, targetVariantId, StringComparison.OrdinalIgnoreCase))
                        {
                            string colorName = variant.TryGetProperty("name", out var cProp) ? cProp.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(colorName)) fullProductName += $" ({colorName})";

                            string variantImageUrl = ExtractImageUrl(variant);
                            if (!string.IsNullOrEmpty(variantImageUrl))
                            {
                                remoteCdnImageUrl = variantImageUrl;
                            }
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(remoteCdnImageUrl))
                {
                    remoteCdnImageUrl = "";
                }

                var resolvedMetadata = (fullProductName, remoteCdnImageUrl);
                lock (_typeMetadataCache) { _typeMetadataCache[cacheKey] = resolvedMetadata; }
                SaveCacheToDisk();
                return resolvedMetadata;
            }
            catch (IOException ioEx)
            {
                System.Diagnostics.Debug.WriteLine($"ℹ️ Ignored redundant network socket abort exception: {ioEx.Message}");
                return (fallbackName, "");
            }
            catch (HttpRequestException httpEx)
            {
                System.Diagnostics.Debug.WriteLine($"ℹ️ Ignored concurrent HTTP pipeline interruption: {httpEx.Message}");
                return (fallbackName, "");
            }
            catch { return (fallbackName, ""); }
        }

        public DiapStashChangeState? GetCachedChangeState() => _cachedChangeState;

        public async Task<DiapStashChangeState?> FetchLatestChangeStateObjectAsync(bool forceRefresh = false, bool isJakeyTTS = false)
        {
            if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_clientId)) return null;

            bool isCacheValid = _cachedChangeState != null;
            
            if (isCacheValid)
            {
                if (isJakeyTTS && (DateTime.Now - _lastChangeStateFetchTime).TotalMinutes < 3)
                {
                    return _cachedChangeState;
                }
                
                if (forceRefresh && (DateTime.Now - _lastChangeStateFetchTime).TotalMinutes < 1)
                {
                    return _cachedChangeState;
                }

                if (!forceRefresh && !isJakeyTTS && (DateTime.Now - _lastChangeStateFetchTime).TotalMinutes < 1)
                {
                    return _cachedChangeState;
                }
            }

            try
            {
                string paginatedUrl = "api/v1/history/changes?page=0&size=10&sort=startTime,DESC";

                using var request = CreateAuthenticatedRequest(HttpMethod.Get, paginatedUrl);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if ((int)response.StatusCode == 429)
                {
                    IsRateLimited = true;
                    return new DiapStashChangeState { ProductName = "⚠️ Cooldown Active (429)", Note = "Too many requests. Wait a moment." };
                }

                if (!response.IsSuccessStatusCode) return null;

                IsRateLimited = false;

                string rawJson;
                try
                {
                    rawJson = await response.Content.ReadAsStringAsync();
                }
                catch (IOException) { return null; }

                using var doc = JsonDocument.Parse(rawJson);

                if (!doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array || dataArray.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement latestNode = dataArray[0];
                DateTime maxStartTime = DateTime.MinValue;

                foreach (var node in dataArray.EnumerateArray())
                {
                    if (node.TryGetProperty("startTime", out var stProp) && stProp.ValueKind == JsonValueKind.String)
                    {
                        if (DateTime.TryParse(stProp.GetString(), out DateTime currentStartTime) && currentStartTime > maxStartTime)
                        {
                            maxStartTime = currentStartTime;
                            latestNode = node;
                        }
                    }
                }

                var stateResult = new DiapStashChangeState();

                if (latestNode.TryGetProperty("id", out var idProp)) stateResult.Id = idProp.GetInt32();

                if (latestNode.TryGetProperty("startTime", out var stPropActual) && stPropActual.ValueKind == JsonValueKind.String)
                {
                    if (DateTime.TryParse(stPropActual.GetString(), out DateTime sTime)) stateResult.StartTime = sTime;
                }

                if (latestNode.TryGetProperty("endTime", out var etProp) && etProp.ValueKind == JsonValueKind.String)
                {
                    if (DateTime.TryParse(etProp.GetString(), out DateTime eTime)) stateResult.EndTime = eTime;
                }

                if (latestNode.TryGetProperty("note", out var noteProp) && noteProp.ValueKind == JsonValueKind.String)
                {
                    stateResult.Note = noteProp.GetString() ?? string.Empty;
                }

                if (latestNode.TryGetProperty("leak", out var leakProp))
                {
                    if (leakProp.ValueKind == JsonValueKind.True) stateResult.HasLeak = true;
                    else if (leakProp.ValueKind == JsonValueKind.String) stateResult.HasLeak = !string.IsNullOrEmpty(leakProp.GetString());
                }

                if (latestNode.TryGetProperty("wetness", out var wetProp))
                {
                    if (wetProp.ValueKind == JsonValueKind.Number) stateResult.Wetness = wetProp.GetInt32();
                    else if (wetProp.ValueKind == JsonValueKind.String && int.TryParse(wetProp.GetString(), out int wVal)) stateResult.Wetness = wVal;
                }

                if (latestNode.TryGetProperty("messyLevel", out var messyProp))
                {
                    if (messyProp.ValueKind == JsonValueKind.Number) stateResult.MessyLevel = messyProp.GetInt32();
                    else if (messyProp.ValueKind == JsonValueKind.String && int.TryParse(messyProp.GetString(), out int mVal)) stateResult.MessyLevel = mVal;
                }

                if (latestNode.TryGetProperty("diapers", out var diapersArray) && diapersArray.ValueKind == JsonValueKind.Array && diapersArray.GetArrayLength() > 0)
                {
                    var innerDiaper = diapersArray[0];
                    if (innerDiaper.TryGetProperty("size", out var sizeProp)) stateResult.Size = sizeProp.GetString() ?? "N/A";
                    if (innerDiaper.TryGetProperty("variantId", out var varProp)) stateResult.VariantId = varProp.GetString() ?? string.Empty;
                    if (innerDiaper.TryGetProperty("typeId", out var typeProp)) stateResult.TypeId = typeProp.GetInt32();

                    var metadata = await FetchDiaperTypeMetadataAsync(stateResult.TypeId, stateResult.VariantId);
                    stateResult.ProductName = metadata.FullName;
                    stateResult.ImageUrl = metadata.ImageUrl;
                }

                _cachedChangeState = stateResult;
                _lastChangeStateFetchTime = DateTime.Now;

                return stateResult;
            }
            // FIXED: Interceptamos de forma segura la caída del pool HTTP en la obtención del estado activo
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                System.Diagnostics.Debug.WriteLine("⚠️ [DiapStashClient] Connection refused by endpoint core pipeline on change state evaluation.");
                return new DiapStashChangeState { ProductName = "Core Offline", Note = "Awaiting connection..." };
            }
            catch { return null; }
        }

        public async Task<bool> RefreshAccessTokenHeadlessAsync()
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
            string clientId = settings.Values["SavedClientId"]?.ToString() ?? "";
            string clientSecret = settings.Values["SavedClientSecret"]?.ToString() ?? "";
            string refreshToken = settings.Values["SavedRefreshToken"]?.ToString() ?? "";

            string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
            if (string.IsNullOrEmpty(refreshToken) && File.Exists(credentialsPath))
            {
                try
                {
                    string rawCreds = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawCreds);
                    var root = doc.RootElement;
                    clientId = root.GetProperty("ClientId").GetString() ?? clientId;
                    refreshToken = root.TryGetProperty("RefreshToken", out var rProp) ? rProp.GetString() ?? "" : "";
                    clientSecret = root.TryGetProperty("ClientSecret", out var sProp) ? sProp.GetString() ?? "" : "";
                }
                catch { }
            }

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refreshToken))
                return false;

            try
            {
                var bodyParams = new Dictionary<string, string>
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

                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string rawJson = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(rawJson);

                    string newAccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
                    string newRefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rfProp) ? rfProp.GetString() ?? "" : "";

                    settings.Values["SavedStashToken"] = newAccessToken;
                    if (!string.IsNullOrEmpty(newRefreshToken))
                    {
                        settings.Values["SavedRefreshToken"] = newRefreshToken;
                        refreshToken = newRefreshToken;
                    }

                    ConfigureAuthentication(newAccessToken, clientId);

                    var credentialBackup = new
                    {
                        ClientId = clientId,
                        ClientSecret = clientSecret,
                        AccessToken = newAccessToken,
                        RefreshToken = refreshToken,
                        TtsUrl = "ws://localhost:8889/"
                    };
                    File.WriteAllText(credentialsPath, JsonSerializer.Serialize(credentialBackup));
                    return true;
                }
            }
            // FIXED: Control preventivo para el pool de actualización headless en arranques offline
            catch (HttpRequestException ex) when (ex.InnerException is SocketException sex && sex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                System.Diagnostics.Debug.WriteLine("⚠️ [DiapStashClient] Headless refresh bypassed due to connection refusal.");
            }
            catch { }
            return false;
        }
    }
}