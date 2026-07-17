using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiapStash_Plugin
{
    public sealed partial class MainWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public static MainWindow? Instance { get; private set; }

        private readonly UserControl _homePage = new HomePage();
        private readonly SettingsPage _settingsPage = new SettingsPage();
        private readonly InventoryPage _inventoryPage = new InventoryPage();
        private readonly ChangeTrackerPage _changeTrackerPage = new ChangeTrackerPage();
        private readonly UserControl _streamingPage = new StreamingPage();

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();

            ExtendsContentIntoTitleBar = false;
            SetTheme(ElementTheme.Default);
            NavView.ItemInvoked += NavView_ItemInvokedHandler;

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
                if (File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                }
            }

            JakeyTtsClient.Instance.LogReceived += OnLogReceived;
            this.Closed += MainWindow_Closed;

            NavView.SelectedItem = NavView.MenuItems[0];
            MainContentFrame.Content = _homePage;

            this.Activate();
        }

        public SettingsPage GetSettingsPageInstance() => _settingsPage;
        public HomePage GetHomePageInstance() => (HomePage)_homePage;

        private void NavView_ItemInvokedHandler(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                HandleNavigation("Settings");
                return;
            }

            var item = args.InvokedItemContainer as NavigationViewItem;
            if (item?.Tag != null) HandleNavigation(item.Tag.ToString());
        }

        public void NavigateToPage(string pageTag)
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                foreach (var menuItem in NavView.MenuItems)
                {
                    if (menuItem is NavigationViewItem item && item.Tag?.ToString() == pageTag)
                    {
                        NavView.SelectedItem = item;
                        HandleNavigation(pageTag);
                        break;
                    }
                }
            });
        }

        private void HandleNavigation(string pageTag)
        {
            switch (pageTag)
            {
                case "Home": MainContentFrame.Content = _homePage; break;
                case "Settings": MainContentFrame.Content = _settingsPage; break;
                case "Inventory": MainContentFrame.Content = _inventoryPage; _ = _inventoryPage.RefreshStockAsync(); break;
                case "ChangeTracker": MainContentFrame.Content = _changeTrackerPage; _ = _changeTrackerPage.RefreshChangeAsync(); break;
                case "Streaming": MainContentFrame.Content = _streamingPage; break;
            }
        }

        public void Log(string message) { if (_homePage is HomePage home) home.AppendLog(message); }
        private void OnLogReceived(string message) => Log(message);

        public void SetTheme(ElementTheme theme)
        {
            if (this.Content is FrameworkElement fe)
            {
                fe.RequestedTheme = theme;
            }

            bool isDark = theme == ElementTheme.Dark || (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
            int isDarkMode = isDark ? 1 : 0;
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDarkMode, sizeof(int));
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            NavView.ItemInvoked -= NavView_ItemInvokedHandler;
            JakeyTtsClient.Instance.LogReceived -= OnLogReceived;
            Task.Run(async () => { try { await JakeyTtsClient.Instance.StopAsync(); } catch { } });
            _settingsPage?.ShutdownServer();
        }
    }
}