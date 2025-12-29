using CommunityToolkit.WinUI.Lottie;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace VHT_CMS_V1
{
    public sealed partial class MainWindowTra : Page
    {
        // Per-container lottie state & cache to avoid redundant reloads (prevents jitter)
        private readonly ConcurrentDictionary<FrameworkElement, PanelLottieState> _lottieStates
            = new ConcurrentDictionary<FrameworkElement, PanelLottieState>();

        private readonly ConcurrentDictionary<string, LottieVisualSource> _lottieCache
            = new ConcurrentDictionary<string, LottieVisualSource>();

        private class PanelLottieState
        {
            public string? Direction;
            public bool Mirror;
            public readonly SemaphoreSlim Lock = new(1, 1);
        }

        public MainWindowTra()
        {
            this.InitializeComponent();
            var vm = TravelatorPanelViewModel.Instance;
            vm.EnsureLoaded();
            this.DataContext = vm;
            //this.DataContext = EscalatorPanelViewModel.Instance;

            // Subscribe to property changes for all escalator panels
            if (this.DataContext is TravelatorPanelViewModel vm2)
            {
                foreach (var panel in vm.EscalatorPanels)
                {
                    panel.PropertyChanged += Panel_PropertyChanged;
                }
                vm.EscalatorPanels.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (LiftPanel panel in e.NewItems)
                            panel.PropertyChanged += Panel_PropertyChanged;
                    }
                    if (e.OldItems != null)
                    {
                        foreach (LiftPanel panel in e.OldItems)
                            panel.PropertyChanged -= Panel_PropertyChanged;
                    }
                };

                // Set Canvas.Left and Canvas.Top for each item after UI is loaded
                this.Loaded += (s, e) =>
                {
                    // Position all travelator panels
                    for (int i = 0; i < RootGrid.Items.Count; i++)
                    {
                        var container = (FrameworkElement)RootGrid.ContainerFromIndex(i);
                        if (container?.DataContext is LiftPanel panel)
                        {
                            Canvas.SetLeft(container, panel.X);
                            Canvas.SetTop(container, panel.Y);
                        }
                    }

                    // --- Force UI refresh for each panel to show current status ---
                    if (this.DataContext is TravelatorPanelViewModel vmLoaded)
                    {
                        foreach (var panel in vmLoaded.EscalatorPanels)
                        {
                            Panel_PropertyChanged(panel, new PropertyChangedEventArgs(nameof(LiftPanel.Status)));
                        }
                    }
                    // -------------------------------------------------------------
                };
            }
        }

        private void Escalatorbtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LiftPanel panel)
            {
                //var win = new TraPanelDetailsControl(panel);
                //win.Activate();
                TraPanelDetailsControl.ShowForPanel(panel);
            }
        }

        /// <summary>
        /// Handles property changes for Escalator Panel objects
        /// </summary>
        private void Panel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LiftPanel.Value) || e.PropertyName == nameof(LiftPanel.Status))
            {
                var panel = sender as LiftPanel;
                if (panel == null)
                    return;

                var container = FindContainerForPanel(panel);
                if (container == null)
                    return;

                var panelImage = FindDescendant<Image>(container, "PanelImage");
                var errorImage = FindDescendant<Image>(container, "ErrorImage");
                var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");

                Debug.WriteLine($"Travelator Panel {panel.Name}: Status = '{panel.Status}', Value = [{string.Join(",", panel.Value)}]");

                // ===== OFFLINE STATUS CHECK =====
                bool isOffline = IsStatusOffline(panel.Status);

                if (isOffline)
                {
                    THandleOfflinePanel(panel, container, panelImage);
                    LoadLottie(container, "Stop", true);
                    return;
                }

                // ===== ONLINE PANEL PROCESSING =====
                RestoreOnlinePanelImage(panelImage, container);

                // Process the raw panel data using DataService
                string[] processed = DataServiceEsc.ProcessSignalData16(panel.Value);

                // --- Error display logic for PanelImage ---
                if (panelImage != null && processed.Length > 20)
                {

                    // --- Custom logic for E107 (Fire) ---
                    if (processed.Any(s => s.Split(',').Select(x => x.Trim()).Contains("E107")))
                    {
                        //string imagePath = "ms-appx:///Assets/WarnImage_6.png";
                        //errorImage.Source = new BitmapImage(imagePath);
                        //errorImage.Visibility = Visibility.Visible;
                        //return;
                        THandle_E107_Fire(panel, container, panelImage);
                        return;
                    }
                    // --- Custom logic for E53 (Seismic) ---
                    if (processed.Any(s => s.Split(',').Select(x => x.Trim()).Contains("E53")))
                    {
                        THandle_E53_Seismic(panel, container, panelImage);
                        return;
                    }

                    // Emergency stop status (E14 or E64)
                    if (processed.Any(s =>
                        s.Split(',').Select(x => x.Trim()).Any(code => code == "E14" || code == "E64")))
                    {
                        THandleE14_Emergency_stop(panel, container, panelImage);
                        return;
                    }

                    // Smoke stop status (E1A or ED1)
                    if (processed.Any(s =>
                        s.Split(',').Select(x => x.Trim()).Any(code => code == "E1A" || code == "ED1")))
                    {
                        THandle_smoke(panel, container, panelImage);
                        return;
                    }

                    // Up direction with passengers
                    if (processed.Any(s => s.Split(',').Select(x => x.Trim()).Contains("up operation with passengers")))
                    {
                        //panelImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/TRAVELATOR_UP.gif"));
                        LoadLottie(container, "Up", true);
                    }

                    //Going down with passengers
                    if (processed.Any(s => s.Split(',').Select(x => x.Trim()).Contains("down operation with passengers")))
                    {
                        //panelImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/Travelator_down_v1.gif"));
                        LoadLottie(container, "Down", true);
                    }



                    //Up operation, down operation, crawling (no passengers)
                    if (processed.Any(s =>
                        s.Split(',').Select(x => x.Trim()).Contains("up operation no passengers") ||
                        s.Split(',').Select(x => x.Trim()).Contains("down operation no passengers") ||
                        s.Split(',').Select(x => x.Trim()).Contains("Crawling (from V6004)") ||
                        s.Split(',').Select(x => x.Trim()).Contains("Intermitted stop (from V6004)") ||
                        s.Split(',').Select(x => x.Trim()).Contains("Ready for operation")))
                    {
                        // panelImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                        LoadLottie(container, "Stop", true);

                    }

                    //Failure status
                    if (processed.Any(s => s.Split(',').Select(x => x.Trim()).Contains("Failure")))
                    {
                        THandle_Failure(panel, container, panelImage);
                        return;
                    }

                    // --- Custom logic for Automatic operation (Starter Key) ---
                    //if (processed.Any(s => s.Split(',').Select(x => x.Trim()).Contains("Automatic operation")))
                    //{
                    //    THandle_Automatic_operation(panel, container, panelImage);
                    //    return;
                    //}

                  

                    // --- General error check (after all specific error checks) ---
                    var knownErrors = new HashSet<string>
                    {
                        "E14", "up operation with passengers", "down operation with passengers",
                        "Failure", "Automatic operation", "E107", "E53",
                        "Ready for operation","Engineer on-site","Local operation","ROIN",
                        "operation possible"
                    };

                    string generalErrorCode = processed.SelectMany(s => s.Split(',').Select(x => x.Trim())).FirstOrDefault(code => code.StartsWith("E", StringComparison.OrdinalIgnoreCase) && !knownErrors.Contains(code));

                    if (!string.IsNullOrEmpty(generalErrorCode))
                    {
                        var errorImageGeneral = FindDescendant<Image>(container, "ErrorImage");
                        var errorImageBorderGeneral = FindDescendant<Border>(container, "ErrorImageBorder");

                        if (errorImageGeneral != null)
                        {
                            errorImageGeneral.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/General_Error.png"));
                            panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                        }

                        if (errorImageBorderGeneral != null)
                        {
                            errorImageBorderGeneral.Visibility = Visibility.Visible;
                        }

                        if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
                        {
                            transform.Y = 0;
                        }

                        Debug.WriteLine($"General error travelator detected: {generalErrorCode}");
                        return;
                    }
                }
            }
        }

        #region Create new methods to handle each error on travelator summary screen 
        private void THandleE14_Emergency_stop(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");
            var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Emergency_stop.jpeg"));
                //panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                LoadLottie(container, "Stop", true);
            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Visible;
            }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                //transform.Y = 0;
            }
        }

        //Smoke

        private void THandle_smoke(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");
            var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/SMOKE.jpeg"));
                //panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                LoadLottie(container, "Stop", true);
            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Visible;
            }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                transform.Y = 0;
            }
        }

        private void THandle_Failure(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");
            var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Safety_trip.png"));
                //panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                LoadLottie(container, "Stop", true);
            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Visible;
            }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                //transform.Y = 0;
            }
        }

        private void THandle_Automatic_operation(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");

            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Starter_key.png"));
                //panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));

            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Visible;
            }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                transform.Y = 0;
            }
        }

        private void THandle_E107_Fire(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");
            var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/WarnImage_6.png"));
                //panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                LoadLottie(container, "Stop", true);
            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Visible;
            }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                // transform.Y = 0;
            }
        }

        private void THandle_E53_Seismic(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");
            var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/WarnImage_2.png"));
                // panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                LoadLottie(container, "Stop", true);
            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Visible;
            }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                //transform.Y = 0;
            }
        }
        private void THandleOfflinePanel(LiftPanel panel, FrameworkElement container, Image? panelImage)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");
            var lottieplayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
            if (errorImage != null)
            {
                errorImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/offline.png"));
                //panelImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Travelator_Stop.png"));
                LoadLottie(container, "Stop", true);
            }

            if (errorImageBorder != null) { errorImageBorder.Visibility = Visibility.Visible; }
            else { errorImageBorder.Visibility = Visibility.Collapsed; }

            if (panelImage != null && panelImage.RenderTransform is TranslateTransform transform)
            {
                //transform.Y = 0;
            }

            //StopAndResetDirectionIndicators(container);
        }

        #endregion


        private void RestoreOnlinePanelImage(Image? panelImage, FrameworkElement container)
        {
            var errorImage = FindDescendant<Image>(container, "ErrorImage");
            var errorImageBorder = FindDescendant<Border>(container, "ErrorImageBorder");

            if (errorImage != null)
            {
                var currentSource = errorImage.Source?.ToString();
                if (currentSource != null && currentSource.Contains("offline.png"))
                {
                    errorImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/offline.png"));
                }
            }

            if (errorImageBorder != null)
            {
                errorImageBorder.Visibility = Visibility.Collapsed;
            }
        }

        private bool IsStatusOffline(string? status)
        {
            if (string.IsNullOrEmpty(status))
                return false;

            if (status == "1" || status.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;

            if (int.TryParse(status, out int statusInt) && statusInt == 1)
                return true;

            return false;
        }

        // Helper methods
        private FrameworkElement? FindContainerForPanel(LiftPanel panel)
        {
            var itemsControl = RootGrid;
            if (itemsControl != null)
            {
                int index = itemsControl.Items.IndexOf(panel);
                if (index >= 0)
                {
                    return (FrameworkElement)itemsControl.ContainerFromIndex(index);
                }
            }
            return null;
        }

        private T? FindDescendant<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T fe && fe.Name == name)
                    return fe;
                var result = FindDescendant<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        private async void LoadLottie(FrameworkElement container, string direction, bool mirror = false)
        {
            try
            {
                if (container == null)
                {
                    Debug.WriteLine("LoadLottie: container is null, skipping.");
                    return;
                }

                var lottiePlayer = FindDescendant<AnimatedVisualPlayer>(container, "LottiePlayer");
                if (lottiePlayer == null)
                {
                    Debug.WriteLine("LoadLottie: LottiePlayer not found in container.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(direction))
                {
                    Debug.WriteLine("LoadLottie: direction is null or empty, skipping animation load.");
                    return;
                }

                // Get per-container state
                var state = _lottieStates.GetOrAdd(container, _ => new PanelLottieState());

                await state.Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    // If same direction and mirror already active and a source exists, skip reload
                    if (!string.IsNullOrEmpty(state.Direction)
                        && string.Equals(state.Direction, direction, StringComparison.OrdinalIgnoreCase)
                        && state.Mirror == mirror
                        && lottiePlayer?.Source != null)
                    {
                        Debug.WriteLine("LoadLottie: same direction and mirror already active — skipping reload.");
                        return;
                    }

                    string fileName = direction.Equals("Up", StringComparison.OrdinalIgnoreCase)
                        ? "TravelatorLR.json"
                        : direction.Equals("Down", StringComparison.OrdinalIgnoreCase)
                            ? "TravelatorRL.json"
                            : direction.Equals("Stop", StringComparison.OrdinalIgnoreCase)
                                ? "Travelator_stop.json"
                                : null;

                    if (fileName == null)
                    {
                        Debug.WriteLine("LoadLottie: No valid direction provided, skipping animation load.");
                        return;
                    }

                    string appDir = AppContext.BaseDirectory;
                    string assetsPath = System.IO.Path.Combine(appDir, "Assets", fileName);

                    if (!File.Exists(assetsPath))
                    {
                        Debug.WriteLine($"LoadLottie: File not found: {assetsPath}");
                        return;
                    }

                    // Load or reuse cached LottieVisualSource (I/O off the UI thread)
                    if (!_lottieCache.TryGetValue(fileName, out var source))
                    {
                        try
                        {
                            using var stream = File.OpenRead(assetsPath);
                            var src = new LottieVisualSource();
                            await src.SetSourceAsync(stream.AsRandomAccessStream());
                            _lottieCache[fileName] = src;
                            source = src;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"LoadLottie: Failed to load Lottie source '{fileName}': {ex.Message}");
                            return;
                        }
                    }

                    var localSource = source;

                    // Apply source and start playback on UI thread
                    var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    if (dq == null)
                        dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

                    if (dq != null)
                    {
                        dq.TryEnqueue(async () =>
                        {
                            try
                            {
                                lottiePlayer.Source = localSource;

                                // Apply mirroring
                                if (mirror)
                                {
                                    lottiePlayer.RenderTransform = new ScaleTransform
                                    {
                                        ScaleX = -1,
                                        ScaleY = 1
                                    };
                                    lottiePlayer.RenderTransformOrigin = new Point(0.5, 0.5);
                                }
                                else
                                {
                                    lottiePlayer.RenderTransform = null;
                                }

                                // Update active tracking BEFORE playing to avoid races
                                state.Direction = direction;
                                state.Mirror = mirror;

                                if (direction.Equals("Stop", StringComparison.OrdinalIgnoreCase))
                                {
                                    lottiePlayer.Pause();
                                    lottiePlayer.SetProgress(0.0);
                                    Debug.WriteLine("LoadLottie: LottiePlayer set to first frame for Stop.");
                                }
                                else
                                {
                                    await lottiePlayer.PlayAsync(0, 1, true);
                                    Debug.WriteLine("LoadLottie: LottiePlayer.PlayAsync called (looping).");
                                }
                            }
                            catch (Exception uiEx)
                            {
                                Debug.WriteLine($"LoadLottie UI: Exception - {uiEx.Message}");
                            }
                        });
                    }
                    else
                    {
                        // No dispatcher available - still update state so subsequent calls behave correctly
                        state.Direction = direction;
                        state.Mirror = mirror;
                        Debug.WriteLine("LoadLottie: Dispatcher not available - updated state but did not start playback.");
                    }
                }
                finally
                {
                    state.Lock.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadLottie: Exception occurred - {ex.Message}");
            }
        }
    }
}


/////////////////////////////////////////////////////////////////////////////////////////////////////////////


  private static readonly Dictionary<string, (double X, double Y)> EscalatorLocations = new()
  {
      { "DPW11", (1380, 220) }, { "DPW9", (190, 160) }, { "DPW13", (360, 160) }, { "DPW14", (530, 160) }, { "DPW12", (870, 650) },
      { "DPW5", (700, 650) }, { "DPW6", (530, 650) }, { "DPW7", (360, 650) }, { "DPW8", (190, 650) },
      { "DPW3", (190, 450) }, { "DPW2", (360, 450) }, { "DPW4", (530, 450) }, { "DPW1", (700, 450) }, { "DPW10", (870, 450) },
      { "IPW1", (1380, 450) }, { "IPW6", (1380, 650) }, { "IPW7", (1550, 650) }
  };
