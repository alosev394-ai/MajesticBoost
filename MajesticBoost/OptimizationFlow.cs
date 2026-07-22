using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MajesticBoost
{
    internal sealed class OptimizationFlowOverlay : Grid
    {
        private enum FlowState
        {
            Hidden,
            Consent,
            Applying,
            RebootRequired,
            RecoveryRequired,
            Restoring,
            Failure
        }

        private sealed class ScriptResult
        {
            public int ExitCode;
            public Exception Error;
            public bool ElevationCanceled;
        }

        private sealed class GlobalTransactionInfo
        {
            public string StatePath;
            public int Version;
            public string Status;
        }

        private static readonly Color BackgroundColor = Color.FromRgb(22, 22, 22);
        private static readonly Color TextColor = Color.FromRgb(244, 244, 244);
        private static readonly Color MutedColor = Color.FromRgb(142, 142, 142);
        private static readonly Color AccentColor = Color.FromRgb(232, 28, 90);
        private static readonly Color ErrorColor = Color.FromRgb(231, 24, 42);

        private readonly Window owner;
        private readonly string[] arguments;
        private readonly FontFamily regularFont;
        private readonly FontFamily semiboldFont;
        private readonly Border card;
        private readonly Grid cardContent;
        private readonly string stateDirectory;
        private readonly string pendingMarkerPath;
        private readonly string completedMarkerPath;
        private readonly bool demoMode;

        private FlowState state;
        private string pendingStatePath;
        private bool rollbackRequested;
        private bool allowOwnerClose;
        private Button preferredFocusButton;

        public OptimizationFlowOverlay(
            Window ownerWindow,
            string[] launchArguments,
            FontFamily normalFont,
            FontFamily boldFont)
        {
            owner = ownerWindow;
            arguments = launchArguments ?? new string[0];
            regularFont = normalFont ?? new FontFamily("Segoe UI");
            semiboldFont = boldFont ?? regularFont;
            demoMode = HasArgument("--demo-setup");

            stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MajesticBoost");
            pendingMarkerPath = Path.Combine(stateDirectory, "optimization-pending.json");
            completedMarkerPath = Path.Combine(stateDirectory, "optimization-completed-v1.marker");

            Width = 424;
            Height = 444;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Background = new SolidColorBrush(BackgroundColor);
            Visibility = Visibility.Collapsed;
            Focusable = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetName(this, "Первоначальная настройка производительности");

            card = new Border
            {
                Width = 386,
                Height = 410,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(24)
            };
            Children.Add(card);

            cardContent = new Grid();
            card.Child = cardContent;

            PreviewKeyDown += OverlayPreviewKeyDown;
            owner.Activated += delegate
            {
                if (IsFlowVisible)
                {
                    FocusPreferredButton();
                }
            };
        }

        public event EventHandler RequestApplicationClose;

        public bool IsFlowVisible
        {
            get { return Visibility == Visibility.Visible; }
        }

        public void ShowIfRequired()
        {
            if (IsFlowVisible)
            {
                return;
            }

            allowOwnerClose = false;
            rollbackRequested = false;

            if (HasArgument("--skip-setup"))
            {
                HideFlow();
                return;
            }

            if (demoMode)
            {
                if (HasArgument("--demo-recovery"))
                {
                    pendingStatePath = Path.Combine(Path.GetTempPath(), "MajesticBoost-demo-state.json");
                    ShowRecoveryRequired("ApplyFailed");
                    return;
                }
                ShowConsent(null);
                return;
            }

            DateTime appliedUtc;
            string markerStatePath;
            bool hasLocalPending = TryReadPendingMarker(out markerStatePath, out appliedUtc);

            GlobalTransactionInfo globalTransaction;
            bool hasGlobalTransaction = TryReadGlobalTransaction(out globalTransaction);
            bool localMatchesGlobal =
                hasLocalPending &&
                hasGlobalTransaction &&
                PathsEqual(markerStatePath, globalTransaction.StatePath);

            string completedStatePath;
            bool completedMatchesGlobal =
                hasGlobalTransaction &&
                TryReadCompletedMarker(out completedStatePath) &&
                PathsEqual(completedStatePath, globalTransaction.StatePath);

            if (hasGlobalTransaction && globalTransaction.Version >= 2)
            {
                if (string.Equals(globalTransaction.Status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    pendingStatePath = globalTransaction.StatePath;
                    if (localMatchesGlobal && HasRestartedSince(appliedUtc))
                    {
                        if (TryMarkCompleted(globalTransaction.StatePath))
                        {
                            TryDeleteFile(pendingMarkerPath);
                            HideFlow();
                            return;
                        }
                    }

                    if (completedMatchesGlobal)
                    {
                        HideFlow();
                        return;
                    }

                    ShowRebootRequired();
                    return;
                }

                if (IsRecoveryStatus(globalTransaction.Status))
                {
                    pendingStatePath = globalTransaction.StatePath;
                    ShowRecoveryRequired(globalTransaction.Status);
                    return;
                }
            }

            // A pointer is authoritative when it exists. Do not act on an unrelated
            // per-user marker if the machine-wide transaction points elsewhere.
            if (hasGlobalTransaction && hasLocalPending && !localMatchesGlobal)
            {
                hasLocalPending = false;
            }

            // Version 1 backups predate transactional recovery. Without a matching
            // local marker they remain available for the Apply script to adopt, but
            // this unelevated UI must not guess that they are an interrupted run.
            if (hasGlobalTransaction && globalTransaction.Version < 2 && !hasLocalPending)
            {
                ShowConsent(null);
                return;
            }

            if (hasLocalPending)
            {
                pendingStatePath = markerStatePath;
                if (HasRestartedSince(appliedUtc))
                {
                    if (TryMarkCompleted(markerStatePath))
                    {
                        TryDeleteFile(pendingMarkerPath);
                        HideFlow();
                        return;
                    }
                }

                ShowRebootRequired();
                return;
            }

            if (File.Exists(completedMarkerPath))
            {
                HideFlow();
                return;
            }

            ShowConsent(null);
        }

        public void HandleEscape()
        {
            if (!IsFlowVisible)
            {
                return;
            }

            if (state == FlowState.Consent)
            {
                CloseApplication();
            }
            else if (state == FlowState.RebootRequired)
            {
                BeginRestoreAndClose();
            }
            else if (state == FlowState.Applying)
            {
                rollbackRequested = true;
                SetApplyingCancellationMessage();
            }
            else if (state == FlowState.RecoveryRequired)
            {
                FocusPreferredButton();
            }
            else if (state == FlowState.Failure)
            {
                CloseApplication();
            }
        }

        public bool ShouldCancelWindowClose()
        {
            if (!IsFlowVisible || allowOwnerClose)
            {
                return false;
            }

            owner.Dispatcher.BeginInvoke(new Action(HandleEscape));
            return true;
        }

        private bool HasArgument(string expected)
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ShowConsent(string notice)
        {
            state = FlowState.Consent;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            var header = BuildHeader(
                "ПЕРЕД ПРОДОЛЖЕНИЕМ",
                "Majestic Boost подготовит Windows для максимального FPS.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
            body.Children.Add(MakeText(
                "Сохраним исходные значения, затем программа:",
                10.5,
                TextColor,
                semiboldFont,
                FontWeights.SemiBold));
            body.Children.Add(MakeConsentBullet("включит максимум питания, Game Mode и HAGS;"));
            body.Children.Add(MakeConsentBullet("отключит DVR, виджеты, прозрачность и NVIDIA Overlay;"));
            body.Children.Add(MakeConsentBullet("снизит графику GTA V и упростит конфиги Majestic;"));
            body.Children.Add(MakeConsentBullet("остановит Wallpaper Engine и SPUser, отключит обновление VirtualPad;"));
            body.Children.Add(MakeConsentBullet("усилит Defender: PUA, облако, безопасную отправку образцов и проверку съёмных носителей."));

            var safetyText = MakeText(
                "Меняются только отличающиеся значения. Резервная копия позволяет безопасно вернуть изменения.",
                9.4,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            safetyText.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(safetyText);

            if (!string.IsNullOrWhiteSpace(notice))
            {
                var noticeText = MakeText(notice, 10.5, ErrorColor, semiboldFont, FontWeights.SemiBold);
                noticeText.Margin = new Thickness(0, 9, 0, 0);
                body.Children.Add(noticeText);
            }

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var cancelButton = MakeActionButton("ОТМЕНИТЬ", false);
            cancelButton.Width = 118;
            cancelButton.Click += delegate { CloseApplication(); };
            AutomationProperties.SetName(cancelButton, "Отменить первоначальную настройку и закрыть программу");
            buttons.Children.Add(cancelButton);

            var continueButton = MakeActionButton("ПРОДОЛЖИТЬ", true);
            continueButton.Width = 150;
            continueButton.Margin = new Thickness(10, 0, 0, 0);
            continueButton.IsDefault = true;
            continueButton.Click += ContinueButtonClick;
            AutomationProperties.SetName(continueButton, "Продолжить и применить оптимизацию производительности");
            buttons.Children.Add(continueButton);
            preferredFocusButton = continueButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private async void ContinueButtonClick(object sender, RoutedEventArgs e)
        {
            await ApplyOptimizationAsync();
        }

        private async Task ApplyOptimizationAsync()
        {
            state = FlowState.Applying;
            rollbackRequested = false;
            ShowWorking(
                "ПРИМЕНЯЕМ НАСТРОЙКИ",
                "Windows запросит права администратора. Не закрывайте это окно до завершения.",
                "ОТМЕНИТЬ");

            if (demoMode)
            {
                await Task.Delay(900);
                pendingStatePath = Path.Combine(Path.GetTempPath(), "MajesticBoost-demo-state.json");
                if (rollbackRequested)
                {
                    await RestoreAndCloseAsync();
                }
                else
                {
                    ShowRebootRequired();
                }
                return;
            }

            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MaxFPS-Apply.ps1");
            if (!File.Exists(scriptPath))
            {
                ShowFailure(
                    "Файл MaxFPS-Apply.ps1 не найден рядом с программой.",
                    false);
                return;
            }

            string resultPath = MakeTemporaryResultPath("apply");
            ScriptResult execution = await RunElevatedPowerShellAsync(
                scriptPath,
                "-AdoptExistingState -ResultPath " + QuoteArgument(resultPath));

            if (execution.ElevationCanceled)
            {
                TryDeleteFile(resultPath);
                ShowConsent("Запрос прав администратора был отменён. Изменения не внесены.");
                return;
            }

            if (execution.Error != null || execution.ExitCode != 0)
            {
                string failedStatePath;
                bool canRestorePartialChanges =
                    TryReadStatePath(resultPath, out failedStatePath) &&
                    Path.IsPathRooted(failedStatePath) &&
                    File.Exists(failedStatePath);
                TryDeleteFile(resultPath);
                if (canRestorePartialChanges)
                {
                    pendingStatePath = failedStatePath;
                    try
                    {
                        WritePendingMarker(failedStatePath, DateTime.UtcNow);
                    }
                    catch
                    {
                    }
                    await RestoreAndCloseAsync();
                    return;
                }
                ShowFailure(
                    "Не удалось применить оптимизацию. Изменения не считаются завершёнными.",
                    false);
                return;
            }

            string statePath;
            if (!TryReadStatePath(resultPath, out statePath) ||
                !Path.IsPathRooted(statePath) ||
                !File.Exists(statePath))
            {
                TryDeleteFile(resultPath);
                ShowFailure(
                    "Оптимизация завершилась без корректного файла резервной копии. Перезапустите настройку.",
                    false);
                return;
            }

            TryDeleteFile(resultPath);
            pendingStatePath = statePath;
            bool pendingMarkerWritten = true;
            try
            {
                WritePendingMarker(statePath, DateTime.UtcNow);
            }
            catch
            {
                pendingMarkerWritten = false;
            }
            if (!pendingMarkerWritten)
            {
                // Without a durable per-user marker the next launch cannot prove
                // whether a reboot happened. Roll back immediately instead of
                // leaving an untracked machine-wide optimization behind.
                await RestoreAndCloseAsync();
                return;
            }

            if (rollbackRequested)
            {
                await RestoreAndCloseAsync();
                return;
            }

            ShowRebootRequired();
        }

        private void ShowRebootRequired()
        {
            state = FlowState.RebootRequired;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            var header = BuildHeader(
                "ОПТИМИЗАЦИЯ ГОТОВА",
                "Настройки применены и сохранены для безопасного отката.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 308,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var rebootBadge = new Border
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                BorderBrush = new SolidColorBrush(AccentColor),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            var rebootGlyph = MakeText("↻", 31, TextColor, semiboldFont, FontWeights.SemiBold);
            rebootGlyph.TextAlignment = TextAlignment.Center;
            rebootGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            rebootGlyph.VerticalAlignment = VerticalAlignment.Center;
            rebootGlyph.Margin = new Thickness(0, -3, 0, 0);
            rebootBadge.Child = rebootGlyph;
            body.Children.Add(rebootBadge);

            var title = MakeText(
                "ТРЕБУЕТСЯ ПЕРЕЗАГРУЗКА",
                14,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.TextAlignment = TextAlignment.Center;
            body.Children.Add(title);

            var description = MakeText(
                "Перезагрузите компьютер, чтобы Windows полностью применила изменения производительности.",
                11,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            description.TextAlignment = TextAlignment.Center;
            description.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(description);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var cancelButton = MakeActionButton("ОТМЕНИТЬ", false);
            cancelButton.Width = 112;
            cancelButton.Click += delegate { BeginRestoreAndClose(); };
            AutomationProperties.SetName(cancelButton, "Отменить оптимизацию, восстановить настройки и закрыть программу");
            buttons.Children.Add(cancelButton);

            var restartButton = MakeActionButton("ПЕРЕЗАГРУЗИТЬ СЕЙЧАС", true);
            restartButton.Width = 194;
            restartButton.Margin = new Thickness(10, 0, 0, 0);
            restartButton.IsDefault = true;
            restartButton.Click += RestartButtonClick;
            AutomationProperties.SetName(restartButton, "Перезагрузить компьютер сейчас");
            buttons.Children.Add(restartButton);
            preferredFocusButton = restartButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void ShowRecoveryRequired(string transactionStatus)
        {
            state = FlowState.RecoveryRequired;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            var header = BuildHeader(
                "НУЖНО ВОССТАНОВЛЕНИЕ",
                "Найдена незавершённая системная транзакция.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            string detail;
            if (string.Equals(transactionStatus, "RestoreIncomplete", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Предыдущий откат завершился не полностью. Резервная копия сохранена для безопасного повтора.";
            }
            else if (string.Equals(transactionStatus, "Restoring", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Предыдущее восстановление было прервано. Его нужно продолжить по той же резервной копии.";
            }
            else if (string.Equals(transactionStatus, "Applying", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Применение настроек было прервано до завершения. Изменённые параметры нужно вернуть.";
            }
            else
            {
                detail = "Применение настроек завершилось ошибкой. Изменённые параметры нужно вернуть из резервной копии.";
            }

            var body = new StackPanel
            {
                Width = 306,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var recoveryBadge = new Border
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                BorderBrush = new SolidColorBrush(AccentColor),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 15)
            };
            var recoveryGlyph = MakeText("↶", 30, TextColor, semiboldFont, FontWeights.SemiBold);
            recoveryGlyph.TextAlignment = TextAlignment.Center;
            recoveryGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            recoveryGlyph.VerticalAlignment = VerticalAlignment.Center;
            recoveryGlyph.Margin = new Thickness(0, -2, 0, 0);
            recoveryBadge.Child = recoveryGlyph;
            body.Children.Add(recoveryBadge);

            var title = MakeText(
                "ТРЕБУЕТСЯ БЕЗОПАСНЫЙ ОТКАТ",
                13.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            title.TextAlignment = TextAlignment.Center;
            body.Children.Add(title);

            var description = MakeText(detail, 10.7, MutedColor, regularFont, FontWeights.Normal);
            description.TextAlignment = TextAlignment.Center;
            description.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(description);

            var closeNotice = MakeText(
                "Сначала выполните попытку восстановления.",
                10,
                ErrorColor,
                semiboldFont,
                FontWeights.SemiBold);
            closeNotice.TextAlignment = TextAlignment.Center;
            closeNotice.Margin = new Thickness(0, 9, 0, 0);
            body.Children.Add(closeNotice);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            var buttons = BuildButtonRow();
            var restoreButton = MakeActionButton("ВОССТАНОВИТЬ", true);
            restoreButton.Width = 174;
            restoreButton.IsDefault = true;
            restoreButton.Click += delegate { BeginRestoreAndClose(); };
            AutomationProperties.SetName(restoreButton, "Восстановить исходные настройки из найденной резервной копии");
            buttons.Children.Add(restoreButton);
            preferredFocusButton = restoreButton;

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void RestartButtonClick(object sender, RoutedEventArgs e)
        {
            if (demoMode)
            {
                CloseApplication();
                return;
            }

            try
            {
                allowOwnerClose = true;
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    Arguments = "/r /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                allowOwnerClose = false;
                ShowFailure("Не удалось запустить перезагрузку: " + ex.Message, true);
            }
        }

        private async void BeginRestoreAndClose()
        {
            if (state == FlowState.Restoring)
            {
                return;
            }
            await RestoreAndCloseAsync();
        }

        private async Task RestoreAndCloseAsync()
        {
            state = FlowState.Restoring;
            ShowWorking(
                "ВОССТАНАВЛИВАЕМ НАСТРОЙКИ",
                "Возвращаем только те параметры, которые Majestic Boost изменил при настройке.",
                null);

            if (demoMode)
            {
                await Task.Delay(700);
                CloseApplication();
                return;
            }

            string validatedStatePath;
            if (!TryValidateStatePath(pendingStatePath, out validatedStatePath))
            {
                ShowFailure(
                    "Файл резервной копии не найден. Автоматический откат остановлен, чтобы не менять систему вслепую.",
                    true);
                return;
            }
            pendingStatePath = validatedStatePath;

            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MaxFPS-Restore.ps1");
            if (!File.Exists(scriptPath))
            {
                ShowFailure("Файл MaxFPS-Restore.ps1 не найден рядом с программой.", true);
                return;
            }

            string resultPath = MakeTemporaryResultPath("restore");
            string scriptArguments =
                "-StatePath " + QuoteArgument(pendingStatePath) +
                " -ResultPath " + QuoteArgument(resultPath);
            ScriptResult execution = await RunElevatedPowerShellAsync(scriptPath, scriptArguments);

            if (execution.ElevationCanceled)
            {
                TryDeleteFile(resultPath);
                ShowFailure(
                    "Откат отменён в запросе прав администратора. Настройки пока остаются применёнными.",
                    true);
                return;
            }

            if (execution.Error != null || execution.ExitCode != 0)
            {
                TryDeleteFile(resultPath);
                ShowFailure(
                    "Не удалось полностью восстановить исходные настройки. Резервная копия сохранена для повторной попытки.",
                    true);
                return;
            }

            TryDeleteFile(resultPath);
            TryDeleteFile(pendingMarkerPath);
            TryDeleteFile(completedMarkerPath);
            CloseApplication();
        }

        private void ShowWorking(string title, string description, string cancelText)
        {
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            var header = BuildHeader(title, description);
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var progressTrack = new Border
            {
                Width = 300,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                ClipToBounds = true
            };
            var progressIndicator = new Border
            {
                Width = 82,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(AccentColor),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TranslateTransform(-82, 0)
            };
            progressTrack.Child = progressIndicator;
            body.Children.Add(progressTrack);

            var statusText = MakeText(
                "Это может занять несколько минут.",
                11,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            statusText.Name = "WorkingStatusText";
            statusText.TextAlignment = TextAlignment.Center;
            statusText.Margin = new Thickness(0, 18, 0, 0);
            AutomationProperties.SetName(statusText, statusText.Text);
            body.Children.Add(statusText);

            var movement = new DoubleAnimation
            {
                From = -82,
                To = 300,
                Duration = TimeSpan.FromMilliseconds(1250),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ((TranslateTransform)progressIndicator.RenderTransform).BeginAnimation(
                TranslateTransform.XProperty,
                movement);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            if (!string.IsNullOrEmpty(cancelText))
            {
                var buttons = BuildButtonRow();
                var cancelButton = MakeActionButton(cancelText, false);
                cancelButton.Width = 132;
                cancelButton.Click += delegate
                {
                    if (state == FlowState.Applying)
                    {
                        rollbackRequested = true;
                        cancelButton.IsEnabled = false;
                        SetApplyingCancellationMessage();
                    }
                };
                AutomationProperties.SetName(cancelButton, "Отменить настройку и восстановить исходные параметры");
                buttons.Children.Add(cancelButton);
                preferredFocusButton = cancelButton;
                Grid.SetRow(buttons, 2);
                cardContent.Children.Add(buttons);
                FocusPreferredButton();
            }
            else
            {
                preferredFocusButton = null;
                Focus();
            }
        }

        private void SetApplyingCancellationMessage()
        {
            var statusText = FindVisualChildByName<TextBlock>(cardContent, "WorkingStatusText");
            if (statusText == null)
            {
                return;
            }
            statusText.Text = "Завершаем текущий шаг, затем безопасно вернём изменения.";
            AutomationProperties.SetName(statusText, statusText.Text);
        }

        private void ShowFailure(string message, bool canRetryRestore)
        {
            state = FlowState.Failure;
            Visibility = Visibility.Visible;
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            var header = BuildHeader("НУЖНО ВНИМАНИЕ", "Majestic Boost остановил операцию безопасно.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var messageText = MakeText(message, 11.5, TextColor, regularFont, FontWeights.Normal);
            messageText.TextAlignment = TextAlignment.Center;
            messageText.VerticalAlignment = VerticalAlignment.Center;
            messageText.Margin = new Thickness(16, 0, 16, 0);
            Grid.SetRow(messageText, 1);
            cardContent.Children.Add(messageText);

            var buttons = BuildButtonRow();
            if (canRetryRestore)
            {
                var retryButton = MakeActionButton("ПОВТОРИТЬ ОТКАТ", true);
                retryButton.Width = 170;
                retryButton.IsDefault = true;
                retryButton.Click += delegate { BeginRestoreAndClose(); };
                AutomationProperties.SetName(retryButton, "Повторить восстановление исходных настроек");
                buttons.Children.Add(retryButton);
                preferredFocusButton = retryButton;
            }
            else
            {
                var closeButton = MakeActionButton("ЗАКРЫТЬ", false);
                closeButton.Width = 118;
                closeButton.Click += delegate { CloseApplication(); };
                buttons.Children.Add(closeButton);

                var retryButton = MakeActionButton("ПОВТОРИТЬ", true);
                retryButton.Width = 140;
                retryButton.Margin = new Thickness(10, 0, 0, 0);
                retryButton.IsDefault = true;
                retryButton.Click += ContinueButtonClick;
                buttons.Children.Add(retryButton);
                preferredFocusButton = retryButton;
            }

            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private StackPanel BuildHeader(string title, string subtitle)
        {
            var header = new StackPanel();
            var titleText = MakeText(title, 18, TextColor, semiboldFont, FontWeights.Bold);
            header.Children.Add(titleText);

            var subtitleText = MakeText(subtitle, 10.5, MutedColor, regularFont, FontWeights.Normal);
            subtitleText.Margin = new Thickness(0, 5, 0, 0);
            header.Children.Add(subtitleText);
            return header;
        }

        private TextBlock MakeBullet(string text)
        {
            var bullet = MakeText("•  " + text, 11, TextColor, regularFont, FontWeights.Normal);
            bullet.Margin = new Thickness(0, 9, 0, 0);
            return bullet;
        }

        private TextBlock MakeConsentBullet(string text)
        {
            var bullet = MakeText("•  " + text, 9.5, TextColor, regularFont, FontWeights.Normal);
            bullet.Margin = new Thickness(0, 4, 0, 0);
            bullet.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            bullet.LineHeight = 11.5;
            return bullet;
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
                Text = text,
                FontSize = size,
                FontFamily = font,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            };
        }

        private static StackPanel BuildButtonRow()
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
        }

        private Button MakeActionButton(string text, bool accentOnHover)
        {
            var baseColor = Color.FromRgb(37, 37, 37);
            var hoverColor = accentOnHover ? AccentColor : Color.FromRgb(49, 49, 49);
            var background = new SolidColorBrush(baseColor);
            var translate = new TranslateTransform();

            var button = new Button
            {
                Content = text,
                Height = 38,
                Padding = new Thickness(13, 0, 13, 0),
                Background = background,
                Foreground = new SolidColorBrush(TextColor),
                BorderThickness = new Thickness(0),
                FontFamily = semiboldFont,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Focusable = true,
                RenderTransform = translate,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Template = BuildRoundedButtonTemplate()
            };

            button.MouseEnter += delegate
            {
                if (!button.IsEnabled)
                {
                    return;
                }
                AnimateBrush(background, hoverColor, 210);
                AnimateLift(translate, -1, 240);
            };
            button.MouseLeave += delegate
            {
                AnimateBrush(background, baseColor, 260);
                AnimateLift(translate, 0, 300);
            };
            button.IsEnabledChanged += delegate
            {
                button.Opacity = button.IsEnabled ? 1.0 : 0.45;
            };
            return button;
        }

        private static ControlTemplate BuildRoundedButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static void AnimateBrush(SolidColorBrush brush, Color target, int milliseconds)
        {
            var animation = new ColorAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateLift(TranslateTransform transform, double target, int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void OverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HandleEscape();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Tab)
            {
                return;
            }

            var focused = Keyboard.FocusedElement as UIElement;
            if (focused != null)
            {
                var direction = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
                    ? FocusNavigationDirection.Previous
                    : FocusNavigationDirection.Next;
                focused.MoveFocus(new TraversalRequest(direction));
            }
            else
            {
                FocusPreferredButton();
            }
            e.Handled = true;
        }

        private void FocusPreferredButton()
        {
            if (preferredFocusButton == null)
            {
                return;
            }
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (preferredFocusButton != null && preferredFocusButton.IsVisible)
                {
                    preferredFocusButton.Focus();
                    Keyboard.Focus(preferredFocusButton);
                }
            }));
        }

        private async Task<ScriptResult> RunElevatedPowerShellAsync(string scriptPath, string scriptArguments)
        {
            return await Task.Run(delegate
            {
                var result = new ScriptResult { ExitCode = -1 };
                try
                {
                    string powershellPath = Path.Combine(
                        Environment.SystemDirectory,
                        "WindowsPowerShell\\v1.0\\powershell.exe");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = powershellPath,
                        Arguments =
                            "-NoProfile -ExecutionPolicy Bypass -File " +
                            QuoteArgument(scriptPath) + " " + scriptArguments,
                        WorkingDirectory = Path.GetDirectoryName(scriptPath),
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using (Process process = Process.Start(startInfo))
                    {
                        if (process == null)
                        {
                            throw new InvalidOperationException("Не удалось запустить PowerShell.");
                        }
                        process.WaitForExit();
                        result.ExitCode = process.ExitCode;
                    }
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223)
                    {
                        result.ElevationCanceled = true;
                    }
                    else
                    {
                        result.Error = ex;
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                }
                return result;
            });
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string MakeTemporaryResultPath(string operation)
        {
            return Path.Combine(
                Path.GetTempPath(),
                "MajesticBoost-" + operation + "-" + Guid.NewGuid().ToString("N") + ".json");
        }

        private static bool TryReadStatePath(string resultPath, out string statePath)
        {
            statePath = null;
            try
            {
                if (!File.Exists(resultPath))
                {
                    return false;
                }
                string json = File.ReadAllText(resultPath, Encoding.UTF8);
                string candidatePath;
                return
                    TryReadJsonString(json, "StateFile", out candidatePath) &&
                    TryValidateStatePath(candidatePath, out statePath);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadPendingMarker(out string statePath, out DateTime appliedUtc)
        {
            statePath = null;
            appliedUtc = DateTime.MinValue;
            try
            {
                if (!File.Exists(pendingMarkerPath))
                {
                    return false;
                }

                string json = File.ReadAllText(pendingMarkerPath, Encoding.UTF8);
                string candidatePath;
                string utcText;
                if (!TryReadJsonString(json, "StatePath", out candidatePath) ||
                    !TryReadJsonString(json, "AppliedUtc", out utcText))
                {
                    return false;
                }

                return
                    DateTime.TryParse(
                        utcText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out appliedUtc) &&
                    TryValidateStatePath(candidatePath, out statePath);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadCompletedMarker(out string statePath)
        {
            statePath = null;
            try
            {
                if (!File.Exists(completedMarkerPath))
                {
                    return false;
                }

                string json = File.ReadAllText(completedMarkerPath, Encoding.UTF8);
                string candidatePath;
                string completedUtc;
                return
                    TryReadJsonString(json, "StatePath", out candidatePath) &&
                    TryReadJsonString(json, "CompletedUtc", out completedUtc) &&
                    TryValidateStatePath(candidatePath, out statePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadGlobalTransaction(out GlobalTransactionInfo transaction)
        {
            transaction = null;
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrWhiteSpace(programData))
                {
                    return false;
                }

                string stateRoot = Path.GetFullPath(Path.Combine(programData, "CodexGamingOptimization"));
                string pointerPath = Path.Combine(stateRoot, "latest-state.txt");
                if (!File.Exists(pointerPath) ||
                    !IsPathFreeOfReparsePoints(pointerPath) ||
                    new FileInfo(pointerPath).Length > 4096)
                {
                    return false;
                }

                string candidatePath = File.ReadAllText(pointerPath, Encoding.UTF8).Trim();
                string validatedPath;
                if (!TryValidateStatePath(candidatePath, out validatedPath))
                {
                    return false;
                }

                var stateFile = new FileInfo(validatedPath);
                if (stateFile.Length <= 0 || stateFile.Length > 8 * 1024 * 1024)
                {
                    return false;
                }

                string json = File.ReadAllText(validatedPath, Encoding.UTF8);
                int version;
                if (!TryReadJsonInteger(json, "Version", out version))
                {
                    version = 1;
                }

                string status;
                if (!TryReadJsonString(json, "Status", out status))
                {
                    status = string.Empty;
                }

                transaction = new GlobalTransactionInfo
                {
                    StatePath = validatedPath,
                    Version = version,
                    Status = status
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryValidateStatePath(string candidatePath, out string validatedPath)
        {
            validatedPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    return false;
                }

                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string backupsRoot = Path.GetFullPath(
                    Path.Combine(programData, "CodexGamingOptimization", "Backups"));
                string fullPath = Path.GetFullPath(candidatePath.Trim());
                string requiredPrefix = backupsRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(fullPath), "state.json", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath) ||
                    !IsPathFreeOfReparsePoints(fullPath))
                {
                    return false;
                }

                validatedPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPathFreeOfReparsePoints(string fullPath)
        {
            try
            {
                string normalized = Path.GetFullPath(fullPath);
                string root = Path.GetPathRoot(normalized);
                if (string.IsNullOrEmpty(root))
                {
                    return false;
                }

                string current = root;
                string remainder = normalized.Substring(root.Length);
                string[] segments = remainder.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int index = 0; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                    if (!Directory.Exists(current) && !File.Exists(current))
                    {
                        return false;
                    }

                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRecoveryStatus(string status)
        {
            return
                string.Equals(status, "Applying", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "ApplyFailed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "RestoreIncomplete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Restoring", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadJsonInteger(string json, string propertyName, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            string pattern =
                "\\\"" + Regex.Escape(propertyName) +
                "\\\"\\s*:\\s*(?<value>-?[0-9]+)";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return
                match.Success &&
                int.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static bool TryReadJsonString(string json, string propertyName, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            string pattern =
                "\\\"" + Regex.Escape(propertyName) +
                "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"";
            Match match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return false;
            }

            value = DecodeJsonString(match.Groups["value"].Value);
            return true;
        }

        private static string DecodeJsonString(string encoded)
        {
            var result = new StringBuilder(encoded.Length);
            for (int index = 0; index < encoded.Length; index++)
            {
                char current = encoded[index];
                if (current != '\\' || index + 1 >= encoded.Length)
                {
                    result.Append(current);
                    continue;
                }

                char escape = encoded[++index];
                switch (escape)
                {
                    case '\"': result.Append('\"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (index + 4 < encoded.Length)
                        {
                            int codePoint;
                            if (int.TryParse(
                                encoded.Substring(index + 1, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out codePoint))
                            {
                                result.Append((char)codePoint);
                                index += 4;
                                break;
                            }
                        }
                        result.Append('u');
                        break;
                    default:
                        result.Append(escape);
                        break;
                }
            }
            return result.ToString();
        }

        private void WritePendingMarker(string statePath, DateTime appliedUtc)
        {
            Directory.CreateDirectory(stateDirectory);
            string json =
                "{\"StatePath\":\"" + EncodeJsonString(statePath) +
                "\",\"AppliedUtc\":\"" + appliedUtc.ToString("o", CultureInfo.InvariantCulture) + "\"}";
            WriteUtf8Atomically(pendingMarkerPath, json);
        }

        private bool TryMarkCompleted(string statePath)
        {
            try
            {
                Directory.CreateDirectory(stateDirectory);
                string json =
                    "{\"StatePath\":\"" + EncodeJsonString(statePath) +
                    "\",\"CompletedUtc\":\"" +
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}";
                WriteUtf8Atomically(
                    completedMarkerPath,
                    json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string EncodeJsonString(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static void WriteUtf8Atomically(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        private static bool HasRestartedSince(DateTime appliedUtc)
        {
            try
            {
                DateTime bootUtc = DateTime.UtcNow.Subtract(
                    TimeSpan.FromMilliseconds(GetTickCount64()));
                return bootUtc > appliedUtc.AddSeconds(1);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private void HideFlow()
        {
            state = FlowState.Hidden;
            Visibility = Visibility.Collapsed;
            preferredFocusButton = null;
        }

        private void CloseApplication()
        {
            allowOwnerClose = true;
            EventHandler handler = RequestApplicationClose;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static T FindVisualChildByName<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                T typed = child as T;
                if (typed != null && string.Equals(typed.Name, name, StringComparison.Ordinal))
                {
                    return typed;
                }

                T nested = FindVisualChildByName<T>(child, name);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }
    }
}
