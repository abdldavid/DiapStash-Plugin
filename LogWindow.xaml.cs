using Microsoft.UI.Xaml;
using System;
using System.Text;

namespace DiapStash_Plugin
{
    public sealed partial class LogWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public LogWindow()
        {
            this.InitializeComponent();
            ExtendsContentIntoTitleBar = false;

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico"));
            }

            bool isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
            if (MainWindow.Instance?.Content is FrameworkElement fe && fe.RequestedTheme != ElementTheme.Default)
            {
                isDark = fe.RequestedTheme == ElementTheme.Dark;
            }
            int isDarkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDarkMode, sizeof(int));

            if (this.Content is FrameworkElement localFe)
            {
                localFe.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
            }

            PopulateBacklog();
        }

        private void PopulateBacklog()
        {
            if (LogBlock == null) return;

            var sb = new StringBuilder();
            lock (HomePage.LogCacheBacklog)
            {
                foreach (var line in HomePage.LogCacheBacklog)
                {
                    sb.AppendLine(line);
                }
            }
            LogBlock.Text = sb.ToString();
            LogScroll?.ChangeView(0, LogScroll.ScrollableHeight, 1);
        }

        public void AppendLog(string message)
        {
            if (this.DispatcherQueue == null) return;

            this.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (LogBlock != null)
                    {
                        LogBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\r\n";
                        LogScroll?.ChangeView(0, LogScroll.ScrollableHeight, 1);
                    }
                }
                catch { }
            });
        }
    }
}