using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace DiapStash_Plugin
{
    public partial class InventoryPage : UserControl
    {
        private List<DiaperStockItem> _masterStockItemsList = new List<DiaperStockItem>();

        public InventoryPage()
        {
            this.InitializeComponent();
        }

        public async Task RefreshStockAsync(bool forceRefresh = false)
        {
            string currentToken = string.Empty;
            string currentClientId = string.Empty;

            try
            {
                string credentialsPath = Path.Combine(DiapStashClient.AppDataFolder, "credentials.json");
                if (File.Exists(credentialsPath))
                {
                    string rawCreds = File.ReadAllText(credentialsPath);
                    using var doc = JsonDocument.Parse(rawCreds);
                    var root = doc.RootElement;
                    currentClientId = DiapStashCredentials.ClientId;
                    currentToken = root.TryGetProperty("AccessToken", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
                }
            }
            catch { }

            if (string.IsNullOrEmpty(currentToken) || string.IsNullOrEmpty(currentClientId))
            {
                InventoryInfoBar.Title = "Authentication Required";
                InventoryInfoBar.Message = "⚠️ Not logged in. Please configure your Client ID and execute loopback authentication via Dashboard or Portal first.";
                InventoryInfoBar.Severity = InfoBarSeverity.Error;
                InventoryInfoBar.IsOpen = true;
                InventoryCardsGrid.ItemsSource = null;
                return;
            }

            DiapStashClient.Instance.ConfigureAuthentication(currentToken, currentClientId);

            try
            {
                var stockItemsList = await DiapStashClient.Instance.FetchCurrentStockItemsAsync(forceRefresh);

                if (this.DispatcherQueue == null) return;

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (InventoryInfoBar == null || InventoryCardsGrid == null) return;

                    if (DiapStashClient.Instance.IsRateLimited)
                    {
                        InventoryInfoBar.Title = "Rate Limited (429)";
                        InventoryInfoBar.Message = "⚠️ Too many requests! API limit reached. Core endpoint cooldown threshold active. Try again later.";
                        InventoryInfoBar.Severity = InfoBarSeverity.Warning;
                        InventoryInfoBar.IsOpen = true;
                        InventoryCardsGrid.ItemsSource = null;
                        return;
                    }

                    if (stockItemsList == null || stockItemsList.Count == 0)
                    {
                        InventoryInfoBar.Title = "Session Unauthorized";
                        InventoryInfoBar.Message = "❌ API authorization failed. Token context rejected, empty or expired. Execute Portal Login to re-authenticate.";
                        InventoryInfoBar.Severity = InfoBarSeverity.Error;
                        InventoryInfoBar.IsOpen = true;
                        InventoryCardsGrid.ItemsSource = null;
                        return;
                    }

                    InventoryInfoBar.IsOpen = false;

                    _masterStockItemsList = stockItemsList;

                    ApplyCurrentInventoryFilters();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Catalog data dispatch processing loop aborted: {ex.Message}");
            }
        }

        private void ApplyCurrentInventoryFilters()
        {
            if (_masterStockItemsList == null || InventoryCardsGrid == null) return;

            bool filterInstockOnly = StockFilterToggle != null && StockFilterToggle.IsOn;

            var query = _masterStockItemsList
                .GroupBy(item => new { item.DiaperTypeId, VariantId = item.VariantId ?? "", Size = item.Size ?? "" })
                .Select(group =>
                {
                    var primaryItem = group.First();

                    return new DiaperStockItem
                    {
                        DiaperTypeId = group.Key.DiaperTypeId,
                        VariantId = group.Key.VariantId,
                        Size = group.Key.Size,
                        Name = primaryItem.Name,
                        Left = group.Sum(i => i.Left),
                        ImageUrl = string.IsNullOrWhiteSpace(primaryItem.ImageUrl) || !primaryItem.ImageUrl.StartsWith("http")
                            ? "https://diapstash.com/diapstash/assets/icons/Diaper.png"
                            : primaryItem.ImageUrl
                    };
                });

            if (filterInstockOnly)
            {
                query = query.Where(item => item.Left > 0);
            }

            InventoryCardsGrid.ItemsSource = query.OrderBy(item => item.Name).ThenBy(item => item.Size).ToList();
        }

        private void StockFilterToggle_Toggled(object sender, RoutedEventArgs e)
        {
            ApplyCurrentInventoryFilters();
        }

        private void RefreshStock_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshStockAsync(forceRefresh: true);
        }

        private async void InspectRawStock_Click(object sender, RoutedEventArgs e)
        {
            if (RawStockDiagnosticBox == null) return;

            RawStockDiagnosticBox.Visibility = Visibility.Visible;
            RawStockDiagnosticBox.Text = "⌛ Requesting raw catalog payloads stream...";
            string rawJson = await DiapStashClient.Instance.GetRawEndpointDataAsync("api/v1/stock/disposables");
            RawStockDiagnosticBox.Text = rawJson;
        }
    }
}