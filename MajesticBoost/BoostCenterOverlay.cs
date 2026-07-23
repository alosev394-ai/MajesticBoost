using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MajesticBoost
{
    internal sealed class BoostBenchmarkRequestEventArgs : EventArgs
    {
        public BoostBenchmarkRequestEventArgs(bool elevate)
        {
            Elevate = elevate;
        }

        public bool Elevate { get; private set; }
    }

    internal sealed class BoostCenterOverlay : Grid
    {
        private enum CenterPage
        {
            Readiness,
            Report,
            Settings
        }

        private sealed class ToggleVisuals
        {
            public SolidColorBrush TrackBrush;
            public TranslateTransform KnobTranslation;
        }

        private sealed class ScrollAnimationProxy : FrameworkElement
        {
            public static readonly DependencyProperty OffsetProperty =
                DependencyProperty.Register(
                    "Offset",
                    typeof(double),
                    typeof(ScrollAnimationProxy),
                    new PropertyMetadata(0.0, OffsetChanged));

            private readonly Action<double> applyOffset;

            public ScrollAnimationProxy(Action<double> apply)
            {
                applyOffset = apply;
            }

            public double Offset
            {
                get { return (double)GetValue(OffsetProperty); }
                set { SetValue(OffsetProperty, value); }
            }

            private static void OffsetChanged(
                DependencyObject sender,
                DependencyPropertyChangedEventArgs args)
            {
                var proxy = sender as ScrollAnimationProxy;
                if (proxy != null && proxy.applyOffset != null)
                {
                    proxy.applyOffset((double)args.NewValue);
                }
            }
        }

        private static readonly Color BackgroundColor = Color.FromRgb(22, 22, 22);
        private static readonly Color SurfaceColor = Color.FromRgb(27, 27, 27);
        private static readonly Color HoverColor = Color.FromRgb(45, 45, 45);
        private static readonly Color ButtonColor = Color.FromRgb(37, 37, 37);
        private static readonly Color BorderColor = Color.FromRgb(56, 56, 56);
        private static readonly Color DividerColor = Color.FromRgb(42, 42, 42);
        private static readonly Color TextColor = Color.FromRgb(244, 244, 244);
        private static readonly Color SecondaryColor = Color.FromRgb(189, 189, 189);
        private static readonly Color MutedColor = Color.FromRgb(142, 142, 142);
        private static readonly Color AccentColor = Color.FromRgb(232, 28, 90);
        private static readonly Color ErrorColor = Color.FromRgb(231, 24, 42);
        private static readonly Color SuccessColor = Color.FromRgb(77, 219, 130);
        private static readonly Color WarningColor = Color.FromRgb(242, 184, 75);

        private readonly FontFamily regularFont;
        private readonly FontFamily semiboldFont;
        private readonly Grid contentRoot;
        private readonly StackPanel pageContent;
        private readonly ScrollViewer pageScroller;
        private readonly ScrollAnimationProxy scrollAnimationProxy;
        private readonly StackPanel footerButtons;
        private TextBlock subtitle;
        private readonly Dictionary<CenterPage, Button> tabButtons =
            new Dictionary<CenterPage, Button>();
        private readonly Dictionary<CenterPage, Border> tabIndicators =
            new Dictionary<CenterPage, Border>();
        private readonly Dictionary<CenterPage, ScaleTransform> tabIndicatorScales =
            new Dictionary<CenterPage, ScaleTransform>();
        private readonly TranslateTransform entranceTranslation;
        private readonly TranslateTransform subtitleTranslation =
            new TranslateTransform();
        private readonly TranslateTransform pageTranslation =
            new TranslateTransform();
        private readonly TranslateTransform footerTranslation =
            new TranslateTransform();

        private CenterPage currentPage;
        private CenterPage renderedPage;
        private BoostPreflightReport preflight;
        private BoostSessionReport sessionReport;
        private BoostCenterSettings settings = new BoostCenterSettings();
        private bool settingsLoading;
        private bool requireBoostDecision;
        private bool benchmarkBusy;
        private bool benchmarkNeedsElevation;
        private int benchmarkPercent;
        private string benchmarkTitle;
        private string benchmarkDetail;
        private Button preferredFocusButton;
        private TextBlock benchmarkNoticeTitleBlock;
        private TextBlock benchmarkNoticeDetailBlock;
        private Border benchmarkProgressFill;
        private Button benchmarkButton;
        private double smoothScrollTarget;
        private int smoothScrollGeneration;
        private bool smoothScrollAnimating;
        private int pageTransitionGeneration;
        private bool pageTransitionAnimating;

        public BoostCenterOverlay(
            FontFamily normalFont,
            FontFamily boldFont)
        {
            regularFont = normalFont ?? new FontFamily("Segoe UI");
            semiboldFont = boldFont ?? regularFont;

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(BackgroundColor);
            Visibility = Visibility.Collapsed;
            Focusable = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetName(this, "Центр Boost");

            entranceTranslation = new TranslateTransform();
            RenderTransform = entranceTranslation;

            contentRoot = new Grid
            {
                Margin = new Thickness(24, 4, 24, 18)
            };
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            Children.Add(contentRoot);

            var header = BuildHeader();
            Grid.SetRow(header, 0);
            contentRoot.Children.Add(header);

            var tabs = BuildTabs();
            Grid.SetRow(tabs, 1);
            contentRoot.Children.Add(tabs);

            pageContent = new StackPanel
            {
                Margin = new Thickness(0, 8, 4, 8),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            pageScroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Content = pageContent,
                RenderTransform = pageTranslation
            };
            pageScroller.Resources[typeof(ScrollBar)] = MakeMajesticVerticalScrollBarStyle();
            scrollAnimationProxy = new ScrollAnimationProxy(
                delegate(double offset) { pageScroller.ScrollToVerticalOffset(offset); });
            pageScroller.PreviewMouseWheel += PageScrollerPreviewMouseWheel;
            pageScroller.PreviewKeyDown += delegate { CancelSmoothMouseWheelScroll(); };
            pageScroller.AddHandler(
                Thumb.DragStartedEvent,
                new DragStartedEventHandler(
                    delegate { CancelSmoothMouseWheelScroll(); }));
            Grid.SetRow(pageScroller, 2);
            contentRoot.Children.Add(pageScroller);

            footerButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = footerTranslation
            };
            Grid.SetRow(footerButtons, 3);
            contentRoot.Children.Add(footerButtons);

            PreviewKeyDown += OverlayPreviewKeyDown;
        }

        public event EventHandler CloseRequested;
        public event EventHandler RefreshRequested;
        public event EventHandler ProceedBoostRequested;
        public event EventHandler RestoreRequested;
        public event EventHandler SettingsChanged;
        public event EventHandler<BoostBenchmarkRequestEventArgs> BenchmarkRequested;

        public bool IsOpen
        {
            get { return Visibility == Visibility.Visible; }
        }

        public bool ConsumesApplicationInput
        {
            get { return IsOpen; }
        }

        public BoostCenterSettings Settings
        {
            get { return settings.Clone(); }
        }

        public void SetSettings(BoostCenterSettings value)
        {
            settings = value == null ? new BoostCenterSettings() : value.Clone();
            if (IsOpen && currentPage == CenterPage.Settings)
            {
                RenderCurrentPage();
            }
        }

        public void SetPreflight(BoostPreflightReport report)
        {
            preflight = report;
            if (IsOpen && currentPage == CenterPage.Readiness)
            {
                RenderCurrentPage();
            }
        }

        public void SetSessionReport(BoostSessionReport report)
        {
            sessionReport = report;
            if (IsOpen && currentPage == CenterPage.Report)
            {
                RenderCurrentPage();
            }
        }

        public void OpenReadiness(bool boostDecision)
        {
            requireBoostDecision = boostDecision;
            Open(CenterPage.Readiness);
        }

        public void OpenReport()
        {
            requireBoostDecision = false;
            Open(CenterPage.Report);
        }

        public void OpenSettings()
        {
            requireBoostDecision = false;
            Open(CenterPage.Settings);
        }

        public void SetBenchmarkProgress(
            string title,
            string detail,
            int percent)
        {
            bool wasBusy = benchmarkBusy;
            benchmarkBusy = true;
            benchmarkNeedsElevation = false;
            benchmarkTitle = title ?? "ТЕСТ ПРОИЗВОДИТЕЛЬНОСТИ";
            benchmarkDetail = detail ?? string.Empty;
            benchmarkPercent = Math.Max(0, Math.Min(100, percent));
            if (IsOpen && currentPage == CenterPage.Report)
            {
                if (!wasBusy ||
                    benchmarkNoticeTitleBlock == null ||
                    benchmarkButton == null)
                {
                    RenderCurrentPage();
                }
                else
                {
                    UpdateBenchmarkProgressVisuals();
                }
            }
        }

        public void SetBenchmarkNeedsElevation(string detail)
        {
            benchmarkBusy = false;
            benchmarkNeedsElevation = true;
            benchmarkTitle = "НУЖНЫ ПРАВА ДЛЯ ЗАМЕРА";
            benchmarkDetail = detail ??
                "Windows разрешает покадровую телеметрию только администратору или участнику Performance Log Users.";
            benchmarkPercent = 0;
            Open(CenterPage.Report);
        }

        public void SetBenchmarkMessage(
            string title,
            string detail,
            bool isError)
        {
            benchmarkBusy = false;
            benchmarkNeedsElevation = false;
            benchmarkTitle = title ?? (isError ? "ЗАМЕР НЕ ВЫПОЛНЕН" : "ЗАМЕР ЗАВЕРШЁН");
            benchmarkDetail = detail ?? string.Empty;
            benchmarkPercent = isError ? -1 : 100;
            Open(CenterPage.Report);
        }

        public void HandleEscape()
        {
            if (benchmarkBusy)
            {
                return;
            }
            Close();
        }

        public void HandleKey(KeyEventArgs e)
        {
            if (!IsOpen || e == null)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                HandleEscape();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 && currentPage == CenterPage.Readiness)
            {
                Raise(RefreshRequested);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab &&
                      (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                bool reverse =
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                int next = ((int)currentPage + (reverse ? 2 : 1)) % 3;
                SwitchPage((CenterPage)next);
                e.Handled = true;
            }
        }

        private Grid BuildHeader()
        {
            var header = new Grid();
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = MakeText(
                "ЦЕНТР BOOST",
                18,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(title, 0);
            header.Children.Add(title);

            subtitle = MakeText(
                "Готовность системы, отчёт сессии и безопасные настройки.",
                10.5,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            subtitle.Margin = new Thickness(0, 4, 0, 0);
            subtitle.RenderTransform = subtitleTranslation;
            AutomationProperties.SetLiveSetting(subtitle, AutomationLiveSetting.Polite);
            Grid.SetRow(subtitle, 1);
            header.Children.Add(subtitle);
            return header;
        }

        private Grid BuildTabs()
        {
            var tabs = new Grid();
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddTab(tabs, CenterPage.Readiness, "ГОТОВНОСТЬ", 0);
            AddTab(tabs, CenterPage.Report, "ОТЧЁТ", 1);
            AddTab(tabs, CenterPage.Settings, "НАСТРОЙКИ", 2);
            return tabs;
        }

        private void AddTab(
            Grid tabs,
            CenterPage page,
            string title,
            int column)
        {
            var host = new Grid();
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
            Grid.SetColumn(host, column);

            var button = new Button
            {
                Content = title,
                FontFamily = semiboldFont,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(MutedColor),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                Template = MakeFlatButtonTemplate(0)
            };
            AutomationProperties.SetName(button, title.ToLowerInvariant());
            button.Click += delegate { SwitchPage(page); };
            Grid.SetRow(button, 0);
            host.Children.Add(button);

            var indicator = new Border
            {
                Height = 2,
                Background = new SolidColorBrush(AccentColor),
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var indicatorScale = new ScaleTransform(0.72, 1);
            indicator.RenderTransform = indicatorScale;
            Grid.SetRow(indicator, 1);
            host.Children.Add(indicator);

            tabButtons[page] = button;
            tabIndicators[page] = indicator;
            tabIndicatorScales[page] = indicatorScale;
            tabs.Children.Add(host);
        }

        private void Open(CenterPage page)
        {
            currentPage = page;
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            RenderCurrentPage();
            UpdateTabs(false);

            if (SystemParameters.ClientAreaAnimation)
            {
                Opacity = 0;
                entranceTranslation.Y = 6;
                BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                entranceTranslation.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            else
            {
                BeginAnimation(OpacityProperty, null);
                entranceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                Opacity = 1;
                entranceTranslation.Y = 0;
            }
            FocusPreferredButton();
        }

        private void Close()
        {
            if (!IsOpen)
            {
                return;
            }
            CancelPageTransitionAnimations();
            CancelSmoothMouseWheelScroll();
            if (!SystemParameters.ClientAreaAnimation)
            {
                FinishClose();
                return;
            }

            IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += delegate { FinishClose(); };
            BeginAnimation(OpacityProperty, fade);
        }

        private void FinishClose()
        {
            BeginAnimation(OpacityProperty, null);
            entranceTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            entranceTranslation.Y = 0;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = true;
            Raise(CloseRequested);
        }

        private void SwitchPage(CenterPage page)
        {
            if (currentPage == page && IsOpen)
            {
                return;
            }

            FinishPageTransitionImmediately();
            CenterPage previousPage = renderedPage;
            currentPage = page;
            requireBoostDecision = false;
            UpdateTabs(true);

            if (!SystemParameters.ClientAreaAnimation)
            {
                RenderCurrentPage();
                FocusPreferredButton();
                return;
            }

            BeginPageTransition(previousPage, page);
        }

        private void UpdateTabs(bool animate)
        {
            foreach (KeyValuePair<CenterPage, Button> pair in tabButtons)
            {
                bool selected = pair.Key == currentPage;
                Color targetColor = selected ? TextColor : MutedColor;
                var foreground = pair.Value.Foreground as SolidColorBrush;
                if (foreground == null)
                {
                    foreground = new SolidColorBrush(targetColor);
                    pair.Value.Foreground = foreground;
                }
                AnimateTabColor(foreground, targetColor, animate);

                AnimateTabIndicator(
                    tabIndicators[pair.Key],
                    tabIndicatorScales[pair.Key],
                    selected ? 1 : 0,
                    selected ? 1 : 0.72,
                    animate);
                AutomationProperties.SetHelpText(
                    pair.Value,
                    selected ? "Выбрано" : "Открыть раздел");
                AutomationProperties.SetItemStatus(
                    pair.Value,
                    selected ? "Выбрано" : string.Empty);
            }
        }

        private void BeginPageTransition(
            CenterPage previousPage,
            CenterPage nextPage)
        {
            if (previousPage == nextPage)
            {
                RenderCurrentPage();
                FocusPreferredButton();
                return;
            }

            CancelSmoothMouseWheelScroll();
            int direction = (int)nextPage > (int)previousPage ? 1 : -1;
            int generation = ++pageTransitionGeneration;
            pageTransitionAnimating = true;
            pageScroller.IsHitTestVisible = false;
            footerButtons.IsHitTestVisible = false;
            FocusSelectedTab(nextPage);

            AnimatePageVisual(
                subtitle,
                subtitleTranslation,
                0,
                -direction * 12,
                90,
                EasingMode.EaseIn,
                null);
            AnimatePageVisual(
                footerButtons,
                footerTranslation,
                0,
                -direction * 18,
                90,
                EasingMode.EaseIn,
                null);
            AnimatePageVisual(
                pageScroller,
                pageTranslation,
                0,
                -direction * 18,
                90,
                EasingMode.EaseIn,
                delegate
                {
                    if (!pageTransitionAnimating ||
                        generation != pageTransitionGeneration)
                    {
                        return;
                    }

                    renderedPage = currentPage;
                    RenderCurrentPageCore();
                    PreparePageVisual(
                        subtitle,
                        subtitleTranslation,
                        direction * 12);
                    PreparePageVisual(
                        footerButtons,
                        footerTranslation,
                        direction * 18);
                    PreparePageVisual(
                        pageScroller,
                        pageTranslation,
                        direction * 18);

                    AnimatePageVisual(
                        subtitle,
                        subtitleTranslation,
                        1,
                        0,
                        130,
                        EasingMode.EaseOut,
                        null);
                    AnimatePageVisual(
                        footerButtons,
                        footerTranslation,
                        1,
                        0,
                        130,
                        EasingMode.EaseOut,
                        null);
                    AnimatePageVisual(
                        pageScroller,
                        pageTranslation,
                        1,
                        0,
                        130,
                        EasingMode.EaseOut,
                        delegate
                        {
                            if (!pageTransitionAnimating ||
                                generation != pageTransitionGeneration)
                            {
                                return;
                            }

                            pageTransitionAnimating = false;
                            ResetPageTransitionVisuals();
                            FocusPreferredButton();
                        });
                });
        }

        private void FinishPageTransitionImmediately()
        {
            if (!pageTransitionAnimating)
            {
                return;
            }

            CancelPageTransitionAnimations();
            if (renderedPage != currentPage)
            {
                renderedPage = currentPage;
                RenderCurrentPageCore();
            }
        }

        private void CancelPageTransitionAnimations()
        {
            ++pageTransitionGeneration;
            pageTransitionAnimating = false;
            ResetPageTransitionVisuals();
        }

        private void ResetPageTransitionVisuals()
        {
            ResetPageVisual(subtitle, subtitleTranslation);
            ResetPageVisual(footerButtons, footerTranslation);
            ResetPageVisual(pageScroller, pageTranslation);
            pageScroller.IsHitTestVisible = true;
            footerButtons.IsHitTestVisible = true;
        }

        private static void PreparePageVisual(
            FrameworkElement element,
            TranslateTransform translation,
            double offset)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = 0;
            translation.X = offset;
        }

        private static void ResetPageVisual(
            FrameworkElement element,
            TranslateTransform translation)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = 1;
            translation.X = 0;
        }

        private static void AnimatePageVisual(
            FrameworkElement element,
            TranslateTransform translation,
            double targetOpacity,
            double targetOffset,
            int milliseconds,
            EasingMode easingMode,
            EventHandler completed)
        {
            double startOpacity = element.Opacity;
            double startOffset = translation.X;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            translation.BeginAnimation(TranslateTransform.XProperty, null);
            element.Opacity = targetOpacity;
            translation.X = targetOffset;

            var opacityAnimation = new DoubleAnimation(
                startOpacity,
                targetOpacity,
                TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = easingMode },
                FillBehavior = FillBehavior.Stop
            };
            if (completed != null)
            {
                opacityAnimation.Completed += completed;
            }

            element.BeginAnimation(
                UIElement.OpacityProperty,
                opacityAnimation,
                HandoffBehavior.SnapshotAndReplace);
            translation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(
                    startOffset,
                    targetOffset,
                    TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = easingMode },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateTabColor(
            SolidColorBrush brush,
            Color target,
            bool animate)
        {
            Color start = brush.Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            if (!animate ||
                !SystemParameters.ClientAreaAnimation ||
                start == target)
            {
                return;
            }

            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    start,
                    target,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateTabIndicator(
            Border indicator,
            ScaleTransform scale,
            double targetOpacity,
            double targetScale,
            bool animate)
        {
            double startOpacity = indicator.Opacity;
            double startScale = scale.ScaleX;
            indicator.BeginAnimation(UIElement.OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            indicator.Opacity = targetOpacity;
            scale.ScaleX = targetScale;

            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                return;
            }

            indicator.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(
                    startOpacity,
                    targetOpacity,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(
                    startScale,
                    targetScale,
                    TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    },
                    FillBehavior = FillBehavior.Stop
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        private void FocusSelectedTab(CenterPage page)
        {
            Button selectedTab;
            if (tabButtons.TryGetValue(page, out selectedTab) &&
                selectedTab.IsEnabled &&
                selectedTab.IsVisible)
            {
                selectedTab.Focus();
                Keyboard.Focus(selectedTab);
            }
        }

        private void RenderCurrentPage()
        {
            CancelPageTransitionAnimations();
            renderedPage = currentPage;
            RenderCurrentPageCore();
        }

        private void RenderCurrentPageCore()
        {
            CancelSmoothMouseWheelScroll();
            pageScroller.ScrollToVerticalOffset(0);
            smoothScrollTarget = 0;
            pageContent.Children.Clear();
            footerButtons.Children.Clear();
            preferredFocusButton = null;
            benchmarkNoticeTitleBlock = null;
            benchmarkNoticeDetailBlock = null;
            benchmarkProgressFill = null;
            benchmarkButton = null;

            if (currentPage == CenterPage.Readiness)
            {
                RenderReadiness();
            }
            else if (currentPage == CenterPage.Report)
            {
                RenderReport();
            }
            else
            {
                RenderSettings();
            }
        }

        private void RenderReadiness()
        {
            subtitle.Text = preflight == null
                ? "Проверяем состояние системы."
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "Последняя проверка: {0:HH:mm:ss}",
                    preflight.CapturedUtc.ToLocalTime());

            if (preflight == null)
            {
                pageContent.Children.Add(MakeEmptyState(
                    "ИДЁТ ПРОВЕРКА",
                    "Собираем только безопасные данные без изменения Windows."));
            }
            else
            {
                foreach (BoostCheckResult check in preflight.Checks)
                {
                    pageContent.Children.Add(BuildCheckRow(check));
                }
            }

            var refresh = MakeActionButton("ПРОВЕРИТЬ СНОВА", false, false);
            refresh.Width = 154;
            refresh.Click += delegate { Raise(RefreshRequested); };
            AutomationProperties.SetName(refresh, "Проверить готовность снова");
            footerButtons.Children.Add(refresh);
            preferredFocusButton = refresh;

            if (requireBoostDecision)
            {
                var proceed = MakeActionButton(
                    preflight != null && preflight.HasBlockers
                        ? "BOOST НЕДОСТУПЕН"
                        : "ПРОДОЛЖИТЬ BOOST",
                    true,
                    false);
                proceed.Width = 168;
                proceed.Margin = new Thickness(10, 0, 0, 0);
                proceed.IsEnabled = preflight != null && !preflight.HasBlockers;
                proceed.IsDefault = proceed.IsEnabled;
                proceed.Click += delegate
                {
                    requireBoostDecision = false;
                    Close();
                    Raise(ProceedBoostRequested);
                };
                AutomationProperties.SetName(proceed, "Продолжить запуск Boost");
                footerButtons.Children.Add(proceed);
                if (proceed.IsEnabled)
                {
                    preferredFocusButton = proceed;
                }
            }
        }

        private FrameworkElement BuildCheckRow(BoostCheckResult check)
        {
            var row = new Grid
            {
                MinHeight = 51,
                Margin = new Thickness(0, 0, 0, 1),
                Background = Brushes.Transparent
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string glyph;
            Color glyphColor;
            GetSeverityVisual(check.Severity, out glyph, out glyphColor);
            var status = MakeText(
                glyph,
                13,
                glyphColor,
                new FontFamily("Segoe UI Symbol"),
                FontWeights.Bold);
            status.VerticalAlignment = VerticalAlignment.Top;
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetColumn(status, 0);
            Grid.SetRowSpan(status, 2);
            row.Children.Add(status);

            var title = MakeText(
                check.Title,
                10.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.Margin = new Thickness(0, 7, 0, 0);
            Grid.SetColumn(title, 1);
            Grid.SetRow(title, 0);
            row.Children.Add(title);

            var detail = MakeText(
                check.Detail,
                9.5,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 2, 4, 7);
            Grid.SetColumn(detail, 1);
            Grid.SetRow(detail, 1);
            row.Children.Add(detail);

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 2);
            Grid.SetRowSpan(separator, 2);
            row.Children.Add(separator);

            AutomationProperties.SetName(
                row,
                check.Title + ". " + check.Detail);
            return row;
        }

        private void RenderReport()
        {
            subtitle.Text = "Что сделал Boost и как прошла последняя игровая сессия.";

            if (!string.IsNullOrWhiteSpace(benchmarkTitle))
            {
                pageContent.Children.Add(BuildBenchmarkNotice());
            }

            if (sessionReport == null)
            {
                pageContent.Children.Add(MakeEmptyState(
                    "ЕЩЁ НЕТ ОТЧЁТА",
                    "Активируйте Boost — выполненные действия появятся здесь."));
            }
            else
            {
                pageContent.Children.Add(BuildSessionSummary(sessionReport));
                if (sessionReport.Performance != null &&
                    sessionReport.Performance.Available)
                {
                    pageContent.Children.Add(BuildPerformanceGrid(sessionReport.Performance));
                }

                var actionsTitle = MakeText(
                    "ДЕЙСТВИЯ",
                    10.5,
                    TextColor,
                    semiboldFont,
                    FontWeights.Bold);
                actionsTitle.Margin = new Thickness(0, 13, 0, 4);
                pageContent.Children.Add(actionsTitle);

                IEnumerable<BoostActionRecord> actions =
                    (sessionReport.Actions ?? new List<BoostActionRecord>())
                        .OrderByDescending(item => item.TimestampUtc)
                        .Take(20);
                if (!actions.Any())
                {
                    pageContent.Children.Add(MakeText(
                        "Действия ещё не зафиксированы.",
                        9.8,
                        MutedColor,
                        regularFont,
                        FontWeights.Normal));
                }
                else
                {
                    foreach (BoostActionRecord action in actions)
                    {
                        pageContent.Children.Add(BuildActionRow(action));
                    }
                }
            }

            var benchmark = MakeActionButton(
                benchmarkNeedsElevation
                    ? "ПОВТОРИТЬ С UAC"
                    : (benchmarkBusy
                        ? "ЗАМЕР " + benchmarkPercent.ToString(CultureInfo.CurrentCulture) + "%"
                        : "ТЕСТ FPS · 60 СЕК"),
                true,
                false);
            benchmark.Width = 164;
            benchmark.IsEnabled = !benchmarkBusy;
            benchmark.Click += delegate
            {
                EventHandler<BoostBenchmarkRequestEventArgs> handler = BenchmarkRequested;
                if (handler != null)
                {
                    handler(this, new BoostBenchmarkRequestEventArgs(benchmarkNeedsElevation));
                }
            };
            AutomationProperties.SetName(
                benchmark,
                benchmarkNeedsElevation
                    ? "Повторить тест FPS с правами администратора"
                    : "Запустить тест FPS на 60 секунд");
            benchmarkButton = benchmark;
            footerButtons.Children.Add(benchmark);
            preferredFocusButton = benchmark;
        }

        private FrameworkElement BuildBenchmarkNotice()
        {
            var notice = new Border
            {
                Background = new SolidColorBrush(SurfaceColor),
                BorderBrush = new SolidColorBrush(
                    benchmarkNeedsElevation || benchmarkPercent < 0
                        ? ErrorColor
                        : AccentColor),
                BorderThickness = new Thickness(1, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var content = new StackPanel();
            var title = MakeText(
                benchmarkTitle,
                10.2,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            benchmarkNoticeTitleBlock = title;
            content.Children.Add(title);
            var detail = MakeText(
                benchmarkDetail,
                9.3,
                SecondaryColor,
                regularFont,
                FontWeights.Normal);
            benchmarkNoticeDetailBlock = detail;
            detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(detail);

            if (benchmarkBusy)
            {
                var track = new Border
                {
                    Height = 3,
                    Background = new SolidColorBrush(ButtonColor),
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(0, 8, 0, 0)
                };
                var fill = new Border
                {
                    Width = 3.2 * benchmarkPercent,
                    Height = 3,
                    Background = new SolidColorBrush(AccentColor),
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                benchmarkProgressFill = fill;
                track.Child = fill;
                content.Children.Add(track);
            }
            notice.Child = content;
            AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);
            return notice;
        }

        private void UpdateBenchmarkProgressVisuals()
        {
            if (benchmarkNoticeTitleBlock != null)
            {
                benchmarkNoticeTitleBlock.Text = benchmarkTitle ?? string.Empty;
            }
            if (benchmarkNoticeDetailBlock != null)
            {
                benchmarkNoticeDetailBlock.Text = benchmarkDetail ?? string.Empty;
            }
            if (benchmarkProgressFill != null)
            {
                benchmarkProgressFill.Width = 3.2 * benchmarkPercent;
            }
            if (benchmarkButton != null)
            {
                benchmarkButton.Content = "ЗАМЕР " +
                    benchmarkPercent.ToString(CultureInfo.CurrentCulture) + "%";
                benchmarkButton.IsEnabled = false;
            }
        }

        private FrameworkElement BuildSessionSummary(BoostSessionReport report)
        {
            var summary = new Grid
            {
                Margin = new Thickness(0, 0, 0, 5)
            };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TimeSpan duration = (report.EndedUtc ?? DateTime.UtcNow) - report.StartedUtc;
            var durationBlock = BuildMetric(
                "ДЛИТЕЛЬНОСТЬ",
                FormatDuration(duration),
                false);
            Grid.SetColumn(durationBlock, 0);
            durationBlock.Margin = new Thickness(0, 0, 4, 0);
            summary.Children.Add(durationBlock);

            long memoryEnd = report.EndedUtc.HasValue
                ? report.AvailableMemoryEndBytes
                : BoostSystemMetrics.GetAvailableMemoryBytes();
            bool memoryAvailable =
                report.AvailableMemoryStartBytes > 0 &&
                memoryEnd > 0;
            long memoryDelta = memoryAvailable
                ? memoryEnd - report.AvailableMemoryStartBytes
                : 0;
            string memoryText = !memoryAvailable
                ? "ДАННЫЕ НЕДОСТУПНЫ"
                : memoryDelta == 0
                    ? "БЕЗ ИЗМЕНЕНИЙ"
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}{1:0} МБ",
                        memoryDelta > 0 ? "+" : string.Empty,
                        memoryDelta / 1048576.0);
            var memoryBlock = BuildMetric(
                "ДОСТУПНАЯ RAM",
                memoryText,
                memoryAvailable && memoryDelta > 0);
            Grid.SetColumn(memoryBlock, 1);
            memoryBlock.Margin = new Thickness(4, 0, 0, 0);
            summary.Children.Add(memoryBlock);
            return summary;
        }

        private FrameworkElement BuildPerformanceGrid(BoostPerformanceResult result)
        {
            var host = new StackPanel
            {
                Margin = new Thickness(0, 9, 0, 0)
            };
            var title = MakeText(
                "ПОКАДРОВЫЙ ЗАМЕР",
                10.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.Margin = new Thickness(0, 0, 0, 5);
            host.Children.Add(title);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddPerformanceMetric(grid, "СРЕДНИЙ FPS", result.AverageFps.ToString("0.0", CultureInfo.CurrentCulture), 0, 0);
            AddPerformanceMetric(grid, "1% LOW", result.OnePercentLowFps.ToString("0.0", CultureInfo.CurrentCulture), 0, 1);
            AddPerformanceMetric(grid, "P95 FRAME TIME", result.P95FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " мс", 1, 0);
            AddPerformanceMetric(grid, "КАДРЫ > 50 МС", result.FramesOver50Ms.ToString(CultureInfo.CurrentCulture), 1, 1);
            host.Children.Add(grid);
            return host;
        }

        private void AddPerformanceMetric(
            Grid grid,
            string title,
            string value,
            int row,
            int column)
        {
            var metric = BuildMetric(title, value, false);
            metric.Margin = new Thickness(
                column == 0 ? 0 : 4,
                row == 0 ? 0 : 8,
                column == 0 ? 4 : 0,
                0);
            Grid.SetRow(metric, row);
            Grid.SetColumn(metric, column);
            grid.Children.Add(metric);
        }

        private Border BuildMetric(
            string title,
            string value,
            bool positive)
        {
            var host = new Border
            {
                Height = 61,
                Background = new SolidColorBrush(SurfaceColor),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 8, 11, 7)
            };
            var content = new StackPanel();
            content.Children.Add(MakeText(
                title,
                8.7,
                MutedColor,
                semiboldFont,
                FontWeights.Bold));
            var valueText = MakeText(
                value,
                15,
                positive ? SuccessColor : TextColor,
                semiboldFont,
                FontWeights.Bold);
            valueText.Margin = new Thickness(0, 3, 0, 0);
            content.Children.Add(valueText);
            host.Child = content;
            return host;
        }

        private FrameworkElement BuildActionRow(BoostActionRecord action)
        {
            var row = new Grid
            {
                MinHeight = 42
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string glyph;
            Color color;
            GetActionVisual(action.Outcome, out glyph, out color);
            var icon = MakeText(
                glyph,
                11,
                color,
                new FontFamily("Segoe UI Symbol"),
                FontWeights.Bold);
            icon.Margin = new Thickness(0, 5, 0, 0);
            Grid.SetColumn(icon, 0);
            Grid.SetRowSpan(icon, 2);
            row.Children.Add(icon);

            var title = MakeText(
                action.Title,
                9.8,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold);
            title.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetColumn(title, 1);
            row.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(action.Detail))
            {
                var detail = MakeText(
                    action.Detail,
                    9,
                    MutedColor,
                    regularFont,
                    FontWeights.Normal);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Margin = new Thickness(0, 1, 0, 5);
                Grid.SetColumn(detail, 1);
                Grid.SetRow(detail, 1);
                row.Children.Add(detail);
            }

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 2);
            Grid.SetRowSpan(separator, 2);
            row.Children.Add(separator);
            return row;
        }

        private void RenderSettings()
        {
            subtitle.Text = "Настройки игровой сессии применяются без перезагрузки.";
            settingsLoading = true;
            try
            {
                pageContent.Children.Add(BuildSettingToggle(
                    "АВТОМАТИЧЕСКИЙ BOOST",
                    "Активировать Boost, когда открытая программа обнаружит GTA.",
                    settings.AutoBoost,
                    delegate(bool value) { settings.AutoBoost = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "ПРОВЕРКА ПЕРЕД ЗАПУСКОМ",
                    "Показывать предупреждения о питании, памяти и перезагрузке.",
                    settings.CheckBeforeBoost,
                    delegate(bool value) { settings.CheckBeforeBoost = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ ONEDRIVE",
                    "Сохранить синхронизацию OneDrive во время запуска Boost.",
                    settings.KeepOneDrive,
                    delegate(bool value) { settings.KeepOneDrive = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ MICROSOFT TEAMS",
                    "Оставить Teams запущенным.",
                    settings.KeepTeams,
                    delegate(bool value) { settings.KeepTeams = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ WALLPAPER ENGINE",
                    "Оставить анимированные обои запущенными.",
                    settings.KeepWallpaper,
                    delegate(bool value) { settings.KeepWallpaper = value; }));
                pageContent.Children.Add(BuildSettingToggle(
                    "НЕ ЗАКРЫВАТЬ NVIDIA OVERLAY",
                    "Сохранить NVIDIA Overlay и запись клипов.",
                    settings.KeepNvidiaOverlay,
                    delegate(bool value) { settings.KeepNvidiaOverlay = value; }));
            }
            finally
            {
                settingsLoading = false;
            }

            var restore = MakeActionButton("ВОССТАНОВИТЬ WINDOWS", false, true);
            restore.Width = 178;
            restore.Click += delegate { Raise(RestoreRequested); };
            AutomationProperties.SetName(
                restore,
                "Открыть безопасное восстановление системных настроек");
            footerButtons.Children.Add(restore);
            preferredFocusButton = restore;
        }

        private FrameworkElement BuildSettingToggle(
            string title,
            string detail,
            bool isChecked,
            Action<bool> apply)
        {
            var toggle = new CheckBox
            {
                MinHeight = 52,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                Template = MakeTransparentCheckBoxTemplate(),
                IsChecked = isChecked
            };
            AutomationProperties.SetName(toggle, title.ToLowerInvariant());
            AutomationProperties.SetHelpText(toggle, detail);

            var content = new Grid
            {
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleText = MakeText(
                title,
                10,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            titleText.Margin = new Thickness(0, 5, 8, 0);
            Grid.SetColumn(titleText, 0);
            Grid.SetRow(titleText, 0);
            content.Children.Add(titleText);

            var detailText = MakeText(
                detail,
                9,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.Margin = new Thickness(0, 2, 8, 6);
            Grid.SetColumn(detailText, 0);
            Grid.SetRow(detailText, 1);
            content.Children.Add(detailText);

            var trackBrush = new SolidColorBrush(
                isChecked ? AccentColor : ButtonColor);
            var track = new Border
            {
                Width = 36,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = trackBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ClipToBounds = false
            };
            Grid.SetColumn(track, 1);
            Grid.SetRowSpan(track, 2);

            var knob = new Ellipse
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(3, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Brushes.White,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            var knobTranslation = new TranslateTransform(isChecked ? 14 : 0, 0);
            knob.RenderTransform = knobTranslation;
            track.Child = knob;
            content.Children.Add(track);

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(DividerColor),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumnSpan(separator, 2);
            Grid.SetRowSpan(separator, 2);
            content.Children.Add(separator);

            toggle.Tag = new ToggleVisuals
            {
                TrackBrush = trackBrush,
                KnobTranslation = knobTranslation
            };
            toggle.Content = content;
            toggle.Checked += delegate
            {
                AnimateToggle(toggle);
                apply(true);
                if (!settingsLoading)
                {
                    Raise(SettingsChanged);
                }
            };
            toggle.Unchecked += delegate
            {
                AnimateToggle(toggle);
                apply(false);
                if (!settingsLoading)
                {
                    Raise(SettingsChanged);
                }
            };
            toggle.MouseEnter += delegate { AnimateToggle(toggle); };
            toggle.MouseLeave += delegate { AnimateToggle(toggle); };
            return toggle;
        }

        private static void AnimateToggle(CheckBox toggle)
        {
            var visuals = toggle.Tag as ToggleVisuals;
            if (visuals == null)
            {
                return;
            }
            bool active = toggle.IsChecked == true;
            Color targetColor = active
                ? AccentColor
                : (toggle.IsMouseOver ? HoverColor : ButtonColor);
            double targetX = active ? 14 : 0;
            if (!SystemParameters.ClientAreaAnimation)
            {
                visuals.TrackBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                visuals.KnobTranslation.BeginAnimation(TranslateTransform.XProperty, null);
                visuals.TrackBrush.Color = targetColor;
                visuals.KnobTranslation.X = targetX;
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            visuals.TrackBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                });
            visuals.KnobTranslation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                });
        }

        private Border MakeEmptyState(string title, string detail)
        {
            var host = new Border
            {
                Background = Brushes.Transparent,
                MinHeight = 180,
                Padding = new Thickness(20)
            };
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleText = MakeText(
                title,
                12,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            titleText.TextAlignment = TextAlignment.Center;
            content.Children.Add(titleText);
            var detailText = MakeText(
                detail,
                10,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            detailText.TextAlignment = TextAlignment.Center;
            detailText.TextWrapping = TextWrapping.Wrap;
            detailText.MaxWidth = 290;
            detailText.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(detailText);
            host.Child = content;
            return host;
        }

        private Button MakeActionButton(
            string text,
            bool primary,
            bool destructive)
        {
            var background = new SolidColorBrush(
                primary ? AccentColor : ButtonColor);
            var foreground = new SolidColorBrush(TextColor);
            var border = new SolidColorBrush(
                primary ? AccentColor : BorderColor);
            var button = new Button
            {
                Height = 38,
                Padding = new Thickness(13, 0, 13, 0),
                Background = background,
                Foreground = foreground,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                FontFamily = semiboldFont,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                Template = MakeFlatButtonTemplate(6),
                Content = text
            };
            var lift = new TranslateTransform();
            button.RenderTransform = lift;

            button.MouseEnter += delegate
            {
                Color target = destructive
                    ? ErrorColor
                    : (primary ? Color.FromRgb(242, 35, 99) : AccentColor);
                AnimateBrush(background, target, 210);
                AnimateBrush(border, target, 210);
                AnimateLift(lift, -1, 240);
            };
            button.MouseLeave += delegate
            {
                AnimateBrush(background, primary ? AccentColor : ButtonColor, 240);
                AnimateBrush(border, primary ? AccentColor : BorderColor, 240);
                AnimateLift(lift, 0, 260);
            };
            return button;
        }

        private static ControlTemplate MakeFlatButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "Chrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            return template;
        }

        private static ControlTemplate MakeTransparentCheckBoxTemplate()
        {
            var template = new ControlTemplate(typeof(CheckBox));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static Style MakeMajesticVerticalScrollBarStyle()
        {
            const string xaml =
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "TargetType=\"{x:Type ScrollBar}\">" +
                "<Setter Property=\"Width\" Value=\"9\"/>" +
                "<Setter Property=\"MinWidth\" Value=\"9\"/>" +
                "<Setter Property=\"Background\" Value=\"Transparent\"/>" +
                "<Setter Property=\"BorderThickness\" Value=\"0\"/>" +
                "<Setter Property=\"Focusable\" Value=\"False\"/>" +
                "<Setter Property=\"Template\">" +
                "<Setter.Value>" +
                "<ControlTemplate TargetType=\"{x:Type ScrollBar}\">" +
                "<Grid Background=\"Transparent\" SnapsToDevicePixels=\"True\">" +
                "<Track x:Name=\"PART_Track\" IsDirectionReversed=\"True\" Focusable=\"False\">" +
                "<Track.DecreaseRepeatButton>" +
                "<RepeatButton Command=\"{x:Static ScrollBar.PageUpCommand}\" " +
                "Focusable=\"False\" IsTabStop=\"False\" Opacity=\"0\">" +
                "<RepeatButton.Template>" +
                "<ControlTemplate TargetType=\"{x:Type RepeatButton}\">" +
                "<Border Background=\"Transparent\"/>" +
                "</ControlTemplate>" +
                "</RepeatButton.Template>" +
                "</RepeatButton>" +
                "</Track.DecreaseRepeatButton>" +
                "<Track.Thumb>" +
                "<Thumb Width=\"5\" MinHeight=\"32\" HorizontalAlignment=\"Center\" " +
                "Background=\"#494949\" Focusable=\"False\">" +
                "<Thumb.Template>" +
                "<ControlTemplate TargetType=\"{x:Type Thumb}\">" +
                "<Border x:Name=\"ThumbChrome\" Background=\"{TemplateBinding Background}\" " +
                "CornerRadius=\"2.5\"/>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property=\"IsMouseOver\" Value=\"True\">" +
                "<Setter TargetName=\"ThumbChrome\" Property=\"Background\" Value=\"#606060\"/>" +
                "</Trigger>" +
                "<Trigger Property=\"IsDragging\" Value=\"True\">" +
                "<Setter TargetName=\"ThumbChrome\" Property=\"Background\" Value=\"#E81C5A\"/>" +
                "</Trigger>" +
                "<Trigger Property=\"IsEnabled\" Value=\"False\">" +
                "<Setter TargetName=\"ThumbChrome\" Property=\"Opacity\" Value=\"0.35\"/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate>" +
                "</Thumb.Template>" +
                "</Thumb>" +
                "</Track.Thumb>" +
                "<Track.IncreaseRepeatButton>" +
                "<RepeatButton Command=\"{x:Static ScrollBar.PageDownCommand}\" " +
                "Focusable=\"False\" IsTabStop=\"False\" Opacity=\"0\">" +
                "<RepeatButton.Template>" +
                "<ControlTemplate TargetType=\"{x:Type RepeatButton}\">" +
                "<Border Background=\"Transparent\"/>" +
                "</ControlTemplate>" +
                "</RepeatButton.Template>" +
                "</RepeatButton>" +
                "</Track.IncreaseRepeatButton>" +
                "</Track>" +
                "</Grid>" +
                "</ControlTemplate>" +
                "</Setter.Value>" +
                "</Setter>" +
                "</Style>";

            return (Style)XamlReader.Parse(xaml);
        }

        private void PageScrollerPreviewMouseWheel(
            object sender,
            MouseWheelEventArgs args)
        {
            if (args.Delta == 0 ||
                pageScroller.ScrollableHeight <= 0 ||
                SystemParameters.WheelScrollLines == 0)
            {
                return;
            }

            double baseTarget = smoothScrollAnimating
                ? smoothScrollTarget
                : pageScroller.VerticalOffset;
            double target = CalculateSmoothScrollTarget(
                pageScroller.VerticalOffset,
                smoothScrollTarget,
                args.Delta,
                pageScroller.ScrollableHeight,
                pageScroller.ViewportHeight,
                SystemParameters.WheelScrollLines,
                smoothScrollAnimating);
            if (Math.Abs(target - baseTarget) < 0.01)
            {
                return;
            }

            args.Handled = true;
            double currentOffset = pageScroller.VerticalOffset;
            int generation = ++smoothScrollGeneration;
            smoothScrollTarget = target;

            scrollAnimationProxy.BeginAnimation(
                ScrollAnimationProxy.OffsetProperty,
                null);
            scrollAnimationProxy.Offset = currentOffset;

            if (!SystemParameters.ClientAreaAnimation)
            {
                scrollAnimationProxy.Offset = target;
                smoothScrollAnimating = false;
                return;
            }

            smoothScrollAnimating = true;
            var animation = new DoubleAnimation(
                currentOffset,
                target,
                TimeSpan.FromMilliseconds(175))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += delegate
            {
                if (generation != smoothScrollGeneration)
                {
                    return;
                }

                scrollAnimationProxy.BeginAnimation(
                    ScrollAnimationProxy.OffsetProperty,
                    null);
                scrollAnimationProxy.Offset = target;
                smoothScrollAnimating = false;
            };
            scrollAnimationProxy.BeginAnimation(
                ScrollAnimationProxy.OffsetProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        internal static double CalculateSmoothScrollTarget(
            double currentOffset,
            double pendingTarget,
            int wheelDelta,
            double scrollableHeight,
            double viewportHeight,
            int wheelScrollLines,
            bool isAnimating)
        {
            double upperBound = Math.Max(0, scrollableHeight);
            double origin = isAnimating ? pendingTarget : currentOffset;
            if (wheelDelta == 0 || upperBound <= 0 || wheelScrollLines == 0)
            {
                return Math.Max(0, Math.Min(upperBound, origin));
            }

            double step = wheelScrollLines < 0
                ? Math.Max(48, viewportHeight * 0.82)
                : Math.Max(36, Math.Min(96, wheelScrollLines * 18.0));
            double target = origin -
                (((double)wheelDelta / Mouse.MouseWheelDeltaForOneLine) * step);
            return Math.Max(0, Math.Min(upperBound, target));
        }

        private void CancelSmoothMouseWheelScroll()
        {
            if (!smoothScrollAnimating)
            {
                return;
            }

            double currentOffset = pageScroller.VerticalOffset;
            ++smoothScrollGeneration;
            scrollAnimationProxy.BeginAnimation(
                ScrollAnimationProxy.OffsetProperty,
                null);
            scrollAnimationProxy.Offset = currentOffset;
            smoothScrollTarget = currentOffset;
            smoothScrollAnimating = false;
        }

        private static void AnimateBrush(
            SolidColorBrush brush,
            Color target,
            int milliseconds)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = target;
                return;
            }
            brush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private static void AnimateLift(
            TranslateTransform transform,
            double target,
            int milliseconds)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = target;
                return;
            }
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        private void FocusPreferredButton()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (preferredFocusButton != null &&
                    preferredFocusButton.IsEnabled &&
                    preferredFocusButton.IsVisible)
                {
                    preferredFocusButton.Focus();
                    Keyboard.Focus(preferredFocusButton);
                }
            }));
        }

        private void OverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            HandleKey(e);
        }

        private static void GetSeverityVisual(
            BoostCheckSeverity severity,
            out string glyph,
            out Color color)
        {
            if (severity == BoostCheckSeverity.Pass)
            {
                glyph = "✓";
                color = SuccessColor;
            }
            else if (severity == BoostCheckSeverity.Warning)
            {
                glyph = "!";
                color = WarningColor;
            }
            else if (severity == BoostCheckSeverity.Blocked)
            {
                glyph = "×";
                color = ErrorColor;
            }
            else if (severity == BoostCheckSeverity.Info)
            {
                glyph = "i";
                color = AccentColor;
            }
            else
            {
                glyph = "?";
                color = MutedColor;
            }
        }

        private static void GetActionVisual(
            BoostActionOutcome outcome,
            out string glyph,
            out Color color)
        {
            if (outcome == BoostActionOutcome.Changed ||
                outcome == BoostActionOutcome.Restored)
            {
                glyph = "✓";
                color = SuccessColor;
            }
            else if (outcome == BoostActionOutcome.Failed)
            {
                glyph = "×";
                color = ErrorColor;
            }
            else if (outcome == BoostActionOutcome.Preserved ||
                     outcome == BoostActionOutcome.ExternalOverridePreserved)
            {
                glyph = "•";
                color = AccentColor;
            }
            else
            {
                glyph = "–";
                color = MutedColor;
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}:{1:00}:{2:00}",
                    (int)duration.TotalHours,
                    duration.Minutes,
                    duration.Seconds);
            }
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0}:{1:00}",
                Math.Max(0, (int)duration.TotalMinutes),
                Math.Max(0, duration.Seconds));
        }

        private static TextBlock MakeText(
            string text,
            double size,
            Color color,
            FontFamily font,
            FontWeight weight)
        {
            return new TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = size,
                FontFamily = font,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private static void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }
    }
}
