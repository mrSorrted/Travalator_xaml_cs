using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VHT_CMS_V1
{
    public sealed partial class MainWindow : Window
    {
        private bool _isClosingConfirmed = false; // Flag to prevent infinite dialog loop

        [SupportedOSPlatform("windows10.0.17763.0")]
        public MainWindow()
        {
            this.InitializeComponent();


            // C# code to set AppTitleBar as titlebar
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);

            // Set the preferred height option for the title bar
            this.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            // Set window to full-screen mode
            SetMaximizedWithControls();
            SetDefaultNavigation();
            this.Activate();

            // Add window closing event handler
            this.Closed += MainWindow_Closed;
        }

        // Handle window closing event
        private ContentDialog _activeDialog = null;

        private async void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // *** 1. BYPASS WHEN LOGOUT IS IN PROGRESS ***
            if (LandingScreen.IsLogoutInProgress)
            {
                _isClosingConfirmed = true;

                // Remove this window from WindowManager tracking
                WindowManager.CloseWindow("MainWindow");

                return; // Allow window to close silently
            }

            // *** 2. IF ALREADY CONFIRMED → ALLOW CLOSE ***
            if (_isClosingConfirmed)
                return;

            // *** 3. PREVENT DEFAULT CLOSE TO SHOW DIALOG ***
            args.Handled = true;

            // *** 4. PREVENT MULTIPLE DIALOGS ***
            if (_activeDialog != null)
                return;

            // *** 5. CHECK ACTIVE BLINKING BUTTONS ***
            bool hasBlinkingButtons = BlinkingButtonManager.HasAnyBlinkingButtons();
            int totalBlinkingCount = BlinkingButtonManager.GetTotalBlinkingButtonCount();
            var blinkingByWindow = BlinkingButtonManager.GetBlinkingButtonsByWindow();

            // *** 6. PREPARE DIALOG MESSAGE ***
            string contentMessage;

            if (hasBlinkingButtons)
            {
                string windowDetails = string.Join("\n", blinkingByWindow.Select(kvp =>
                    $"  • {kvp.Key}: {kvp.Value} active button{(kvp.Value > 1 ? "s" : "")}"
                ));

                contentMessage =
                    $"⚠️ Warning: {totalBlinkingCount} control button{(totalBlinkingCount > 1 ? "s are" : " is")} currently active:\n\n" +
                    $"{windowDetails}\n\n" +
                    "You must deactivate ALL blinking control buttons before closing the summary screens.\n\n" +
                    "This prevents potential safety issues and ensures all elevator controls are properly released.";
            }
            else
            {
                contentMessage = "Do you want to close the summary screens?";
            }

            // *** 7. CREATE & STORE DIALOG INSTANCE ***
            _activeDialog = new ContentDialog()
            {
                Title = hasBlinkingButtons ? "Cannot Close - Active Controls Detected" : "Close Summary Screens",
                Content = contentMessage,
                PrimaryButtonText = hasBlinkingButtons ? "OK (Cannot Close)" : "Yes",
                CloseButtonText = hasBlinkingButtons ? "Cancel" : "No",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            // Disable primary button if close is not allowed
            _activeDialog.IsPrimaryButtonEnabled = !hasBlinkingButtons;

            var result = await _activeDialog.ShowAsync();

            // DIALOG DONE → CLEAR REFERENCE
            _activeDialog = null;

            // *** 8. IF USER CONFIRMED CLOSE AND SAFE TO CLOSE ***
            if (result == ContentDialogResult.Primary && !hasBlinkingButtons)
            {
                _isClosingConfirmed = true;

                // Remove window from WindowManager tracking
                WindowManager.CloseWindow("MainWindow");

                // Now close
                this.Close();
            }

            // If user cancelled or controls are active → do nothing
        }
        /// <summary>
        /// Sets the window to maximized state while keeping minimize, maximize, and close buttons visible
        /// </summary>
        private void SetMaximizedWithControls()
        {
            try
            {
                var appWindow = this.AppWindow;
                if (appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    // Keep the title bar and window controls visible
                    presenter.SetBorderAndTitleBar(true, true);

                    // Maximize the window
                    presenter.Maximize();

                    // Ensure window controls are visible
                    this.AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                    this.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                    this.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set maximized mode: {ex.Message}");
            }
        }

        private void SetDefaultNavigation()
        {
            // Wait for the NavigationView to be loaded
            nvSample.Loaded += (s, e) =>
            {
                // Find and select the "Summary Elevators" navigation item
                foreach (var menuItem in nvSample.MenuItems)
                {
                    if (menuItem is NavigationViewItem navItem)
                    {
                        var content = navItem.Content?.ToString();
                        var tag = navItem.Tag?.ToString();

                        if (string.Equals(content, "Summary Elevators", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(tag, "Summary Elevators", StringComparison.OrdinalIgnoreCase))
                        {
                            // Set as selected item (this will make it "lit")
                            nvSample.SelectedItem = navItem;

                            // Navigate to MainWindowEle by default
                            contentFrame.Navigate(typeof(MainWindowEle));

                            // Hide MainScrollViewer if it exists
                            if (MainScrollViewer != null)
                            {
                                MainScrollViewer.Visibility = Visibility.Collapsed;
                            }

                            System.Diagnostics.Debug.WriteLine("Default navigation set to Summary Elevators");
                            break;
                        }
                    }
                }
            };
        }

        private void nvSample_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
                return;

            var item = args.InvokedItemContainer as NavigationViewItem;

            // Fallback if InvokedItemContainer is null
            if (item == null)
            {
                foreach (var mi in sender.MenuItems)
                {
                    if (mi is NavigationViewItem nvi && Equals(nvi.Content, args.InvokedItem))
                    {
                        item = nvi;
                        break;
                    }
                }
            }

            if (item == null)
                return;

            var tag = item.Tag as string;
            var content = item.Content as string;

            if (string.Equals(tag, "Summary Elevators", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(content, "Summary Elevators", StringComparison.OrdinalIgnoreCase))
            {
                contentFrame.Navigate(typeof(MainWindowEle));
                System.Diagnostics.Debug.WriteLine("Navigated to Summary Elevators");
            }
            else if (string.Equals(tag, "Summary Escalators", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(content, "Summary Escalators", StringComparison.OrdinalIgnoreCase))
            {
                contentFrame.Navigate(typeof(MainWindowEsc));
                System.Diagnostics.Debug.WriteLine("Navigated to Summary Escalators");
            }
            else if (string.Equals(tag, "Summary Travelators", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(content, "Summary Travelators", StringComparison.OrdinalIgnoreCase))
            {
                contentFrame.Navigate(typeof(MainWindowTra));
                System.Diagnostics.Debug.WriteLine("Travelators clicked");
            }
            else if (string.Equals(tag, "Summary Elevator NON EPC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(content, "Summary Elevator NON EPC", StringComparison.OrdinalIgnoreCase))
            {
                contentFrame.Navigate(typeof(MainWindowEleN));
                System.Diagnostics.Debug.WriteLine("Elevator NON EPC clicked");
            }
           
        }
    }
}
