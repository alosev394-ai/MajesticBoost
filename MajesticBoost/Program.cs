using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

[assembly: AssemblyTitle("Majestic Boost")]
[assembly: AssemblyDescription("Animated Max FPS launcher for Majestic")]
[assembly: AssemblyCompany("Codex Gaming Optimization")]
[assembly: AssemblyProduct("Majestic Boost")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace MajesticBoost
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var application = new Application();
            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
            application.Run(new BoostWindow(args));
        }
    }

    internal sealed class BoostWindow : Window
    {
        private sealed class PreferenceToggleVisuals
        {
            public SolidColorBrush TrackBrush;
            public TranslateTransform KnobTranslation;
        }

        private Button boostButton;
        private Border boostSurface;
        private Grid rocket;
        private Canvas grayRocketLayer;
        private Canvas colorRocketLayer;
        private Canvas starField;
        private Grid flameLayer;
        private TextBlock caption;
        private CheckBox keepDiscordToggle;
        private CheckBox keepEpicToggle;
        private CheckBox keepSteamToggle;
        private OptimizationFlowOverlay optimizationOverlay;
        private UpdateFlowOverlay updateOverlay;
        private ScaleTransform rocketScale;
        private TranslateTransform flightTranslation;
        private TranslateTransform floatTranslation;
        private readonly List<FrameworkElement> stars = new List<FrameworkElement>();
        private DispatcherTimer readinessTimer;
        private Process boostProcess;
        private string readinessSignalPath;
        private DateTime readinessDeadline;
        private bool animationRunning;
        private bool departureFinished;
        private bool boostReady;
        private bool boostActive;
        private bool preferencesLoaded;
        private readonly string[] launchArguments;

        public BoostWindow(string[] args)
        {
            launchArguments = args ?? new string[0];
            Title = "Majestic Boost — Boost производительности";
            Width = 460;
            Height = 480;
            MinWidth = 460;
            MinHeight = 480;
            MaxWidth = 460;
            MaxHeight = 480;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = LoadMajesticFontFamily();
            Icon = BuildWindowIcon();

            Content = BuildShell();
            Loaded += BoostWindowLoaded;
            KeyDown += WindowKeyDown;
            PreviewMouseLeftButtonDown += WindowMouseLeftButtonDown;
            Closing += BoostWindowClosing;
            Closed += WindowClosed;
        }

        private Grid BuildShell()
        {
            var shell = new Grid();
            shell.Margin = new Thickness(18);

            var frame = new Border();
            frame.CornerRadius = new CornerRadius(11);
            frame.BorderThickness = new Thickness(1);
            frame.BorderBrush = BrushFrom("#FF383838");
            frame.Background = BrushFrom("#FF161616");
            frame.Effect = new DropShadowEffect
            {
                BlurRadius = 34,
                ShadowDepth = 0,
                Opacity = 0.64,
                Color = Color.FromRgb(0, 0, 0)
            };
            shell.Children.Add(frame);

            var root = new Grid();
            root.Background = Brushes.Transparent;
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(70) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(188) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.SizeChanged += delegate
            {
                root.Clip = new RectangleGeometry(
                    new Rect(0, 0, Math.Max(0, root.ActualWidth), Math.Max(0, root.ActualHeight)),
                    10,
                    10);
            };
            frame.Child = root;

            var controls = BuildWindowControls();
            Grid.SetRow(controls, 0);
            root.Children.Add(controls);

            var title = BuildTitle();
            Grid.SetRow(title, 1);
            root.Children.Add(title);

            var buttonStage = BuildBoostButton();
            Grid.SetRow(buttonStage, 2);
            root.Children.Add(buttonStage);
            boostButton.IsEnabled = false;

            caption = MakeText("НАЖМИ, ЧТОБЫ АКТИВИРОВАТЬ", 10, "#FF8E8E8E", FontWeights.Bold);
            caption.FontFamily = LoadMajesticSemiboldFontFamily();
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(caption, 3);
            root.Children.Add(caption);

            var preferences = BuildPreferencePanel();
            Grid.SetRow(preferences, 4);
            root.Children.Add(preferences);

            optimizationOverlay = new OptimizationFlowOverlay(
                this,
                launchArguments,
                LoadMajesticFontFamily(),
                LoadMajesticSemiboldFontFamily());
            optimizationOverlay.RequestApplicationClose += delegate { Close(); };
            Grid.SetRow(optimizationOverlay, 0);
            Grid.SetRowSpan(optimizationOverlay, 5);
            Panel.SetZIndex(optimizationOverlay, 100);
            root.Children.Add(optimizationOverlay);

            updateOverlay = new UpdateFlowOverlay(
                this,
                launchArguments,
                LoadMajesticFontFamily(),
                LoadMajesticSemiboldFontFamily());
            updateOverlay.RequestApplicationClose += delegate { Close(); };
            Grid.SetRow(updateOverlay, 0);
            Grid.SetRowSpan(updateOverlay, 5);
            Panel.SetZIndex(updateOverlay, 200);
            root.Children.Add(updateOverlay);

            return shell;
        }

        private Grid BuildWindowControls()
        {
            var header = new Grid();
            header.Margin = new Thickness(16, 0, 0, 0);

            var controls = new StackPanel();
            controls.Orientation = Orientation.Horizontal;
            controls.HorizontalAlignment = HorizontalAlignment.Right;
            controls.VerticalAlignment = VerticalAlignment.Top;

            var version = MakeText(GetMajesticLauncherVersion(), 11.5, "#FF8B8B8B", FontWeights.Bold);
            version.FontFamily = LoadMajesticSemiboldFontFamily();
            version.LayoutTransform = new ScaleTransform(0.95, 1);
            version.RenderTransform = new TranslateTransform(0, 2);
            version.VerticalAlignment = VerticalAlignment.Center;
            version.Margin = new Thickness(0, 0, 10, 0);
            controls.Children.Add(version);

            var minimize = MakeWindowButton("Свернуть", false);
            minimize.Click += delegate { WindowState = WindowState.Minimized; };
            controls.Children.Add(minimize);

            var close = MakeWindowButton("Закрыть", true);
            close.Click += delegate { Close(); };
            controls.Children.Add(close);

            header.Children.Add(controls);
            return header;
        }

        private StackPanel BuildTitle()
        {
            var title = new StackPanel();
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;

            var firstLine = MakeText("BOOST", 28, "#FFF4F4F4", FontWeights.Bold);
            firstLine.HorizontalAlignment = HorizontalAlignment.Center;
            firstLine.Margin = new Thickness(0, 0, 0, -4);
            title.Children.Add(firstLine);

            var secondLine = MakeText("ПРОИЗВОДИТЕЛЬНОСТИ", 17, "#FFFFFFFF", FontWeights.Bold);
            secondLine.Foreground = BrushFrom("#FFE81C5A");
            secondLine.HorizontalAlignment = HorizontalAlignment.Center;
            title.Children.Add(secondLine);
            return title;
        }

        private Grid BuildBoostButton()
        {
            var stage = new Grid();
            stage.ClipToBounds = false;

            boostButton = new Button();
            boostButton.Width = 184;
            boostButton.Height = 184;
            boostButton.HorizontalAlignment = HorizontalAlignment.Center;
            boostButton.VerticalAlignment = VerticalAlignment.Center;
            boostButton.BorderThickness = new Thickness(0);
            boostButton.Background = Brushes.Transparent;
            boostButton.Cursor = Cursors.Hand;
            boostButton.FocusVisualStyle = null;
            boostButton.Template = MakeTransparentButtonTemplate();
            AutomationProperties.SetName(boostButton, "Активировать Boost производительности");
            AutomationProperties.SetHelpText(boostButton, "Отключает лишние фоновые процессы и запускает Majestic.");

            boostSurface = new Border();
            boostSurface.Width = 178;
            boostSurface.Height = 178;
            boostSurface.CornerRadius = new CornerRadius(54);
            boostSurface.Background = BrushFrom("#FF1B1B1B");
            boostSurface.BorderBrush = BrushFrom("#FFE81C5A");
            boostSurface.BorderThickness = new Thickness(1.5);

            var viewport = new Grid();
            viewport.Width = 176;
            viewport.Height = 176;
            viewport.Background = Brushes.Transparent;
            viewport.Clip = new RectangleGeometry(new Rect(0, 0, 176, 176), 52, 52);
            boostSurface.Child = viewport;

            starField = BuildStarField();
            viewport.Children.Add(starField);

            rocket = BuildRocket();
            viewport.Children.Add(rocket);

            boostButton.Content = boostSurface;
            boostButton.Click += BoostButtonClick;
            boostButton.MouseEnter += BoostButtonMouseEnter;
            boostButton.MouseLeave += BoostButtonMouseLeave;
            stage.Children.Add(boostButton);
            return stage;
        }

        private StackPanel BuildPreferencePanel()
        {
            preferencesLoaded = false;
            var panel = new StackPanel();
            panel.Width = 300;
            panel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;

            keepDiscordToggle = BuildPreferenceToggle("НЕ ЗАКРЫВАТЬ DISCORD");
            keepEpicToggle = BuildPreferenceToggle("НЕ ЗАКРЫВАТЬ EPIC GAMES");
            keepSteamToggle = BuildPreferenceToggle("НЕ ЗАКРЫВАТЬ STEAM");
            panel.Children.Add(keepDiscordToggle);
            panel.Children.Add(keepEpicToggle);
            panel.Children.Add(keepSteamToggle);

            LoadBoostPreferences();
            UpdatePreferenceToggle(keepDiscordToggle, false);
            UpdatePreferenceToggle(keepEpicToggle, false);
            UpdatePreferenceToggle(keepSteamToggle, false);
            preferencesLoaded = true;
            return panel;
        }

        private CheckBox BuildPreferenceToggle(string text)
        {
            var toggle = new CheckBox();
            toggle.Width = 300;
            toggle.Height = 30;
            toggle.Background = Brushes.Transparent;
            toggle.BorderThickness = new Thickness(0);
            toggle.Cursor = Cursors.Hand;
            toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            toggle.VerticalContentAlignment = VerticalAlignment.Center;
            toggle.FocusVisualStyle = null;
            toggle.Template = MakeTransparentCheckBoxTemplate();
            AutomationProperties.SetName(toggle, text.ToLowerInvariant());
            AutomationProperties.SetHelpText(
                toggle,
                "Если включено, Majestic Boost не будет закрывать эту программу перед запуском игры.");

            var content = new Grid();
            content.Width = 300;
            content.Height = 30;
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            var label = MakeText(text, 10.5, "#FFBDBDBD", FontWeights.SemiBold);
            label.FontFamily = LoadMajesticSemiboldFontFamily();
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            content.Children.Add(label);

            var trackBrush = new SolidColorBrush(Color.FromRgb(37, 37, 37));
            var track = new Border();
            track.Width = 36;
            track.Height = 20;
            track.CornerRadius = new CornerRadius(10);
            track.Background = trackBrush;
            track.HorizontalAlignment = HorizontalAlignment.Right;
            track.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(track, 1);

            var knob = new Ellipse();
            knob.Width = 16;
            knob.Height = 16;
            knob.Margin = new Thickness(2, 0, 0, 0);
            knob.HorizontalAlignment = HorizontalAlignment.Left;
            knob.VerticalAlignment = VerticalAlignment.Center;
            knob.Fill = Brushes.White;
            var knobTranslation = new TranslateTransform();
            knob.RenderTransform = knobTranslation;
            track.Child = knob;
            content.Children.Add(track);

            toggle.Tag = new PreferenceToggleVisuals
            {
                TrackBrush = trackBrush,
                KnobTranslation = knobTranslation
            };
            toggle.Content = content;
            toggle.Checked += PreferenceToggleChanged;
            toggle.Unchecked += PreferenceToggleChanged;
            toggle.MouseEnter += delegate { UpdatePreferenceToggle(toggle, true); };
            toggle.MouseLeave += delegate { UpdatePreferenceToggle(toggle, true); };
            return toggle;
        }

        private void PreferenceToggleChanged(object sender, RoutedEventArgs e)
        {
            var toggle = sender as CheckBox;
            if (toggle != null)
            {
                UpdatePreferenceToggle(toggle, true);
            }
            if (preferencesLoaded)
            {
                SaveBoostPreferences();
            }
        }

        private static void UpdatePreferenceToggle(CheckBox toggle, bool animate)
        {
            var visuals = toggle.Tag as PreferenceToggleVisuals;
            if (visuals == null)
            {
                return;
            }

            bool isChecked = toggle.IsChecked == true;
            Color targetColor = isChecked
                ? Color.FromRgb(232, 28, 90)
                : (toggle.IsMouseOver ? Color.FromRgb(52, 52, 52) : Color.FromRgb(37, 37, 37));
            double targetX = isChecked ? 16 : 0;
            if (!animate)
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
                new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            visuals.KnobTranslation.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        }

        private void LoadBoostPreferences()
        {
            var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = GetPreferencesPath();
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int separator = line.IndexOf('=');
                        bool parsed;
                        if (separator > 0 && bool.TryParse(line.Substring(separator + 1).Trim(), out parsed))
                        {
                            values[line.Substring(0, separator).Trim()] = parsed;
                        }
                    }
                }
            }
            catch { }

            keepDiscordToggle.IsChecked = GetPreference(values, "KeepDiscord");
            keepEpicToggle.IsChecked = GetPreference(values, "KeepEpic");
            keepSteamToggle.IsChecked = GetPreference(values, "KeepSteam");
        }

        private void SaveBoostPreferences()
        {
            try
            {
                string path = GetPreferencesPath();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllLines(
                    path,
                    new[]
                    {
                        "KeepDiscord=" + (keepDiscordToggle.IsChecked == true),
                        "KeepEpic=" + (keepEpicToggle.IsChecked == true),
                        "KeepSteam=" + (keepSteamToggle.IsChecked == true)
                    },
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static bool GetPreference(Dictionary<string, bool> values, string name)
        {
            bool value;
            return values.TryGetValue(name, out value) && value;
        }

        private static string GetPreferencesPath()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MajesticBoost",
                "boost-preferences.ini");
        }

        private Canvas BuildStarField()
        {
            var canvas = new Canvas();
            canvas.Width = 176;
            canvas.Height = 176;
            canvas.Opacity = 0;
            canvas.IsHitTestVisible = false;

            var random = new Random(2407);
            for (int index = 0; index < 18; index++)
            {
                var star = new Rectangle();
                star.Width = 3 + random.NextDouble() * 8;
                star.Height = index % 4 == 0 ? 1.7 : 1.15;
                star.RadiusX = star.Height / 2;
                star.RadiusY = star.Height / 2;
                star.Fill = index % 3 == 0 ? BrushFrom("#FFE81C5A") : BrushFrom("#FFFFA6C1");
                star.Opacity = 0.28 + random.NextDouble() * 0.58;
                star.RenderTransformOrigin = new Point(0.5, 0.5);
                var transforms = new TransformGroup();
                transforms.Children.Add(new RotateTransform(-31));
                transforms.Children.Add(new TranslateTransform());
                star.RenderTransform = transforms;
                Canvas.SetLeft(star, 15 + random.NextDouble() * 155);
                Canvas.SetTop(star, 4 + random.NextDouble() * 154);
                canvas.Children.Add(star);
                stars.Add(star);
            }
            return canvas;
        }

        private Grid BuildRocket()
        {
            var host = new Grid();
            host.Width = 106;
            host.Height = 112;
            host.HorizontalAlignment = HorizontalAlignment.Center;
            host.VerticalAlignment = VerticalAlignment.Center;
            host.RenderTransformOrigin = new Point(0.5, 0.5);

            rocketScale = new ScaleTransform(1, 1);
            flightTranslation = new TranslateTransform(0, 0);
            floatTranslation = new TranslateTransform(0, 0);
            var transforms = new TransformGroup();
            transforms.Children.Add(rocketScale);
            transforms.Children.Add(new RotateTransform(43));
            transforms.Children.Add(flightTranslation);
            transforms.Children.Add(floatTranslation);
            host.RenderTransform = transforms;

            flameLayer = BuildStaticFlame();
            flameLayer.Opacity = 0;
            host.Children.Add(flameLayer);

            grayRocketLayer = BuildRocketLayer(false);
            grayRocketLayer.Opacity = 1;
            host.Children.Add(grayRocketLayer);

            colorRocketLayer = BuildRocketLayer(true);
            colorRocketLayer.Opacity = 0;
            host.Children.Add(colorRocketLayer);
            return host;
        }

        private Grid BuildStaticFlame()
        {
            var grid = new Grid();
            grid.Width = 72;
            grid.Height = 103;
            grid.HorizontalAlignment = HorizontalAlignment.Center;
            grid.VerticalAlignment = VerticalAlignment.Center;
            grid.IsHitTestVisible = false;

            var outer = new System.Windows.Shapes.Path();
            outer.Data = Geometry.Parse("M 24,68 C 18,82 24,96 36,102 C 48,96 54,82 48,68 Z");
            outer.Fill = MakeLinearBrush("#FFFFD166", "#FFFF4D5A", 90);
            outer.Effect = new DropShadowEffect
            {
                BlurRadius = 13,
                ShadowDepth = 0,
                Opacity = 0.88,
                Color = Color.FromRgb(255, 83, 70)
            };
            grid.Children.Add(outer);

            var inner = new System.Windows.Shapes.Path();
            inner.Data = Geometry.Parse("M 30,71 C 27,82 31,91 36,96 C 41,91 45,82 42,71 Z");
            inner.Fill = MakeLinearBrush("#FFFFFFFF", "#FFFFBE45", 90);
            grid.Children.Add(inner);
            return grid;
        }

        private Canvas BuildRocketLayer(bool useColor)
        {
            var canvas = new Canvas();
            canvas.Width = 72;
            canvas.Height = 103;
            canvas.HorizontalAlignment = HorizontalAlignment.Center;
            canvas.VerticalAlignment = VerticalAlignment.Center;
            canvas.IsHitTestVisible = false;

            var leftFin = new System.Windows.Shapes.Path();
            leftFin.Data = Geometry.Parse("M 19,51 L 7,70 L 22,65 Z");
            leftFin.Fill = useColor
                ? MakeLinearBrush("#FFFF4A83", "#FFA40F3B", 90)
                : MakeLinearBrush("#FF9AA1AB", "#FF606873", 90);
            canvas.Children.Add(leftFin);

            var rightFin = new System.Windows.Shapes.Path();
            rightFin.Data = Geometry.Parse("M 53,51 L 65,70 L 50,65 Z");
            rightFin.Fill = useColor
                ? MakeLinearBrush("#FFE81C5A", "#FF731D3A", 90)
                : MakeLinearBrush("#FF9AA1AB", "#FF59616D", 90);
            canvas.Children.Add(rightFin);

            var body = new System.Windows.Shapes.Path();
            body.Data = Geometry.Parse("M 36,3 C 22,15 18,35 18,57 C 18,67 25,74 36,79 C 47,74 54,67 54,57 C 54,35 50,15 36,3 Z");
            body.Fill = useColor
                ? MakeLinearBrush("#FFFFFFFF", "#FFD9C6CD", 32)
                : MakeLinearBrush("#FFD1D5DB", "#FF737B87", 32);
            body.Stroke = useColor ? BrushFrom("#D9FFFFFF") : BrushFrom("#FFBBC0C8");
            body.StrokeThickness = 1;
            canvas.Children.Add(body);

            var bodyShade = new System.Windows.Shapes.Path();
            bodyShade.Data = Geometry.Parse("M 36,3 C 48,17 51,36 50,58 C 48,66 43,72 36,79 C 47,74 54,67 54,57 C 54,35 50,15 36,3 Z");
            bodyShade.Fill = useColor ? BrushFrom("#503C1722") : BrushFrom("#35545A64");
            canvas.Children.Add(bodyShade);

            var window = new Ellipse();
            window.Width = 18;
            window.Height = 18;
            Canvas.SetLeft(window, 27);
            Canvas.SetTop(window, 27);
            window.Fill = useColor
                ? MakeLinearBrush("#FFFFA4C2", "#FFE81C5A", 45)
                : MakeLinearBrush("#FFB5BBC3", "#FF68717C", 45);
            window.Stroke = useColor ? BrushFrom("#FFFFFFFF") : BrushFrom("#FFD4D7DC");
            window.StrokeThickness = 2;
            if (useColor)
            {
                window.Effect = new DropShadowEffect
                {
                    BlurRadius = 9,
                    ShadowDepth = 0,
                    Opacity = 0.7,
                    Color = Color.FromRgb(232, 28, 90)
                };
            }
            canvas.Children.Add(window);

            var seam = new Line();
            seam.X1 = 23;
            seam.Y1 = 57;
            seam.X2 = 49;
            seam.Y2 = 57;
            seam.Stroke = useColor ? BrushFrom("#668C4A61") : BrushFrom("#66656D78");
            seam.StrokeThickness = 1;
            canvas.Children.Add(seam);
            return canvas;
        }

        private void BoostButtonMouseEnter(object sender, MouseEventArgs e)
        {
            if (animationRunning)
            {
                return;
            }
            AnimateRocketScale(1.08, 155);
            if (!boostActive)
            {
                AnimateRocketColor(true, 180);
            }
        }

        private void BoostButtonMouseLeave(object sender, MouseEventArgs e)
        {
            if (animationRunning)
            {
                return;
            }
            AnimateRocketScale(1, 180);
            if (!boostActive)
            {
                AnimateRocketColor(false, 210);
                flameLayer.Opacity = 0;
            }
        }

        private void BoostButtonClick(object sender, RoutedEventArgs e)
        {
            if (animationRunning || boostActive)
            {
                return;
            }
            StartBoost();
        }

        private void StartBoost()
        {
            animationRunning = true;
            departureFinished = false;
            boostReady = false;
            boostButton.IsEnabled = false;
            AnimateRocketColor(true, 110);
            AnimateRocketScale(1.08, 100);
            flameLayer.Opacity = 1;
            caption.Text = "АКТИВИРУЮ BOOST...";
            caption.Foreground = BrushFrom("#FFFF8BAF");

            if (!LaunchBoostScript())
            {
                HandleBoostFailure("BOOST НЕ ЗАПУЩЕН");
                return;
            }

            PlayDeparture();
        }

        private bool LaunchBoostScript()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            foreach (string argument in arguments)
            {
                if (string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase))
                {
                    var demoTimer = new DispatcherTimer();
                    demoTimer.Interval = TimeSpan.FromMilliseconds(950);
                    demoTimer.Tick += delegate
                    {
                        demoTimer.Stop();
                        MarkBoostReady();
                    };
                    demoTimer.Start();
                    return true;
                }
            }

            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string scriptPath = System.IO.Path.Combine(baseDirectory, "Game-Boost.ps1");
                if (!File.Exists(scriptPath))
                {
                    throw new FileNotFoundException("Game-Boost.ps1 не найден рядом с приложением.", scriptPath);
                }

                readinessSignalPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "MajesticBoost-ready-" + Process.GetCurrentProcess().Id + ".flag");
                if (File.Exists(readinessSignalPath))
                {
                    File.Delete(readinessSignalPath);
                }

                string powershell = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell\\v1.0\\powershell.exe");
                var scriptArguments = new StringBuilder();
                scriptArguments.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
                scriptArguments.Append(scriptPath);
                scriptArguments.Append("\"");
                if (keepDiscordToggle == null || keepDiscordToggle.IsChecked != true)
                {
                    scriptArguments.Append(" -CloseDiscord");
                }
                if (keepEpicToggle == null || keepEpicToggle.IsChecked != true)
                {
                    scriptArguments.Append(" -CloseEpic");
                }
                if (keepSteamToggle == null || keepSteamToggle.IsChecked != true)
                {
                    scriptArguments.Append(" -CloseSteam");
                }
                scriptArguments.Append(" -ReadySignalPath \"");
                scriptArguments.Append(readinessSignalPath);
                scriptArguments.Append("\"");

                var startInfo = new ProcessStartInfo();
                startInfo.FileName = powershell;
                startInfo.Arguments = scriptArguments.ToString();
                startInfo.WorkingDirectory = baseDirectory;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                boostProcess = Process.Start(startInfo);

                readinessDeadline = DateTime.Now.AddSeconds(20);
                readinessTimer = new DispatcherTimer();
                readinessTimer.Interval = TimeSpan.FromMilliseconds(120);
                readinessTimer.Tick += ReadinessTimerTick;
                readinessTimer.Start();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ReadinessTimerTick(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(readinessSignalPath) && File.Exists(readinessSignalPath))
            {
                readinessTimer.Stop();
                TryDeleteReadinessSignal();
                MarkBoostReady();
                return;
            }

            bool processFailed = boostProcess != null && boostProcess.HasExited && boostProcess.ExitCode != 0;
            if (processFailed || DateTime.Now >= readinessDeadline)
            {
                readinessTimer.Stop();
                HandleBoostFailure("BOOST НЕ ЗАПУЩЕН");
            }
        }

        private void PlayDeparture()
        {
            var duration = TimeSpan.FromMilliseconds(620);
            var x = MakeEaseAnimation(0, 152, duration, EasingMode.EaseIn);
            var y = MakeEaseAnimation(0, -108, duration, EasingMode.EaseIn);
            var opacity = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(560));
            opacity.BeginTime = TimeSpan.FromMilliseconds(60);
            opacity.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            x.Completed += delegate
            {
                departureFinished = true;
                if (boostReady)
                {
                    PlayReturn();
                }
            };
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, x);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            rocket.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        private void MarkBoostReady()
        {
            if (boostReady || boostActive)
            {
                return;
            }
            boostReady = true;
            StartStarfield();
            caption.Text = "BOOST АКТИВЕН";
            caption.Foreground = BrushFrom("#FFE81C5A");
            if (departureFinished)
            {
                PlayReturn();
            }
        }

        private void PlayReturn()
        {
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            flightTranslation.X = -152;
            flightTranslation.Y = 104;
            rocket.Opacity = 0;

            var duration = TimeSpan.FromMilliseconds(760);
            var x = MakeEaseAnimation(-152, 0, duration, EasingMode.EaseOut);
            var y = MakeEaseAnimation(104, 0, duration, EasingMode.EaseOut);
            var opacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(470));
            opacity.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            x.Completed += delegate
            {
                flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
                flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                rocket.BeginAnimation(UIElement.OpacityProperty, null);
                flightTranslation.X = 0;
                flightTranslation.Y = 0;
                rocket.Opacity = 1;
                rocketScale.ScaleX = 1;
                rocketScale.ScaleY = 1;
                boostActive = true;
                animationRunning = false;
                boostButton.IsEnabled = true;
                StartRocketFloat();
            };
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, x);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            rocket.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        private void HandleBoostFailure(string message)
        {
            if (readinessTimer != null)
            {
                readinessTimer.Stop();
            }
            TryDeleteReadinessSignal();
            boostReady = false;
            departureFinished = false;
            flightTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            flightTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            rocket.BeginAnimation(UIElement.OpacityProperty, null);
            flightTranslation.X = 0;
            flightTranslation.Y = 0;
            rocket.Opacity = 1;
            flameLayer.Opacity = 0;
            AnimateRocketColor(false, 180);
            AnimateRocketScale(1, 160);
            caption.Text = message;
            caption.Foreground = BrushFrom("#FFFF667A");
            animationRunning = false;
            boostButton.IsEnabled = true;
        }

        private void StartStarfield()
        {
            if (starField.Opacity > 0.5)
            {
                return;
            }
            var appear = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320));
            starField.BeginAnimation(UIElement.OpacityProperty, appear);

            for (int index = 0; index < stars.Count; index++)
            {
                var transforms = (TransformGroup)stars[index].RenderTransform;
                var translation = (TranslateTransform)transforms.Children[1];
                double durationMs = 1050 + (index % 6) * 180;
                var x = new DoubleAnimation(66, -112, TimeSpan.FromMilliseconds(durationMs));
                var y = new DoubleAnimation(-44, 74, TimeSpan.FromMilliseconds(durationMs));
                x.BeginTime = TimeSpan.FromMilliseconds((index % 9) * 115);
                y.BeginTime = x.BeginTime;
                x.RepeatBehavior = RepeatBehavior.Forever;
                y.RepeatBehavior = RepeatBehavior.Forever;
                translation.BeginAnimation(TranslateTransform.XProperty, x);
                translation.BeginAnimation(TranslateTransform.YProperty, y);
            }
        }

        private void StartRocketFloat()
        {
            var y = new DoubleAnimation(-3.5, 3.5, TimeSpan.FromMilliseconds(1250));
            y.AutoReverse = true;
            y.RepeatBehavior = RepeatBehavior.Forever;
            y.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            var x = new DoubleAnimation(-1.6, 1.6, TimeSpan.FromMilliseconds(1650));
            x.AutoReverse = true;
            x.RepeatBehavior = RepeatBehavior.Forever;
            x.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            floatTranslation.BeginAnimation(TranslateTransform.YProperty, y);
            floatTranslation.BeginAnimation(TranslateTransform.XProperty, x);
        }

        private void AnimateRocketColor(bool colorized, int milliseconds)
        {
            double colorTarget = colorized ? 1 : 0;
            double grayTarget = colorized ? 0 : 1;
            var colorAnimation = new DoubleAnimation(colorTarget, TimeSpan.FromMilliseconds(milliseconds));
            var grayAnimation = new DoubleAnimation(grayTarget, TimeSpan.FromMilliseconds(milliseconds));
            colorAnimation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            grayAnimation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            colorRocketLayer.BeginAnimation(UIElement.OpacityProperty, colorAnimation);
            grayRocketLayer.BeginAnimation(UIElement.OpacityProperty, grayAnimation);
        }

        private void AnimateRocketScale(double target, int milliseconds)
        {
            var x = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds));
            var y = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds));
            x.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            y.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            rocketScale.BeginAnimation(ScaleTransform.ScaleXProperty, x);
            rocketScale.BeginAnimation(ScaleTransform.ScaleYProperty, y);
        }

        private static DoubleAnimation MakeEaseAnimation(double from, double to, TimeSpan duration, EasingMode mode)
        {
            var animation = new DoubleAnimation(from, to, duration);
            animation.EasingFunction = new CubicEase { EasingMode = mode };
            return animation;
        }

        private void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (updateOverlay != null && updateOverlay.ConsumesApplicationInput)
            {
                if (e.Key == Key.Escape)
                {
                    updateOverlay.HandleEscape();
                }
                e.Handled = true;
                return;
            }

            if (optimizationOverlay != null && optimizationOverlay.IsFlowVisible)
            {
                if (e.Key == Key.Escape)
                {
                    optimizationOverlay.HandleEscape();
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                if (Keyboard.FocusedElement is ButtonBase)
                {
                    return;
                }
                if (!animationRunning && !boostActive)
                {
                    StartBoost();
                }
                e.Handled = true;
            }
        }

        private async void BoostWindowLoaded(object sender, RoutedEventArgs e)
        {
            bool canContinue = true;
            if (updateOverlay != null)
            {
                canContinue = await updateOverlay.CheckForUpdatesAsync();
            }
            if (!canContinue)
            {
                return;
            }

            boostButton.IsEnabled = true;
            if (optimizationOverlay != null)
            {
                optimizationOverlay.ShowIfRequired();
            }
        }

        private void BoostWindowClosing(object sender, CancelEventArgs e)
        {
            if (updateOverlay != null && updateOverlay.ShouldCancelWindowClose())
            {
                e.Cancel = true;
                return;
            }
            if (optimizationOverlay != null && optimizationOverlay.ShouldCancelWindowClose())
            {
                e.Cancel = true;
            }
        }

        private void WindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            var element = e.OriginalSource as DependencyObject;
            while (element != null)
            {
                if (element is ButtonBase)
                {
                    return;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }

        private void WindowClosed(object sender, EventArgs e)
        {
            if (readinessTimer != null)
            {
                readinessTimer.Stop();
            }
            TryDeleteReadinessSignal();
        }

        private void TryDeleteReadinessSignal()
        {
            try
            {
                if (!string.IsNullOrEmpty(readinessSignalPath) && File.Exists(readinessSignalPath))
                {
                    File.Delete(readinessSignalPath);
                }
            }
            catch { }
        }

        private static Button MakeWindowButton(string accessibleName, bool isClose)
        {
            var button = new Button();
            button.Width = 30;
            button.Height = 30;
            var backgroundBrush = new SolidColorBrush(Color.FromArgb(0, 27, 27, 27));
            var glyphBrush = new SolidColorBrush(Color.FromRgb(139, 139, 139));
            button.Foreground = glyphBrush;
            button.Background = backgroundBrush;
            button.BorderThickness = new Thickness(0);
            button.Cursor = Cursors.Hand;
            button.FocusVisualStyle = null;
            button.ToolTip = accessibleName;
            button.Template = MakeChromeButtonTemplate();
            AutomationProperties.SetName(button, accessibleName);

            var glyphCanvas = new Canvas();
            glyphCanvas.Width = 24;
            glyphCanvas.Height = 24;
            glyphCanvas.Background = Brushes.Transparent;
            glyphCanvas.IsHitTestVisible = false;
            if (isClose)
            {
                var closeGlyph = new System.Windows.Shapes.Path();
                closeGlyph.Data = Geometry.Parse("M 6,6 L 18,18 M 18,6 L 6,18");
                closeGlyph.Stroke = glyphBrush;
                closeGlyph.StrokeThickness = 2;
                closeGlyph.StrokeStartLineCap = PenLineCap.Round;
                closeGlyph.StrokeEndLineCap = PenLineCap.Round;
                glyphCanvas.Children.Add(closeGlyph);
            }
            else
            {
                var minimizeGlyph = new Rectangle();
                minimizeGlyph.Width = 20;
                minimizeGlyph.Height = 2;
                minimizeGlyph.RadiusX = 1;
                minimizeGlyph.RadiusY = 1;
                minimizeGlyph.Fill = glyphBrush;
                Canvas.SetLeft(minimizeGlyph, 2);
                Canvas.SetTop(minimizeGlyph, 17);
                glyphCanvas.Children.Add(minimizeGlyph);
            }

            var glyphHost = new Viewbox();
            glyphHost.Width = 20;
            glyphHost.Height = 20;
            glyphHost.Stretch = Stretch.Uniform;
            glyphHost.Child = glyphCanvas;
            glyphHost.IsHitTestVisible = false;
            button.Content = glyphHost;

            var lift = new TranslateTransform();
            button.RenderTransform = lift;
            button.RenderTransformOrigin = new Point(0.5, 0.5);

            button.MouseEnter += delegate
            {
                Panel.SetZIndex(button, 2);
                var colorEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var liftEase = new SineEase { EasingMode = EasingMode.EaseInOut };
                backgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(
                        isClose ? Color.FromRgb(231, 24, 42) : Color.FromRgb(45, 45, 45),
                        TimeSpan.FromMilliseconds(220)) { EasingFunction = colorEase });
                glyphBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Colors.White, TimeSpan.FromMilliseconds(220)) { EasingFunction = colorEase });
                lift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(-1, TimeSpan.FromMilliseconds(320)) { EasingFunction = liftEase });
            };
            button.MouseLeave += delegate
            {
                Panel.SetZIndex(button, 0);
                var colorEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var liftEase = new SineEase { EasingMode = EasingMode.EaseInOut };
                backgroundBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromArgb(0, 27, 27, 27), TimeSpan.FromMilliseconds(260)) { EasingFunction = colorEase });
                glyphBrush.BeginAnimation(
                    SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(139, 139, 139), TimeSpan.FromMilliseconds(240)) { EasingFunction = colorEase });
                lift.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = liftEase });
            };
            return button;
        }

        private static ControlTemplate MakeTransparentButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            template.VisualTree = presenter;
            return template;
        }

        private static ControlTemplate MakeTransparentCheckBoxTemplate()
        {
            var template = new ControlTemplate(typeof(CheckBox));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            template.VisualTree = presenter;
            return template;
        }

        private static ControlTemplate MakeChromeButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static TextBlock MakeText(string text, double size, string color, FontWeight weight)
        {
            var block = new TextBlock();
            block.Text = text;
            block.FontSize = size;
            block.FontWeight = weight;
            block.Foreground = BrushFrom(color);
            TextOptions.SetTextFormattingMode(block, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(block, TextRenderingMode.ClearType);
            return block;
        }

        private static FontFamily LoadMajesticFontFamily()
        {
            try
            {
                string fontDirectory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost",
                    "Fonts");
                Directory.CreateDirectory(fontDirectory);

                string regularFont = System.IO.Path.Combine(fontDirectory, "ProximaNova-Regular.ttf");
                if (!File.Exists(regularFont))
                {
                    ExtractMajesticFonts(fontDirectory);
                }

                if (File.Exists(regularFont))
                {
                    string directoryUri = fontDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                        + System.IO.Path.DirectorySeparatorChar;
                    return new FontFamily(new Uri(directoryUri, UriKind.Absolute), "./#Proxima Nova");
                }
            }
            catch
            {
                // Majestic may not be installed; Segoe UI keeps the app usable.
            }

            return new FontFamily("Segoe UI");
        }

        private static FontFamily LoadMajesticSemiboldFontFamily()
        {
            try
            {
                string fontDirectory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost",
                    "Fonts");
                string semiboldFont = System.IO.Path.Combine(fontDirectory, "ProximaNova-Semibold.ttf");
                if (!File.Exists(semiboldFont))
                {
                    Directory.CreateDirectory(fontDirectory);
                    ExtractMajesticFonts(fontDirectory);
                }

                if (File.Exists(semiboldFont))
                {
                    string directoryUri = fontDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                        + System.IO.Path.DirectorySeparatorChar;
                    return new FontFamily(new Uri(directoryUri, UriKind.Absolute), "./#Proxima Nova");
                }
            }
            catch
            {
                // Use the closest system fallback if Majestic is unavailable.
            }

            return new FontFamily("Segoe UI Semibold");
        }

        private static string GetMajesticLauncherVersion()
        {
            try
            {
                string launcherPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticLauncher",
                    "Majestic Launcher.exe");
                if (File.Exists(launcherPath))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(launcherPath);
                    Version version;
                    if (Version.TryParse(info.FileVersion, out version))
                    {
                        return string.Format(
                            "v. {0}.{1}.{2}",
                            version.Major,
                            version.Minor,
                            Math.Max(0, version.Build));
                    }
                }
            }
            catch
            {
                // Fallback stays available if the launcher is being updated.
            }

            return "v. 1.2.0";
        }

        private static void ExtractMajesticFonts(string destinationDirectory)
        {
            string asarPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MajesticLauncher",
                "resources",
                "app.asar");
            if (!File.Exists(asarPath))
            {
                return;
            }

            using (var stream = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                reader.ReadUInt32();
                uint headerSize = reader.ReadUInt32();
                reader.ReadUInt32();
                uint jsonLength = reader.ReadUInt32();
                if (jsonLength == 0 || jsonLength > 64 * 1024 * 1024)
                {
                    return;
                }

                string header = Encoding.UTF8.GetString(reader.ReadBytes((int)jsonLength));
                long dataOffset = 8L + headerSize;
                var pattern = new Regex(
                    @"ProximaNova-(?<weight>Black|Bold|Regular|Semibold)-[^""\\]+\.ttf"":\{""size"":(?<size>\d+),""offset"":""(?<offset>\d+)""",
                    RegexOptions.CultureInvariant);

                foreach (Match match in pattern.Matches(header))
                {
                    int size;
                    long offset;
                    if (!int.TryParse(match.Groups["size"].Value, out size)
                        || !long.TryParse(match.Groups["offset"].Value, out offset)
                        || size <= 0
                        || dataOffset + offset + size > stream.Length)
                    {
                        continue;
                    }

                    string targetPath = System.IO.Path.Combine(
                        destinationDirectory,
                        "ProximaNova-" + match.Groups["weight"].Value + ".ttf");
                    stream.Position = dataOffset + offset;
                    byte[] bytes = reader.ReadBytes(size);
                    if (bytes.Length == size)
                    {
                        File.WriteAllBytes(targetPath, bytes);
                    }
                }
            }
        }

        private static Brush MakeLinearBrush(string from, string to, double angle)
        {
            double radians = angle * Math.PI / 180.0;
            double x = Math.Cos(radians) * 0.5;
            double y = Math.Sin(radians) * 0.5;
            var brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0.5 - x, 0.5 - y);
            brush.EndPoint = new Point(0.5 + x, 0.5 + y);
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(from), 0));
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(to), 1));
            brush.Freeze();
            return brush;
        }

        private static Brush BrushFrom(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }

        private static ImageSource BuildWindowIcon()
        {
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(
                BrushFrom("#FF1B1B1B"),
                new Pen(BrushFrom("#FFE81C5A"), 1.5),
                new RectangleGeometry(new Rect(1, 1, 30, 30), 8, 8)));
            group.Children.Add(new GeometryDrawing(
                MakeLinearBrush("#FFFFFFFF", "#FFFF9AB7", 90),
                null,
                Geometry.Parse("M 16,5 C 10,10 9,18 10,23 L 16,27 L 22,23 C 23,18 22,10 16,5 Z")));
            group.Children.Add(new GeometryDrawing(
                BrushFrom("#FFFF6B57"),
                null,
                Geometry.Parse("M 13,24 C 13,28 15,30 16,31 C 17,30 19,28 19,24 Z")));
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }
}
