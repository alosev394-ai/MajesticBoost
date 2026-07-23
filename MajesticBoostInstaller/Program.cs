using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Majestic Boost Setup")]
[assembly: AssemblyDescription("Installer for Majestic Boost")]
[assembly: AssemblyCompany("Silus Suspect")]
[assembly: AssemblyCopyright("© Silus Suspect")]
[assembly: AssemblyProduct("Majestic Boost")]
[assembly: AssemblyVersion("1.6.4.0")]
[assembly: AssemblyFileVersion("1.6.4.0")]

namespace MajesticBoostSetup
{
    internal static class Program
    {
        private const string SetupMutexName = @"Global\CodexGamingOptimization.MajesticBoost.Setup";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool scheduleCleanup = false;

            try
            {
                using (var setupMutex = new System.Threading.Mutex(false, SetupMutexName))
                {
                    bool ownsMutex = false;
                    try
                    {
                        try
                        {
                            ownsMutex = setupMutex.WaitOne(0, false);
                        }
                        catch (System.Threading.AbandonedMutexException)
                        {
                            ownsMutex = true;
                        }

                        if (!ownsMutex)
                        {
                            MessageBox.Show(
                                "Установка или удаление Majestic Boost уже выполняется. Дождитесь завершения открытого установщика.",
                                "Majestic Boost Setup",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            Environment.ExitCode = 2;
                            return;
                        }

                        scheduleCleanup = true;
                        Run(args);
                    }
                    finally
                    {
                        if (ownsMutex)
                        {
                            setupMutex.ReleaseMutex();
                        }
                    }
                }
            }
            finally
            {
                if (scheduleCleanup)
                {
                    InstallerEngine.ScheduleUpdateSourceCleanupIfNeeded();
                }
            }
        }

        private static void Run(string[] args)
        {
            bool uninstall = string.Equals(
                Path.GetFullPath(Application.ExecutablePath),
                Path.GetFullPath(InstallerEngine.UninstallerExe),
                StringComparison.OrdinalIgnoreCase);
            bool quiet = false;
            bool silentInstall = false;
            bool launchAfterInstall = false;
            bool updateUi = false;
            bool demoUpdateUi = false;
            foreach (string argument in args)
            {
                if (string.Equals(argument, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    uninstall = true;
                }
                else if (string.Equals(argument, "/quiet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "/silent", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                    silentInstall = true;
                }
                else if (string.Equals(argument, "/launch", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-launch", StringComparison.OrdinalIgnoreCase))
                {
                    launchAfterInstall = true;
                }
                else if (string.Equals(argument, "/updateui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-updateui", StringComparison.OrdinalIgnoreCase))
                {
                    updateUi = true;
                }
                else if (string.Equals(argument, "/demo-updateui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-demo-updateui", StringComparison.OrdinalIgnoreCase))
                {
                    updateUi = true;
                    demoUpdateUi = true;
                }
            }

            if (uninstall)
            {
                InstallerEngine.Uninstall(quiet);
                return;
            }

            if (silentInstall && !updateUi)
            {
                try
                {
                    InstallerEngine.Install(InstallerEngine.GetDesktopShortcutPreference());
                    if (launchAfterInstall)
                    {
                        InstallerEngine.LaunchInstalledApplication();
                    }
                    Environment.ExitCode = 0;
                }
                catch
                {
                    Environment.ExitCode = 1;
                }
                return;
            }

            Application.ApplicationExit += delegate { MajesticFontProvider.Dispose(); };
            Application.Run(updateUi ? (Form)new UpdateProgressForm(demoUpdateUi) : new InstallerForm());
        }
    }

    internal static class InstallerEngine
    {
        public const string ProductName = "Majestic Boost";
        public const string ProductVersion = "1.6.4";
        public static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProductName);
        public static readonly string InstalledExe = Path.Combine(InstallDirectory, "MajesticBoost.exe");
        public static readonly string InstalledGameBoostScript = Path.Combine(InstallDirectory, "Game-Boost.ps1");
        public static readonly string InstalledMaxFpsApplyScript = Path.Combine(InstallDirectory, "MaxFPS-Apply.ps1");
        public static readonly string InstalledMaxFpsRestoreScript = Path.Combine(InstallDirectory, "MaxFPS-Restore.ps1");
        public static readonly string PresentMonDirectory = Path.Combine(InstallDirectory, "Tools", "PresentMon");
        public static readonly string InstalledPresentMon = Path.Combine(PresentMonDirectory, "PresentMon.exe");
        public static readonly string InstalledPresentMonLicense = Path.Combine(PresentMonDirectory, "LICENSE.txt");
        public static readonly string InstalledPresentMonThirdParty = Path.Combine(PresentMonDirectory, "THIRD_PARTY.txt");
        public static readonly string UninstallerExe = Path.Combine(InstallDirectory, "Uninstall.exe");

        private const string UninstallRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MajesticBoost";
        private const string AppPathsRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\MajesticBoost.exe";
        private const AccessControlSections CaptureSecuritySections =
            AccessControlSections.Access |
            AccessControlSections.Owner |
            AccessControlSections.Group;

        public static void Install(bool createDesktopShortcut, Action<int, string> progress = null)
        {
            ReportProgress(progress, 0, "Подготовка обновления");
            EnsureInstallIsNotDowngrade();
            Directory.CreateDirectory(InstallDirectory);
            ReportProgress(progress, 5, "Подготовка папки установки");

            InstallPayloadsAtomically(progress, delegate
            {
            PostInstallRegistrationSnapshot registrationSnapshot =
                CapturePostInstallRegistration();
            try
            {
            ReportProgress(progress, 76, "Обновление компонентов удаления");

            string startMenuDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                ProductName);
            Directory.CreateDirectory(startMenuDirectory);
            CreateShortcut(
                Path.Combine(startMenuDirectory, ProductName + ".lnk"),
                InstalledExe,
                InstallDirectory,
                "Animated Majestic MAX FPS launcher.");
            ReportProgress(progress, 82, "Обновление ярлыков");

            if (createDesktopShortcut)
            {
                CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"),
                    InstalledExe,
                    InstallDirectory,
                    "Animated Majestic MAX FPS launcher.");
            }
            else
            {
                DeleteIfExists(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    ProductName + ".lnk"));
            }
            ReportProgress(progress, 87, "Сохранение параметров установки");

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey uninstall = baseKey.CreateSubKey(UninstallRegistryPath))
            {
                uninstall.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                uninstall.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
                uninstall.SetValue("Publisher", "Silus Suspect", RegistryValueKind.String);
                uninstall.SetValue("InstallLocation", InstallDirectory, RegistryValueKind.String);
                uninstall.SetValue("DisplayIcon", InstalledExe + ",0", RegistryValueKind.String);
                uninstall.SetValue("UninstallString", Quote(UninstallerExe) + " /uninstall", RegistryValueKind.String);
                uninstall.SetValue("QuietUninstallString", Quote(UninstallerExe) + " /uninstall /quiet", RegistryValueKind.String);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                uninstall.SetValue("EstimatedSize", CalculateEstimatedSizeKb(), RegistryValueKind.DWord);
                uninstall.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
            ReportProgress(progress, 94, "Регистрация новой версии");

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey appPath = baseKey.CreateSubKey(AppPathsRegistryPath))
            {
                appPath.SetValue(string.Empty, InstalledExe, RegistryValueKind.String);
                appPath.SetValue("Path", InstallDirectory, RegistryValueKind.String);
            }
            ReportProgress(progress, 100, "Обновление установлено");
            }
            catch (Exception registrationException)
            {
                try
                {
                    RestorePostInstallRegistration(registrationSnapshot);
                }
                catch (Exception compensationException)
                {
                    throw new AggregateException(
                        "Installation registration failed and its previous state could not be restored completely.",
                        registrationException,
                        compensationException);
                }
                throw;
            }
            });
        }

        public static bool GetDesktopShortcutPreference()
        {
            if (!File.Exists(InstalledExe))
            {
                return true;
            }

            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ProductName + ".lnk");
            return File.Exists(shortcutPath);
        }

        public static void ScheduleUpdateSourceCleanupIfNeeded()
        {
            try
            {
                string executablePath = Path.GetFullPath(Application.ExecutablePath);
                string directoryPath = Path.GetFullPath(Path.GetDirectoryName(executablePath));
                string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                string parentPath = Path.GetFullPath(Path.GetDirectoryName(directoryPath))
                    .TrimEnd(Path.DirectorySeparatorChar);
                string directoryName = Path.GetFileName(directoryPath);
                string executableName = Path.GetFileName(executablePath);
                if (!string.Equals(parentPath, tempRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(directoryName, @"^MajesticBoost\.Update\.[0-9a-f]{32}$", RegexOptions.IgnoreCase) ||
                    !Regex.IsMatch(executableName, @"^MajesticBoost-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe$", RegexOptions.IgnoreCase))
                {
                    return;
                }

                DirectoryInfo directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                int processId = Process.GetCurrentProcess().Id;
                string encodedExecutable = Convert.ToBase64String(Encoding.UTF8.GetBytes(executablePath));
                string encodedDirectory = Convert.ToBase64String(Encoding.UTF8.GetBytes(directoryPath));
                string cleanupCommand =
                    "$ErrorActionPreference='SilentlyContinue';" +
                    "Wait-Process -Id " + processId.ToString(CultureInfo.InvariantCulture) +
                    " -ErrorAction SilentlyContinue;" +
                    "$e=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedExecutable + "'));" +
                    "$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedDirectory + "'));" +
                    "$t=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar);" +
                    "$p=[IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($d)).TrimEnd([IO.Path]::DirectorySeparatorChar);" +
                    "if($p -ieq $t -and [IO.Path]::GetFileName($d) -match '^MajesticBoost\\.Update\\.[0-9a-f]{32}$'){" +
                    "$i=Get-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue;" +
                    "if($i -and -not ($i.Attributes -band [IO.FileAttributes]::ReparsePoint)){" +
                    "[IO.File]::Delete($e);" +
                    "if([IO.Directory]::Exists($d) -and [IO.Directory]::GetFileSystemEntries($d).Length -eq 0){[IO.Directory]::Delete($d,$false)}" +
                    "}}";
                string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(cleanupCommand));
                var cleanupInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.SystemDirectory,
                        @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encodedCommand,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process cleanupProcess = Process.Start(cleanupInfo);
                if (cleanupProcess != null)
                {
                    cleanupProcess.Dispose();
                }
            }
            catch
            {
                // A stale temporary setup is harmless and can be removed later.
            }
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet)
            {
                DialogResult result = MessageBox.Show(
                    "Удалить Majestic Boost и все установленные файлы?",
                    "Удаление Majestic Boost",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                StopInstalledApplication();

                string desktopShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    ProductName + ".lnk");
                DeleteIfExists(desktopShortcut);

                string startMenuDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    ProductName);
                if (Directory.Exists(startMenuDirectory))
                {
                    Directory.Delete(startMenuDirectory, true);
                }

                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    baseKey.DeleteSubKeyTree(UninstallRegistryPath, false);
                    baseKey.DeleteSubKeyTree(AppPathsRegistryPath, false);
                }

                string localData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost");
                if (Directory.Exists(localData))
                {
                    Directory.Delete(localData, true);
                }
                TryPruneProtectedCaptureFiles(true);

                if (!quiet)
                {
                    MessageBox.Show(
                        "Majestic Boost удалён.",
                        "Удаление завершено",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                int currentProcessId = Process.GetCurrentProcess().Id;
                string escapedInstallDirectory = InstallDirectory.Replace("'", "''");
                string cleanupCommand =
                    "$ErrorActionPreference='SilentlyContinue';" +
                    "Wait-Process -Id " + currentProcessId.ToString(CultureInfo.InvariantCulture) +
                    " -ErrorAction SilentlyContinue;" +
                    "Remove-Item -LiteralPath '" + escapedInstallDirectory +
                    "' -Recurse -Force -ErrorAction SilentlyContinue";
                string encodedCleanupCommand = Convert.ToBase64String(
                    Encoding.Unicode.GetBytes(cleanupCommand));
                var cleanupInfo = new ProcessStartInfo();
                cleanupInfo.FileName = Path.Combine(
                    Environment.SystemDirectory,
                    @"WindowsPowerShell\v1.0\powershell.exe");
                cleanupInfo.Arguments =
                    "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
                    encodedCleanupCommand;
                cleanupInfo.UseShellExecute = false;
                cleanupInfo.CreateNoWindow = true;
                cleanupInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(cleanupInfo);
            }
            catch (Exception exception)
            {
                if (!quiet)
                {
                    MessageBox.Show(
                        "Не удалось полностью удалить программу:\r\n" + exception.Message,
                        "Ошибка удаления",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                Environment.ExitCode = 1;
            }
        }

        private sealed class ShortcutSnapshot
        {
            public string Path;
            public bool Existed;
            public byte[] Contents;
            public FileAttributes Attributes;
            public DateTime LastWriteTimeUtc;
        }

        private sealed class RegistryKeySnapshot
        {
            public string Name;
            public bool Existed;
            public readonly List<RegistryValueSnapshot> Values =
                new List<RegistryValueSnapshot>();
            public readonly List<RegistryKeySnapshot> Children =
                new List<RegistryKeySnapshot>();
        }

        private sealed class RegistryValueSnapshot
        {
            public string Name;
            public object Value;
            public RegistryValueKind Kind;
        }

        private sealed class PostInstallRegistrationSnapshot
        {
            public ShortcutSnapshot StartMenuShortcut;
            public ShortcutSnapshot DesktopShortcut;
            public bool StartMenuDirectoryExisted;
            public RegistryKeySnapshot UninstallKey;
            public RegistryKeySnapshot AppPathsKey;
        }

        private static PostInstallRegistrationSnapshot CapturePostInstallRegistration()
        {
            string startMenuDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                ProductName);
            string desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ProductName + ".lnk");
            var snapshot = new PostInstallRegistrationSnapshot
            {
                StartMenuShortcut = CaptureShortcut(Path.Combine(
                    startMenuDirectory,
                    ProductName + ".lnk")),
                DesktopShortcut = CaptureShortcut(desktopShortcut),
                StartMenuDirectoryExisted = Directory.Exists(startMenuDirectory)
            };

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64))
            {
                snapshot.UninstallKey = CaptureRegistryKey(baseKey, UninstallRegistryPath);
                snapshot.AppPathsKey = CaptureRegistryKey(baseKey, AppPathsRegistryPath);
            }
            return snapshot;
        }

        private static ShortcutSnapshot CaptureShortcut(string path)
        {
            var snapshot = new ShortcutSnapshot
            {
                Path = path,
                Existed = File.Exists(path)
            };
            if (snapshot.Existed)
            {
                snapshot.Contents = File.ReadAllBytes(path);
                snapshot.Attributes = File.GetAttributes(path);
                snapshot.LastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            }
            return snapshot;
        }

        private static RegistryKeySnapshot CaptureRegistryKey(RegistryKey parent, string path)
        {
            RegistryKey key = parent.OpenSubKey(path, false);
            if (key == null)
            {
                return new RegistryKeySnapshot { Name = path, Existed = false };
            }
            using (key)
            {
                RegistryKeySnapshot snapshot = CaptureRegistryTree(key, path);
                snapshot.Existed = true;
                return snapshot;
            }
        }

        private static RegistryKeySnapshot CaptureRegistryTree(RegistryKey key, string name)
        {
            var snapshot = new RegistryKeySnapshot { Name = name, Existed = true };
            foreach (string valueName in key.GetValueNames())
            {
                snapshot.Values.Add(new RegistryValueSnapshot
                {
                    Name = valueName,
                    Value = key.GetValue(
                        valueName,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames),
                    Kind = key.GetValueKind(valueName)
                });
            }
            foreach (string childName in key.GetSubKeyNames())
            {
                using (RegistryKey child = key.OpenSubKey(childName, false))
                {
                    if (child == null)
                    {
                        throw new IOException(
                            "An installation registry key changed while it was being backed up.");
                    }
                    snapshot.Children.Add(CaptureRegistryTree(child, childName));
                }
            }
            return snapshot;
        }

        private static void RestorePostInstallRegistration(
            PostInstallRegistrationSnapshot snapshot)
        {
            var failures = new List<Exception>();
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64))
            {
                TryCompensation(
                    delegate { RestoreRegistryKey(baseKey, snapshot.AppPathsKey); },
                    failures);
                TryCompensation(
                    delegate { RestoreRegistryKey(baseKey, snapshot.UninstallKey); },
                    failures);
            }
            TryCompensation(
                delegate { RestoreShortcut(snapshot.DesktopShortcut); },
                failures);
            TryCompensation(
                delegate { RestoreShortcut(snapshot.StartMenuShortcut); },
                failures);

            if (!snapshot.StartMenuDirectoryExisted)
            {
                TryDeleteEmptyDirectory(Path.GetDirectoryName(
                    snapshot.StartMenuShortcut.Path));
            }
            if (failures.Count != 0)
            {
                throw new AggregateException(
                    "One or more installation registration items could not be restored.",
                    failures);
            }
        }

        private static void TryCompensation(Action action, List<Exception> failures)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        private static void RestoreShortcut(ShortcutSnapshot snapshot)
        {
            if (!snapshot.Existed)
            {
                DeleteIfExists(snapshot.Path);
                return;
            }

            string directory = Path.GetDirectoryName(snapshot.Path);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (File.Exists(snapshot.Path))
            {
                File.SetAttributes(snapshot.Path, FileAttributes.Normal);
            }
            File.WriteAllBytes(snapshot.Path, snapshot.Contents);
            File.SetLastWriteTimeUtc(snapshot.Path, snapshot.LastWriteTimeUtc);
            File.SetAttributes(snapshot.Path, snapshot.Attributes);
        }

        private static void RestoreRegistryKey(
            RegistryKey baseKey,
            RegistryKeySnapshot snapshot)
        {
            baseKey.DeleteSubKeyTree(snapshot.Name, false);
            if (!snapshot.Existed)
            {
                return;
            }

            using (RegistryKey key = baseKey.CreateSubKey(snapshot.Name))
            {
                if (key == null)
                {
                    throw new IOException(
                        "The previous installation registry key could not be recreated.");
                }
                RestoreRegistryTree(key, snapshot);
            }
        }

        private static void RestoreRegistryTree(
            RegistryKey key,
            RegistryKeySnapshot snapshot)
        {
            foreach (RegistryValueSnapshot value in snapshot.Values)
            {
                key.SetValue(value.Name, value.Value, value.Kind);
            }
            foreach (RegistryKeySnapshot childSnapshot in snapshot.Children)
            {
                using (RegistryKey child = key.CreateSubKey(childSnapshot.Name))
                {
                    if (child == null)
                    {
                        throw new IOException(
                            "A previous installation registry subkey could not be recreated.");
                    }
                    RestoreRegistryTree(child, childSnapshot);
                }
            }
        }

        private sealed class PayloadTransactionItem
        {
            public string ResourceName;
            public string StagePath;
            public string DestinationPath;
            public string BackupPath;
            public string ProgressText;
            public bool CopyInstaller;
            public bool Executable;
            public bool PresentMon;
            public bool DestinationExisted;
            public bool Committed;
            public bool Restored = true;
        }

        private sealed class CaptureDirectoryTransaction
        {
            public string CommonDataDirectory;
            public string ProductDirectory;
            public string CaptureDirectory;
            public bool ProductExisted;
            public bool CaptureExisted;
            public string ProductSecuritySddl;
            public string CaptureSecuritySddl;
            public bool ProductTouched;
            public bool CaptureTouched;
            public bool Restored = true;
        }

        private static void InstallPayloadsAtomically(Action<int, string> progress, Action registerInstallation)
        {
            string token = Guid.NewGuid().ToString("N");
            var items = new List<PayloadTransactionItem>
            {
                CreatePayloadItem(token, "Game-Boost", "MajesticBoost.GameBoost.ps1", InstalledGameBoostScript, "игровых настроек", false, false),
                CreatePayloadItem(token, "MaxFPS-Apply", "MajesticBoost.MaxFPSApply.ps1", InstalledMaxFpsApplyScript, "профиля производительности", false, false),
                CreatePayloadItem(token, "MaxFPS-Restore", "MajesticBoost.MaxFPSRestore.ps1", InstalledMaxFpsRestoreScript, "компонентов восстановления", false, false),
                CreatePayloadItem(token, "PresentMon-License", "MajesticBoost.PresentMon.License.txt", InstalledPresentMonLicense, "лицензии измерителя FPS", false, false),
                CreatePayloadItem(token, "PresentMon-ThirdParty", "MajesticBoost.PresentMon.ThirdParty.txt", InstalledPresentMonThirdParty, "уведомлений сторонних компонентов", false, false),
                CreatePayloadItem(token, "PresentMon", "MajesticBoost.PresentMon.exe", InstalledPresentMon, "измерителя FPS", false, true),
                CreatePayloadItem(token, "Uninstall", null, UninstallerExe, "компонентов удаления", true, false),
                CreatePayloadItem(token, "MajesticBoost", "MajesticBoost.Payload.exe", InstalledExe, "файлов программы", true, false)
            };
            items[6].CopyInstaller = true;
            bool installationSucceeded = false;
            CaptureDirectoryTransaction captureDirectories = null;

            try
            {
                for (int index = 0; index < items.Count; index++)
                {
                    PayloadTransactionItem item = items[index];
                    ReportProgress(
                        progress,
                        10 + index * 2,
                        "Распаковка " + item.ProgressText);
                    if (item.CopyInstaller)
                    {
                        File.Copy(Application.ExecutablePath, item.StagePath, false);
                    }
                    else
                    {
                        ExtractResource(item.ResourceName, item.StagePath);
                    }
                }

                // No installed file is touched until every embedded payload exists
                // and passes its own integrity validation.
                for (int index = 0; index < items.Count; index++)
                {
                    PayloadTransactionItem item = items[index];
                    ReportProgress(
                        progress,
                        28 + index * 2,
                        "Проверка " + item.ProgressText);
                    ValidateStagedPayload(item.StagePath, item.Executable);
                    if (item.PresentMon)
                    {
                        ValidatePresentMonPayload(item.StagePath);
                    }
                }

                StopInstalledApplication();
                ReportProgress(progress, 45, "Остановка запущенной версии");
                captureDirectories = PrepareCaptureDirectoryTransaction();
                ApplyCaptureDirectoryTransaction(captureDirectories);
                ReportProgress(progress, 47, "Защита папки измерений");

                // Dependencies are published first; the main application remains
                // the final commit marker for the transaction.
                for (int index = 0; index < items.Count; index++)
                {
                    PayloadTransactionItem item = items[index];
                    string directory = Path.GetDirectoryName(item.DestinationPath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    ReportProgress(
                        progress,
                        48 + index * 3,
                        "Установка " + item.ProgressText);
                    CommitStagedFile(
                        item.StagePath,
                        item.DestinationPath,
                        item.BackupPath,
                        item.DestinationExisted);
                    item.Committed = true;
                }
                if (registerInstallation != null)
                {
                    registerInstallation();
                }
                installationSucceeded = true;
                ReportProgress(progress, 72, "Файлы программы обновлены");
            }
            catch
            {
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    PayloadTransactionItem item = items[index];
                    if (item.Committed)
                    {
                        item.Restored = RestoreCommittedFile(
                            item.DestinationPath,
                            item.BackupPath,
                            item.DestinationExisted);
                    }
                }
                if (captureDirectories != null)
                {
                    captureDirectories.Restored =
                        RollbackCaptureDirectoryTransaction(captureDirectories);
                }
                throw;
            }
            finally
            {
                foreach (PayloadTransactionItem item in items)
                {
                    TryDeleteIfExists(item.StagePath);
                    if (installationSucceeded || !item.Committed || item.Restored)
                    {
                        TryDeleteIfExists(item.BackupPath);
                    }
                }
                if (!installationSucceeded)
                {
                    TryDeleteEmptyDirectory(PresentMonDirectory);
                    TryDeleteEmptyDirectory(Path.GetDirectoryName(PresentMonDirectory));
                }
            }
        }

        private static CaptureDirectoryTransaction PrepareCaptureDirectoryTransaction()
        {
            string commonDataDirectory;
            string productDirectory;
            string captureDirectory;
            ResolveProtectedCapturePaths(
                out commonDataDirectory,
                out productDirectory,
                out captureDirectory);

            ValidateCaptureDirectory(commonDataDirectory, true, "ProgramData");
            ValidateCaptureDirectory(productDirectory, false, "MajesticBoost");
            ValidateCaptureDirectory(captureDirectory, false, "Captures");

            bool productExisted = Directory.Exists(productDirectory);
            bool captureExisted = Directory.Exists(captureDirectory);
            if (!productExisted && captureExisted)
            {
                throw new IOException(
                    "The capture directory exists without its protected product parent.");
            }

            return new CaptureDirectoryTransaction
            {
                CommonDataDirectory = commonDataDirectory,
                ProductDirectory = productDirectory,
                CaptureDirectory = captureDirectory,
                ProductExisted = productExisted,
                CaptureExisted = captureExisted,
                ProductSecuritySddl = productExisted
                    ? CaptureDirectorySecuritySddl(productDirectory)
                    : null,
                CaptureSecuritySddl = captureExisted
                    ? CaptureDirectorySecuritySddl(captureDirectory)
                    : null
            };
        }

        private static void ApplyCaptureDirectoryTransaction(
            CaptureDirectoryTransaction transaction)
        {
            if (transaction == null)
            {
                throw new ArgumentNullException("transaction");
            }

            EnsureCaptureDirectoryState(
                transaction.ProductDirectory,
                transaction.ProductExisted,
                "MajesticBoost");
            transaction.ProductTouched = true;
            ApplySecureCaptureDirectory(
                transaction.ProductDirectory,
                transaction.ProductExisted,
                false);

            // The protected parent is tightened before the child is touched, so
            // an unelevated user cannot swap the Captures directory underneath
            // the elevated installer.
            EnsureCaptureDirectoryState(
                transaction.CaptureDirectory,
                transaction.CaptureExisted,
                "Captures");
            transaction.CaptureTouched = true;
            ApplySecureCaptureDirectory(
                transaction.CaptureDirectory,
                transaction.CaptureExisted,
                true);
        }

        private static DirectorySecurity CreateCaptureDirectorySecurity(
            bool allowInheritedFileCleanup)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);

            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null);
            var authenticatedUsers = new SecurityIdentifier(
                WellKnownSidType.AuthenticatedUserSid,
                null);
            security.SetOwner(administrators);
            security.SetGroup(administrators);

            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit |
                InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                system,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                administrators,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            if (allowInheritedFileCleanup)
            {
                // Standard users still cannot create or modify entries in the
                // Captures directory. This object-only inherited right lets the
                // originating user delete the admin-created CSV after copying it
                // down from an over-the-shoulder UAC capture.
                security.AddAccessRule(new FileSystemAccessRule(
                    authenticatedUsers,
                    FileSystemRights.Delete,
                    InheritanceFlags.ObjectInherit,
                    PropagationFlags.InheritOnly,
                    AccessControlType.Allow));
            }
            return security;
        }

        private static void ApplySecureCaptureDirectory(
            string path,
            bool existed,
            bool allowInheritedFileCleanup)
        {
            DirectorySecurity security = CreateCaptureDirectorySecurity(
                allowInheritedFileCleanup);
            if (!existed)
            {
                Directory.CreateDirectory(path, security);
            }
            ValidateCaptureDirectory(path, true, Path.GetFileName(path));
            Directory.SetAccessControl(path, security);
            ValidateCaptureDirectory(path, true, Path.GetFileName(path));
        }

        private static bool RollbackCaptureDirectoryTransaction(
            CaptureDirectoryTransaction transaction)
        {
            bool captureRestored = true;
            if (transaction.CaptureTouched && !transaction.CaptureExisted)
            {
                captureRestored =
                    TryDeleteCreatedCaptureDirectory(transaction.CaptureDirectory);
            }

            bool productRestored = true;
            if (transaction.ProductTouched)
            {
                productRestored = transaction.ProductExisted
                    ? TryRestoreCaptureDirectorySecurity(
                        transaction.ProductDirectory,
                        transaction.ProductSecuritySddl)
                    : TryDeleteCreatedCaptureDirectory(transaction.ProductDirectory);
            }

            // Restore the child only after the original parent ACL is back.
            // If restoring the parent failed, keeping the child protected is
            // safer than restoring a possibly user-writable previous child ACL.
            if (transaction.CaptureTouched && transaction.CaptureExisted)
            {
                captureRestored = productRestored &&
                    TryRestoreCaptureDirectorySecurity(
                        transaction.CaptureDirectory,
                        transaction.CaptureSecuritySddl);
            }
            return productRestored && captureRestored;
        }

        private static bool TryRestoreCaptureDirectorySecurity(
            string path,
            string securitySddl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(securitySddl))
                {
                    return false;
                }
                ValidateCaptureDirectory(path, true, Path.GetFileName(path));
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm(
                    securitySddl,
                    CaptureSecuritySections);
                Directory.SetAccessControl(path, security);
                ValidateCaptureDirectory(path, true, Path.GetFileName(path));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteCreatedCaptureDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return !File.Exists(path);
                }
                ValidateCaptureDirectory(path, true, Path.GetFileName(path));
                if (Directory.GetFileSystemEntries(path).Length != 0)
                {
                    return false;
                }
                Directory.Delete(path, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string CaptureDirectorySecuritySddl(string path)
        {
            DirectorySecurity security = Directory.GetAccessControl(
                path,
                CaptureSecuritySections);
            return security.GetSecurityDescriptorSddlForm(CaptureSecuritySections);
        }

        private static void EnsureCaptureDirectoryState(
            string path,
            bool expectedToExist,
            string name)
        {
            bool exists = Directory.Exists(path);
            if (exists != expectedToExist ||
                (!exists && File.Exists(path)))
            {
                throw new IOException(
                    "The protected " + name +
                    " directory changed during installation.");
            }
            ValidateCaptureDirectory(path, expectedToExist, name);
        }

        private static void ResolveProtectedCapturePaths(
            out string commonDataDirectory,
            out string productDirectory,
            out string captureDirectory)
        {
            string commonData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData))
            {
                throw new DirectoryNotFoundException(
                    "The system ProgramData directory is unavailable.");
            }

            commonDataDirectory = Path.GetFullPath(commonData);
            productDirectory = Path.GetFullPath(Path.Combine(
                commonDataDirectory,
                "MajesticBoost"));
            captureDirectory = Path.GetFullPath(Path.Combine(
                productDirectory,
                "Captures"));
            if (!IsStrictChildPath(commonDataDirectory, productDirectory) ||
                !IsStrictChildPath(productDirectory, captureDirectory))
            {
                throw new IOException(
                    "The protected capture directory resolved outside ProgramData.");
            }
        }

        private static bool IsStrictChildPath(string parentPath, string childPath)
        {
            string prefix = parentPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return childPath.Length > prefix.Length &&
                   childPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateCaptureDirectory(
            string path,
            bool required,
            string name)
        {
            if (!Directory.Exists(path))
            {
                if (File.Exists(path))
                {
                    throw new IOException(
                        "A file occupies the protected " + name + " directory path.");
                }
                if (required)
                {
                    throw new DirectoryNotFoundException(
                        "The protected " + name + " directory is unavailable.");
                }
                return;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "The protected " + name + " directory cannot be a reparse point.");
            }
        }

        private static void TryPruneProtectedCaptureFiles(bool removeDirectories)
        {
            try
            {
                string commonDataDirectory;
                string productDirectory;
                string captureDirectory;
                ResolveProtectedCapturePaths(
                    out commonDataDirectory,
                    out productDirectory,
                    out captureDirectory);
                ValidateCaptureDirectory(commonDataDirectory, true, "ProgramData");
                ValidateCaptureDirectory(productDirectory, false, "MajesticBoost");
                ValidateCaptureDirectory(captureDirectory, false, "Captures");
                if (!Directory.Exists(captureDirectory))
                {
                    return;
                }

                string capturePrefix = captureDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string candidate in Directory.GetFiles(
                    captureDirectory,
                    "MajesticBoost-PresentMon-*.csv",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(candidate);
                        string fileName = Path.GetFileName(fullPath);
                        if (!fullPath.StartsWith(
                                capturePrefix,
                                StringComparison.OrdinalIgnoreCase) ||
                            !Regex.IsMatch(
                                fileName,
                                @"^MajesticBoost-PresentMon-[0-9a-f]{32}\.csv$",
                                RegexOptions.IgnoreCase |
                                RegexOptions.CultureInvariant) ||
                            (File.GetAttributes(fullPath) &
                             FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                        File.Delete(fullPath);
                    }
                    catch
                    {
                        // Continue pruning other exact capture artifacts.
                    }
                }

                if (removeDirectories)
                {
                    TryDeleteCreatedCaptureDirectory(captureDirectory);
                    TryDeleteCreatedCaptureDirectory(productDirectory);
                }
            }
            catch
            {
                // Capture staging is temporary; an unsafe or busy path is left
                // untouched rather than making uninstall destructive.
            }
        }

        private static PayloadTransactionItem CreatePayloadItem(
            string token,
            string stageName,
            string resourceName,
            string destination,
            string progressText,
            bool executable,
            bool presentMon)
        {
            return new PayloadTransactionItem
            {
                ResourceName = resourceName,
                StagePath = Path.Combine(
                    InstallDirectory,
                    "." + stageName + "-" + token + ".stage"),
                DestinationPath = destination,
                BackupPath = Path.Combine(
                    InstallDirectory,
                    "." + stageName + "-" + token + ".backup"),
                ProgressText = progressText,
                Executable = executable,
                PresentMon = presentMon,
                DestinationExisted = File.Exists(destination)
            };
        }

        private static void EnsureInstallIsNotDowngrade()
        {
            if (!File.Exists(InstalledExe))
            {
                return;
            }

            FileVersionInfo installedInfo = FileVersionInfo.GetVersionInfo(InstalledExe);
            if (IsDowngrade(installedInfo.FileVersion, ProductVersion + ".0"))
            {
                Version installedVersion = Version.Parse(installedInfo.FileVersion.Trim());
                throw new InvalidOperationException(
                    "На компьютере уже установлена более новая версия Majestic Boost (" +
                    installedVersion.ToString(3) + "). Установка более старой версии отменена.");
            }
        }

        private static bool IsDowngrade(string installedVersionText, string setupVersionText)
        {
            Version installedVersion;
            Version setupVersion;
            return Version.TryParse((installedVersionText ?? string.Empty).Trim(), out installedVersion) &&
                Version.TryParse((setupVersionText ?? string.Empty).Trim(), out setupVersion) &&
                installedVersion > setupVersion;
        }

        private static void ValidateStagedPayload(string path, bool executable)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                throw new InvalidDataException("Встроенные файлы установщика повреждены.");
            }

            if (!executable)
            {
                return;
            }

            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 2 || input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                {
                    throw new InvalidDataException("Встроенный файл программы повреждён.");
                }
            }

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(path);
            Version payloadVersion;
            Version expectedVersion;
            if (!string.Equals(versionInfo.ProductName, ProductName, StringComparison.Ordinal) ||
                !Version.TryParse((versionInfo.FileVersion ?? string.Empty).Trim(), out payloadVersion) ||
                !Version.TryParse(ProductVersion + ".0", out expectedVersion) ||
                payloadVersion != expectedVersion)
            {
                throw new InvalidDataException("Встроенный исполняемый файл имеет неверную версию или имя продукта.");
            }
        }

        private static void ValidatePresentMonPayload(string path)
        {
            const long expectedLength = 956768;
            const string expectedSha256 =
                "9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191";
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expectedLength)
            {
                throw new InvalidDataException("Встроенный измеритель FPS имеет неверный размер.");
            }

            string actualHash;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                {
                    throw new InvalidDataException("Встроенный измеритель FPS повреждён.");
                }
                input.Position = 0;
                using (SHA256 sha256 = SHA256.Create())
                {
                    actualHash = BitConverter.ToString(sha256.ComputeHash(input))
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
            if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Не совпадает контрольная сумма измерителя FPS.");
            }
        }

        private static void CommitStagedFile(string stage, string destination, string backup, bool destinationExists)
        {
            if (destinationExists)
            {
                File.Replace(stage, destination, backup, true);
            }
            else
            {
                File.Move(stage, destination);
            }
        }

        private static void ReplaceFileWithoutRetainedBackup(string source, string destination)
        {
            string discardBackup = destination + ".replace-backup-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Replace(source, destination, discardBackup, true);
            }
            finally
            {
                try
                {
                    DeleteIfExists(discardBackup);
                }
                catch
                {
                    // A disposable copy of the failed destination must not make
                    // restoring the known-good installation report failure.
                }
            }
        }

        private static bool RestoreCommittedFile(string destination, string backup, bool destinationExisted)
        {
            try
            {
                if (destinationExisted && File.Exists(backup))
                {
                    if (File.Exists(destination))
                    {
                        ReplaceFileWithoutRetainedBackup(backup, destination);
                    }
                    else
                    {
                        File.Move(backup, destination);
                    }
                }
                else if (!destinationExisted)
                {
                    DeleteIfExists(destination);
                }
                else
                {
                    return false;
                }
                return true;
            }
            catch
            {
                // Keep the original installation error; backup remains for diagnostics.
                return false;
            }
        }

        private static void ExtractResource(string resourceName, string destination)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(resourceName))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("В установщике отсутствует ресурс: " + resourceName);
                }
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static void StopInstalledApplication()
        {
            foreach (Process process in Process.GetProcessesByName("MajesticBoost"))
            {
                try
                {
                    string runningPath = process.MainModule.FileName;
                    if (string.Equals(runningPath, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!process.CloseMainWindow() || !process.WaitForExit(1200))
                        {
                            process.Kill();
                        }
                        if (!process.WaitForExit(3000))
                        {
                            throw new InvalidOperationException("Закройте запущенный Majestic Boost и повторите установку.");
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch
                {
                    // An unrelated inaccessible process with the same name is ignored.
                }
                finally { process.Dispose(); }
            }
        }

        public static void LaunchInstalledApplication()
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = explorer;
            startInfo.Arguments = Quote(InstalledExe);
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("Windows Script Host недоступен.");
            }

            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }
                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }

        private static int CalculateEstimatedSizeKb()
        {
            long total = 0;
            foreach (string file in new[]
            {
                InstalledExe,
                InstalledGameBoostScript,
                InstalledMaxFpsApplyScript,
                InstalledMaxFpsRestoreScript,
                InstalledPresentMon,
                InstalledPresentMonLicense,
                InstalledPresentMonThirdParty,
                UninstallerExe
            })
            {
                if (File.Exists(file))
                {
                    total += new FileInfo(file).Length;
                }
            }
            return (int)Math.Max(1, total / 1024);
        }

        private static void ReportProgress(Action<int, string> progress, int percent, string stage)
        {
            if (progress == null)
            {
                return;
            }

            try
            {
                progress(Math.Max(0, Math.Min(100, percent)), stage ?? string.Empty);
            }
            catch
            {
                // A closed or unavailable progress surface must not corrupt installation.
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void TryDeleteIfExists(string path)
        {
            try
            {
                DeleteIfExists(path);
            }
            catch
            {
                // Cleanup must not turn a successful commit or the original
                // installation error into a different failure.
            }
        }

        private static void TryDeleteEmptyDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) &&
                    Directory.Exists(path) &&
                    Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path, false);
                }
            }
            catch
            {
                // A harmless empty directory can be removed by a later install.
            }
        }
    }

    internal static class MajesticFontProvider
    {
        private static readonly PrivateFontCollection Fonts = new PrivateFontCollection();
        private static bool loaded;

        public static Font Create(float size, FontStyle style)
        {
            EnsureLoaded();
            FontFamily selected = null;
            string preferred = "Proxima Nova";
            foreach (FontFamily family in Fonts.Families)
            {
                if (string.Equals(family.Name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    selected = family;
                    break;
                }
            }

            if (selected != null)
            {
                FontStyle actualStyle = selected.IsStyleAvailable(style) ? style : FontStyle.Regular;
                try
                {
                    return new Font(selected, size, actualStyle, GraphicsUnit.Point);
                }
                catch { }
            }

            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }

        public static void Dispose()
        {
            Fonts.Dispose();
        }

        private static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }
            loaded = true;

            try
            {
                string fontDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost",
                    "Fonts");
                Directory.CreateDirectory(fontDirectory);
                string regularFont = Path.Combine(fontDirectory, "ProximaNova-Regular.ttf");
                if (!File.Exists(regularFont))
                {
                    ExtractFromMajestic(fontDirectory);
                }

                foreach (string fontFile in Directory.GetFiles(fontDirectory, "ProximaNova-*.ttf"))
                {
                    Fonts.AddFontFile(fontFile);
                }
            }
            catch
            {
                // Segoe UI is used when Majestic is not installed.
            }
        }

        private static void ExtractFromMajestic(string destinationDirectory)
        {
            string asarPath = Path.Combine(
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

                    stream.Position = dataOffset + offset;
                    byte[] bytes = reader.ReadBytes(size);
                    if (bytes.Length == size)
                    {
                        File.WriteAllBytes(
                            Path.Combine(destinationDirectory, "ProximaNova-" + match.Groups["weight"].Value + ".ttf"),
                            bytes);
                    }
                }
            }
        }
    }

    internal static class MajesticDrawing
    {
        public static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            float diameter = radius * 2F;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180F, 90F);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270F, 90F);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90F, 90F);
            path.CloseFigure();
            return path;
        }

        public static Color Interpolate(Color from, Color to, float amount)
        {
            amount = Math.Max(0F, Math.Min(1F, amount));
            return Color.FromArgb(
                (int)Math.Round(from.A + ((to.A - from.A) * amount)),
                (int)Math.Round(from.R + ((to.R - from.R) * amount)),
                (int)Math.Round(from.G + ((to.G - from.G) * amount)),
                (int)Math.Round(from.B + ((to.B - from.B) * amount)));
        }

        public static float CssEase(float progress)
        {
            progress = Math.Max(0F, Math.Min(1F, progress));
            float low = 0F;
            float high = 1F;
            float parameter = progress;
            for (int index = 0; index < 10; index++)
            {
                parameter = (low + high) * 0.5F;
                float x = CubicBezier(parameter, 0.25F, 0.25F);
                if (x < progress)
                {
                    low = parameter;
                }
                else
                {
                    high = parameter;
                }
            }
            return CubicBezier(parameter, 0.10F, 1F);
        }

        private static float CubicBezier(float parameter, float firstControl, float secondControl)
        {
            float inverse = 1F - parameter;
            return (3F * inverse * inverse * parameter * firstControl)
                + (3F * inverse * parameter * parameter * secondControl)
                + (parameter * parameter * parameter);
        }
    }

    internal abstract class AnimatedButtonBase : Button
    {
        private readonly Timer animationTimer;
        private Color currentFill;
        private Color currentGlyph;
        private Color startFill;
        private Color startGlyph;
        private Color targetFill;
        private Color targetGlyph;
        private long animationStart;
        private int animationDuration;
        private bool pressed;

        protected AnimatedButtonBase()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw,
                true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            TabStop = true;

            animationTimer = new Timer();
            animationTimer.Interval = 15;
            animationTimer.Tick += AnimationTick;
        }

        protected abstract Color IdleFill { get; }
        protected abstract Color HoverFill { get; }
        protected abstract Color PressedFill { get; }
        protected abstract Color IdleGlyph { get; }
        protected abstract Color HoverGlyph { get; }
        protected abstract Color PressedGlyph { get; }
        protected abstract float CornerRadius { get; }

        protected void InitializeVisualState()
        {
            currentFill = IdleFill;
            currentGlyph = IdleGlyph;
            startFill = currentFill;
            startGlyph = currentGlyph;
            targetFill = currentFill;
            targetGlyph = currentGlyph;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (Enabled)
            {
                BeginTransition(HoverFill, HoverGlyph, 200);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pressed = false;
            BeginTransition(IdleFill, IdleGlyph, 200);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && e.Button == MouseButtons.Left)
            {
                pressed = true;
                BeginTransition(PressedFill, PressedGlyph, 90);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            pressed = false;
            if (Enabled && ClientRectangle.Contains(e.Location))
            {
                BeginTransition(HoverFill, HoverGlyph, 160);
            }
            else
            {
                BeginTransition(IdleFill, IdleGlyph, 160);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && !pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                pressed = true;
                BeginTransition(PressedFill, PressedGlyph, 90);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                pressed = false;
                bool pointerInside = ClientRectangle.Contains(PointToClient(Cursor.Position));
                BeginTransition(
                    pointerInside ? HoverFill : IdleFill,
                    pointerInside ? HoverGlyph : IdleGlyph,
                    160);
            }
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (pressed)
            {
                pressed = false;
                BeginTransition(IdleFill, IdleGlyph, 160);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            pressed = false;
            bool pointerInside = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
            BeginTransition(
                pointerInside ? HoverFill : IdleFill,
                pointerInside ? HoverGlyph : IdleGlyph,
                160);
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color parentColor = Parent == null ? Color.FromArgb(22, 22, 22) : Parent.BackColor;
            using (var backgroundBrush = new SolidBrush(parentColor))
            {
                e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF buttonBounds = new RectangleF(0F, 0F, Math.Max(1F, Width - 1F), Math.Max(1F, Height - 1F));
            Color fill = currentFill;
            Color glyph = currentGlyph;
            if (!Enabled)
            {
                fill = MajesticDrawing.Interpolate(fill, parentColor, 0.45F);
                glyph = Color.FromArgb(100, 100, 100);
            }
            if (SystemInformation.HighContrast)
            {
                fill = Enabled && (pressed || ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    ? SystemColors.Highlight
                    : SystemColors.ControlDark;
                glyph = Enabled ? SystemColors.HighlightText : SystemColors.GrayText;
            }

            using (GraphicsPath path = MajesticDrawing.RoundedRectangle(buttonBounds, CornerRadius))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
            }

            DrawContent(e.Graphics, Rectangle.Round(buttonBounds), glyph);
        }

        protected abstract void DrawContent(Graphics graphics, Rectangle bounds, Color glyphColor);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BeginTransition(Color fill, Color glyph, int duration)
        {
            startFill = currentFill;
            startGlyph = currentGlyph;
            targetFill = fill;
            targetGlyph = glyph;
            animationStart = Stopwatch.GetTimestamp();
            animationDuration = Math.Max(1, duration);
            animationTimer.Start();
            Invalidate();
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            double elapsed = (Stopwatch.GetTimestamp() - animationStart) * 1000D / Stopwatch.Frequency;
            float progress = (float)Math.Min(1D, elapsed / animationDuration);
            float eased = MajesticDrawing.CssEase(progress);
            currentFill = MajesticDrawing.Interpolate(startFill, targetFill, eased);
            currentGlyph = MajesticDrawing.Interpolate(startGlyph, targetGlyph, eased);
            Invalidate();
            if (progress >= 1F)
            {
                animationTimer.Stop();
                currentFill = targetFill;
                currentGlyph = targetGlyph;
            }
        }
    }

    internal sealed class MajesticActionButton : AnimatedButtonBase
    {
        public MajesticActionButton()
        {
            InitializeVisualState();
        }

        protected override Color IdleFill { get { return Color.FromArgb(37, 37, 37); } }
        protected override Color HoverFill { get { return Color.FromArgb(232, 28, 90); } }
        protected override Color PressedFill { get { return Color.FromArgb(208, 25, 81); } }
        protected override Color IdleGlyph { get { return Color.White; } }
        protected override Color HoverGlyph { get { return Color.White; } }
        protected override Color PressedGlyph { get { return Color.White; } }
        protected override float CornerRadius { get { return 8F; } }

        protected override void DrawContent(Graphics graphics, Rectangle bounds, Color glyphColor)
        {
            TextRenderer.DrawText(
                graphics,
                Text,
                Font,
                bounds,
                glyphColor,
                TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class MajesticCloseButton : AnimatedButtonBase
    {
        public MajesticCloseButton()
        {
            InitializeVisualState();
        }

        protected override Color IdleFill { get { return Color.FromArgb(0, 231, 24, 42); } }
        protected override Color HoverFill { get { return Color.FromArgb(231, 24, 42); } }
        protected override Color PressedFill { get { return Color.FromArgb(197, 20, 35); } }
        protected override Color IdleGlyph { get { return Color.FromArgb(128, 255, 255, 255); } }
        protected override Color HoverGlyph { get { return Color.White; } }
        protected override Color PressedGlyph { get { return Color.White; } }
        protected override float CornerRadius { get { return 6F; } }

        protected override void DrawContent(Graphics graphics, Rectangle bounds, Color glyphColor)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(glyphColor, 1.6F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                graphics.DrawLine(pen, 9F, 9F, 21F, 21F);
                graphics.DrawLine(pen, 21F, 9F, 9F, 21F);
            }
        }
    }

    internal sealed class MajesticToggle : CheckBox
    {
        private static readonly Color OffColor = Color.FromArgb(37, 37, 37);
        private static readonly Color OffHoverColor = Color.FromArgb(52, 52, 52);
        private static readonly Color OnColor = Color.FromArgb(232, 28, 90);
        private readonly Timer animationTimer;
        private float thumbPosition;
        private float startThumbPosition;
        private float targetThumbPosition;
        private Color currentTrackColor;
        private Color startTrackColor;
        private Color targetTrackColor;
        private long animationStart;
        private bool pointerInside;

        public MajesticToggle()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
                true);
            AutoSize = false;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            currentTrackColor = OffColor;
            targetTrackColor = OffColor;

            animationTimer = new Timer();
            animationTimer.Interval = 15;
            animationTimer.Tick += AnimationTick;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            thumbPosition = Checked ? 1F : 0F;
            startThumbPosition = thumbPosition;
            targetThumbPosition = thumbPosition;
            currentTrackColor = TargetTrackColor();
            startTrackColor = currentTrackColor;
            targetTrackColor = currentTrackColor;
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (!IsHandleCreated)
            {
                thumbPosition = Checked ? 1F : 0F;
                currentTrackColor = Checked ? OnColor : OffColor;
                return;
            }
            BeginTransition();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            pointerInside = true;
            BeginTransition();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pointerInside = false;
            BeginTransition();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
            {
                pointerInside = false;
            }
            BeginTransition();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color parentColor = Parent == null ? Color.FromArgb(22, 22, 22) : Parent.BackColor;
            using (var backgroundBrush = new SolidBrush(parentColor))
            {
                e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
            }

            Color textColor = Enabled ? ForeColor : Color.FromArgb(95, 95, 95);
            Rectangle textBounds = new Rectangle(0, 0, Math.Max(0, Width - 52), Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textBounds,
                textColor,
                TextFormatFlags.Left
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPadding
                | TextFormatFlags.EndEllipsis);

            // Keep a two-pixel inset so antialiasing never clips the rounded cap.
            float trackLeft = Width - 38F;
            float trackTop = (Height - 20F) * 0.5F;
            RectangleF trackBounds = new RectangleF(trackLeft, trackTop, 36F, 20F);
            Color trackColor = currentTrackColor;
            Color knobColor = Color.White;
            if (!Enabled)
            {
                trackColor = MajesticDrawing.Interpolate(trackColor, parentColor, 0.5F);
                knobColor = Color.FromArgb(145, 145, 145);
            }
            if (SystemInformation.HighContrast)
            {
                if (!Enabled)
                {
                    trackColor = SystemColors.ControlDarkDark;
                    knobColor = SystemColors.GrayText;
                }
                else
                {
                    trackColor = Checked ? SystemColors.Highlight : SystemColors.ControlDark;
                    knobColor = Checked ? SystemColors.HighlightText : SystemColors.Window;
                }
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath trackPath = MajesticDrawing.RoundedRectangle(trackBounds, 10F))
            using (var trackBrush = new SolidBrush(trackColor))
            {
                e.Graphics.FillPath(trackBrush, trackPath);
            }

            float knobLeft = trackLeft + 2F + (16F * thumbPosition);
            RectangleF knobBounds = new RectangleF(knobLeft, trackTop + 2F, 16F, 16F);
            using (var knobBrush = new SolidBrush(knobColor))
            {
                e.Graphics.FillEllipse(knobBrush, knobBounds);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animationTimer.Stop();
                animationTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private Color TargetTrackColor()
        {
            if (Checked)
            {
                return OnColor;
            }
            return pointerInside && Enabled ? OffHoverColor : OffColor;
        }

        private void BeginTransition()
        {
            if (!IsHandleCreated)
            {
                return;
            }
            startThumbPosition = thumbPosition;
            targetThumbPosition = Checked ? 1F : 0F;
            startTrackColor = currentTrackColor;
            targetTrackColor = TargetTrackColor();
            animationStart = Stopwatch.GetTimestamp();
            animationTimer.Start();
            Invalidate();
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            double elapsed = (Stopwatch.GetTimestamp() - animationStart) * 1000D / Stopwatch.Frequency;
            float progress = (float)Math.Min(1D, elapsed / 200D);
            float eased = MajesticDrawing.CssEase(progress);
            thumbPosition = startThumbPosition + ((targetThumbPosition - startThumbPosition) * eased);
            currentTrackColor = MajesticDrawing.Interpolate(startTrackColor, targetTrackColor, eased);
            Invalidate(new Rectangle(Math.Max(0, Width - 40), 0, Math.Min(40, Width), Height));
            if (progress >= 1F)
            {
                animationTimer.Stop();
                thumbPosition = targetThumbPosition;
                currentTrackColor = targetTrackColor;
            }
        }
    }

    internal sealed class UpdateProgressForm : Form
    {
        private const int ProgressTrackWidth = 480;
        private readonly Color background = Color.FromArgb(22, 22, 22);
        private readonly Color accent = Color.FromArgb(232, 28, 90);
        private readonly Color muted = Color.FromArgb(142, 142, 142);
        private readonly bool demoMode;
        private readonly Timer progressAnimationTimer;
        private readonly Timer demoTimer;
        private MajesticCloseButton closeButton;
        private MajesticActionButton actionButton;
        private Label headlineLabel;
        private Label descriptionLabel;
        private Label percentLabel;
        private Label phaseLabel;
        private Label detailLabel;
        private Panel progressFill;
        private int displayedProgress;
        private int targetProgress;
        private int demoMilestoneIndex;
        private bool installing;
        private bool successPending;
        private bool successShown;

        private static readonly int[] DemoPercentages =
        {
            0, 5, 10, 17, 25, 35, 44, 55, 68, 76, 87, 94, 100
        };

        private static readonly string[] DemoStages =
        {
            "Подготовка обновления",
            "Подготовка папки установки",
            "Остановка запущенной версии",
            "Распаковка файлов программы",
            "Распаковка компонентов обновления",
            "Проверка файлов программы",
            "Проверка компонентов обновления",
            "Установка профиля производительности",
            "Установка новой версии программы",
            "Обновление компонентов удаления",
            "Сохранение параметров установки",
            "Регистрация новой версии",
            "Обновление установлено"
        };

        public UpdateProgressForm(bool demoMode)
        {
            this.demoMode = demoMode;
            Text = "Majestic Boost Update";
            ClientSize = new Size(560, 345);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = background;
            ForeColor = Color.White;
            Font = CreateUiFont(9F, FontStyle.Regular);
            DoubleBuffered = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            progressAnimationTimer = new Timer();
            progressAnimationTimer.Interval = 15;
            progressAnimationTimer.Tick += ProgressAnimationTick;

            demoTimer = new Timer();
            demoTimer.Interval = 360;
            demoTimer.Tick += DemoTimerTick;

            BuildInterface();
            Resize += delegate { ApplyRoundedRegion(); };
            Shown += UpdateProgressFormShown;
            MouseDown += DragWindow;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = MakeRoundedRectangle(
                new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 11))
            using (var pen = new Pen(Color.FromArgb(56, 56, 56), 1F))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (installing)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                progressAnimationTimer.Stop();
                progressAnimationTimer.Dispose();
                demoTimer.Stop();
                demoTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BuildInterface()
        {
            closeButton = new MajesticCloseButton();
            closeButton.Location = new Point(530, 0);
            closeButton.Size = new Size(30, 30);
            closeButton.AccessibleName = "Закрыть обновление";
            closeButton.AccessibleDescription = "Закрывает окно обновления Majestic Boost";
            closeButton.TabIndex = 1;
            closeButton.Click += delegate
            {
                if (!installing)
                {
                    Close();
                }
            };
            Controls.Add(closeButton);

            var iconBox = new PictureBox();
            iconBox.Location = new Point(40, 32);
            iconBox.Size = new Size(50, 50);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = Icon == null ? null : Icon.ToBitmap();
            iconBox.MouseDown += DragWindow;
            Controls.Add(iconBox);

            var title = MakeLabel("MAJESTIC BOOST", 22F, FontStyle.Bold, Color.White);
            title.Location = new Point(105, 31);
            title.AutoSize = true;
            title.MouseDown += DragWindow;
            Controls.Add(title);

            var version = MakeLabel("UPDATE  •  v" + InstallerEngine.ProductVersion, 8.5F, FontStyle.Bold, accent);
            version.Location = new Point(108, 66);
            version.AutoSize = true;
            version.MouseDown += DragWindow;
            Controls.Add(version);

            headlineLabel = MakeLabel("УСТАНОВКА ОБНОВЛЕНИЯ", 16F, FontStyle.Bold, Color.White);
            headlineLabel.Location = new Point(40, 112);
            headlineLabel.AutoSize = true;
            Controls.Add(headlineLabel);

            descriptionLabel = MakeLabel(
                "Majestic Boost обновляется до версии " + InstallerEngine.ProductVersion,
                10F,
                FontStyle.Regular,
                muted);
            descriptionLabel.Location = new Point(42, 146);
            descriptionLabel.AutoSize = true;
            Controls.Add(descriptionLabel);

            percentLabel = MakeLabel("0%", 24F, FontStyle.Bold, accent);
            percentLabel.Location = new Point(39, 181);
            percentLabel.Size = new Size(120, 42);
            percentLabel.TextAlign = ContentAlignment.MiddleLeft;
            percentLabel.AccessibleName = "Прогресс обновления: 0 процентов";
            Controls.Add(percentLabel);

            phaseLabel = MakeLabel("Подготовка обновления", 10F, FontStyle.Bold, Color.FromArgb(220, 220, 220));
            phaseLabel.Location = new Point(162, 190);
            phaseLabel.Size = new Size(356, 28);
            phaseLabel.TextAlign = ContentAlignment.MiddleLeft;
            phaseLabel.AutoEllipsis = true;
            Controls.Add(phaseLabel);

            var progressTrack = new Panel();
            progressTrack.Location = new Point(40, 231);
            progressTrack.Size = new Size(ProgressTrackWidth, 6);
            progressTrack.BackColor = Color.FromArgb(48, 48, 48);
            Controls.Add(progressTrack);

            progressFill = new Panel();
            progressFill.Location = new Point(0, 0);
            progressFill.Size = new Size(0, 6);
            progressFill.BackColor = accent;
            progressTrack.Controls.Add(progressFill);

            detailLabel = MakeLabel(
                "Не закрывайте установщик до завершения обновления.",
                9F,
                FontStyle.Regular,
                muted);
            detailLabel.Location = new Point(42, 252);
            detailLabel.Size = new Size(478, 34);
            detailLabel.AutoEllipsis = true;
            Controls.Add(detailLabel);

            actionButton = new MajesticActionButton();
            actionButton.Text = "ПРОДОЛЖИТЬ";
            actionButton.Location = new Point(350, 288);
            actionButton.Size = new Size(170, 42);
            actionButton.ForeColor = Color.White;
            actionButton.Font = CreateUiFont(10F, FontStyle.Bold);
            actionButton.AccessibleName = "Продолжить после обновления";
            actionButton.AccessibleDescription = "Запускает обновлённую версию Majestic Boost";
            actionButton.TabIndex = 0;
            actionButton.Visible = false;
            actionButton.Click += ActionButtonClick;
            Controls.Add(actionButton);

            AcceptButton = actionButton;
            CancelButton = closeButton;
        }

        private void UpdateProgressFormShown(object sender, EventArgs e)
        {
            ApplyRoundedRegion();
            BeginInvoke(new Action(StartInstallation));
        }

        private void StartInstallation()
        {
            if (installing)
            {
                return;
            }

            installing = true;
            successPending = false;
            successShown = false;
            displayedProgress = 0;
            targetProgress = 0;
            progressFill.Width = 0;
            percentLabel.Text = "0%";
            percentLabel.AccessibleName = "Прогресс обновления: 0 процентов";
            headlineLabel.Text = "УСТАНОВКА ОБНОВЛЕНИЯ";
            headlineLabel.ForeColor = Color.White;
            descriptionLabel.Text = "Majestic Boost обновляется до версии " + InstallerEngine.ProductVersion;
            phaseLabel.Text = "Подготовка обновления";
            phaseLabel.ForeColor = Color.FromArgb(220, 220, 220);
            detailLabel.Text = "Не закрывайте установщик до завершения обновления.";
            detailLabel.ForeColor = muted;
            actionButton.Visible = false;
            actionButton.Enabled = false;
            closeButton.Enabled = false;
            progressAnimationTimer.Start();

            if (demoMode)
            {
                demoMilestoneIndex = 0;
                demoTimer.Start();
                return;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallerEngine.Install(
                        InstallerEngine.GetDesktopShortcutPreference(),
                        ReportProgressFromWorker);
                    PostToUi(InstallationCompleted);
                }
                catch (Exception exception)
                {
                    PostToUi(delegate { InstallationFailed(exception); });
                }
            });
        }

        private void ReportProgressFromWorker(int percent, string stage)
        {
            PostToUi(delegate { SetProgressTarget(percent, stage); });
        }

        private void PostToUi(Action action)
        {
            if (action == null || IsDisposed || Disposing)
            {
                return;
            }

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
                // The window was disposed while a background operation was finishing.
            }
            catch (InvalidOperationException)
            {
                // The window was closed while a background operation was finishing.
            }
        }

        private void SetProgressTarget(int percent, string stage)
        {
            int normalized = Math.Max(0, Math.Min(100, percent));
            targetProgress = Math.Max(targetProgress, normalized);
            if (!string.IsNullOrWhiteSpace(stage))
            {
                phaseLabel.Text = stage;
            }
            progressAnimationTimer.Start();
        }

        private void InstallationCompleted()
        {
            SetProgressTarget(100, "Обновление установлено");
            successPending = true;
        }

        private void InstallationFailed(Exception exception)
        {
            installing = false;
            successPending = false;
            demoTimer.Stop();
            headlineLabel.Text = "НЕ УДАЛОСЬ ОБНОВИТЬ";
            headlineLabel.ForeColor = Color.FromArgb(255, 102, 122);
            descriptionLabel.Text = "Повторите установку обновления.";
            phaseLabel.Text = "Ошибка установки";
            phaseLabel.ForeColor = Color.FromArgb(255, 102, 122);
            detailLabel.Text = FriendlyError(exception);
            detailLabel.ForeColor = Color.FromArgb(205, 205, 205);
            actionButton.Text = "ПОВТОРИТЬ";
            actionButton.AccessibleName = "Повторить обновление Majestic Boost";
            actionButton.AccessibleDescription = "Повторно запускает установку обновления";
            actionButton.Visible = true;
            actionButton.Enabled = true;
            closeButton.Enabled = true;
            actionButton.Focus();
        }

        private void ProgressAnimationTick(object sender, EventArgs e)
        {
            if (displayedProgress < targetProgress)
            {
                int difference = targetProgress - displayedProgress;
                displayedProgress += Math.Min(3, Math.Max(1, (difference + 11) / 12));
                if (displayedProgress > targetProgress)
                {
                    displayedProgress = targetProgress;
                }
                percentLabel.Text = displayedProgress.ToString(CultureInfo.InvariantCulture) + "%";
                percentLabel.AccessibleName = "Прогресс обновления: " +
                    displayedProgress.ToString(CultureInfo.InvariantCulture) + " процентов";
                progressFill.Width = (int)Math.Round(
                    ProgressTrackWidth * (displayedProgress / 100D),
                    MidpointRounding.AwayFromZero);
            }

            if (successPending && displayedProgress >= 100)
            {
                successPending = false;
                ShowSuccess();
            }
            else if (displayedProgress >= targetProgress)
            {
                progressAnimationTimer.Stop();
            }
        }

        private void DemoTimerTick(object sender, EventArgs e)
        {
            if (demoMilestoneIndex >= DemoPercentages.Length)
            {
                demoTimer.Stop();
                successPending = true;
                progressAnimationTimer.Start();
                return;
            }

            SetProgressTarget(
                DemoPercentages[demoMilestoneIndex],
                DemoStages[demoMilestoneIndex]);
            demoMilestoneIndex++;
        }

        private void ShowSuccess()
        {
            if (successShown)
            {
                return;
            }

            successShown = true;
            installing = false;
            headlineLabel.Text = "ПРОГРАММА УСПЕШНО ОБНОВЛЕНА";
            headlineLabel.ForeColor = Color.White;
            descriptionLabel.Text = "Версия " + InstallerEngine.ProductVersion + " готова к запуску.";
            phaseLabel.Text = "Обновление завершено";
            phaseLabel.ForeColor = accent;
            detailLabel.Text = "Нажмите «Продолжить», чтобы открыть Majestic Boost.";
            detailLabel.ForeColor = muted;
            actionButton.Text = "ПРОДОЛЖИТЬ";
            actionButton.AccessibleName = "Продолжить после обновления";
            actionButton.AccessibleDescription = demoMode
                ? "Закрывает демонстрацию обновления"
                : "Запускает обновлённую версию Majestic Boost";
            actionButton.Visible = true;
            actionButton.Enabled = true;
            closeButton.Enabled = true;
            actionButton.Focus();
        }

        private void ActionButtonClick(object sender, EventArgs e)
        {
            if (!successShown)
            {
                StartInstallation();
                return;
            }

            if (demoMode)
            {
                Close();
                return;
            }

            try
            {
                InstallerEngine.LaunchInstalledApplication();
                Close();
            }
            catch (Exception exception)
            {
                detailLabel.Text = "Не удалось запустить программу: " + FriendlyError(exception);
                detailLabel.ForeColor = Color.FromArgb(255, 102, 122);
                actionButton.Enabled = true;
                actionButton.Focus();
            }
        }

        private static string FriendlyError(Exception exception)
        {
            if (exception == null || string.IsNullOrWhiteSpace(exception.Message))
            {
                return "Неизвестная ошибка. Нажмите «Повторить».";
            }

            string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return message.Length <= 150 ? message : message.Substring(0, 147) + "...";
        }

        private Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = CreateUiFont(size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        private Font CreateUiFont(float size, FontStyle style)
        {
            return demoMode
                ? new Font("Segoe UI", size, style, GraphicsUnit.Point)
                : MajesticFontProvider.Create(size, style);
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = MakeRoundedRectangle(new Rectangle(0, 0, Width, Height), 11))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private static GraphicsPath MakeRoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || installing)
            {
                return;
            }
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly Color background = Color.FromArgb(22, 22, 22);
        private readonly Color panel = Color.FromArgb(27, 27, 27);
        private readonly Color accent = Color.FromArgb(232, 28, 90);
        private readonly Color muted = Color.FromArgb(142, 142, 142);
        private MajesticActionButton installButton;
        private MajesticCloseButton closeButton;
        private MajesticToggle desktopShortcut;
        private Label statusLabel;
        private Panel progressFill;
        private bool installed;

        public InstallerForm()
        {
            Text = "Majestic Boost Setup";
            ClientSize = new Size(560, 360);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = background;
            ForeColor = Color.White;
            Font = MajesticFontProvider.Create(9F, FontStyle.Regular);
            DoubleBuffered = true;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            BuildInterface();
            Resize += delegate { ApplyRoundedRegion(); };
            Shown += delegate { ApplyRoundedRegion(); };
            MouseDown += DragWindow;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = MakeRoundedRectangle(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 11))
            using (var pen = new Pen(Color.FromArgb(56, 56, 56), 1F))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        private void BuildInterface()
        {
            closeButton = new MajesticCloseButton();
            closeButton.Location = new Point(530, 0);
            closeButton.Size = new Size(30, 30);
            closeButton.AccessibleName = "Закрыть установщик";
            closeButton.AccessibleDescription = "Закрывает окно установки Majestic Boost";
            closeButton.TabIndex = 2;
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);

            var iconBox = new PictureBox();
            iconBox.Location = new Point(38, 35);
            iconBox.Size = new Size(52, 52);
            iconBox.SizeMode = PictureBoxSizeMode.Zoom;
            iconBox.Image = Icon.ToBitmap();
            iconBox.MouseDown += DragWindow;
            Controls.Add(iconBox);

            var title = MakeLabel("MAJESTIC BOOST", 22F, FontStyle.Bold, Color.White);
            title.Location = new Point(105, 35);
            title.AutoSize = true;
            title.MouseDown += DragWindow;
            Controls.Add(title);

            var version = MakeLabel("SETUP  •  v" + InstallerEngine.ProductVersion, 8.5F, FontStyle.Bold, accent);
            version.Location = new Point(108, 69);
            version.AutoSize = true;
            version.MouseDown += DragWindow;
            Controls.Add(version);

            var subtitle = MakeLabel("Установщик лаунчера максимальной производительности", 10F, FontStyle.Regular, muted);
            subtitle.Location = new Point(40, 110);
            subtitle.AutoSize = true;
            Controls.Add(subtitle);

            var locationPanel = new Panel();
            locationPanel.Location = new Point(40, 145);
            locationPanel.Size = new Size(480, 70);
            locationPanel.BackColor = panel;
            Controls.Add(locationPanel);

            var locationTitle = MakeLabel("ПАПКА УСТАНОВКИ", 8.5F, FontStyle.Bold, muted);
            locationTitle.Location = new Point(16, 11);
            locationTitle.AutoSize = true;
            locationPanel.Controls.Add(locationTitle);

            var locationValue = MakeLabel(InstallerEngine.InstallDirectory, 9.5F, FontStyle.Regular, Color.FromArgb(235, 235, 235));
            locationValue.Location = new Point(16, 34);
            locationValue.AutoEllipsis = true;
            locationValue.Size = new Size(448, 24);
            locationPanel.Controls.Add(locationValue);

            desktopShortcut = new MajesticToggle();
            desktopShortcut.Text = "Создать ярлык на рабочем столе";
            desktopShortcut.Checked = InstallerEngine.GetDesktopShortcutPreference();
            desktopShortcut.Location = new Point(42, 226);
            desktopShortcut.Size = new Size(478, 26);
            desktopShortcut.ForeColor = Color.FromArgb(195, 195, 195);
            desktopShortcut.Font = MajesticFontProvider.Create(9.5F, FontStyle.Regular);
            desktopShortcut.AccessibleName = "Создать ярлык на рабочем столе";
            desktopShortcut.AccessibleDescription = "Включает или отключает создание ярлыка Majestic Boost на рабочем столе";
            desktopShortcut.TabIndex = 0;
            Controls.Add(desktopShortcut);

            var progressTrack = new Panel();
            progressTrack.Location = new Point(40, 276);
            progressTrack.Size = new Size(480, 4);
            progressTrack.BackColor = Color.FromArgb(48, 48, 48);
            Controls.Add(progressTrack);

            progressFill = new Panel();
            progressFill.Location = new Point(0, 0);
            progressFill.Size = new Size(0, 4);
            progressFill.BackColor = accent;
            progressTrack.Controls.Add(progressFill);

            statusLabel = MakeLabel("ГОТОВО К УСТАНОВКЕ", 8.5F, FontStyle.Bold, muted);
            statusLabel.Location = new Point(42, 292);
            statusLabel.AutoSize = true;
            Controls.Add(statusLabel);

            installButton = new MajesticActionButton();
            installButton.Text = "УСТАНОВИТЬ";
            installButton.Location = new Point(350, 299);
            installButton.Size = new Size(170, 42);
            installButton.ForeColor = Color.White;
            installButton.Font = MajesticFontProvider.Create(10F, FontStyle.Bold);
            installButton.AccessibleName = "Установить Majestic Boost";
            installButton.AccessibleDescription = "Начинает установку приложения";
            installButton.TabIndex = 1;
            installButton.Click += InstallButtonClick;
            Controls.Add(installButton);

            AcceptButton = installButton;
            CancelButton = closeButton;
        }

        private void InstallButtonClick(object sender, EventArgs e)
        {
            if (installed)
            {
                InstallerEngine.LaunchInstalledApplication();
                Close();
                return;
            }

            installButton.Enabled = false;
            closeButton.Enabled = false;
            desktopShortcut.Enabled = false;
            statusLabel.Text = "УСТАНАВЛИВАЮ...";
            statusLabel.ForeColor = Color.FromArgb(255, 139, 175);
            AnimateProgress(120);

            try
            {
                InstallerEngine.Install(desktopShortcut.Checked);
                AnimateProgress(480);
                statusLabel.Text = "УСТАНОВЛЕНО";
                statusLabel.ForeColor = accent;
                installButton.Text = "ЗАПУСТИТЬ";
                installButton.AccessibleName = "Запустить Majestic Boost";
                installButton.AccessibleDescription = "Запускает установленное приложение Majestic Boost";
                installButton.Enabled = true;
                closeButton.Enabled = true;
                installed = true;
            }
            catch (Exception exception)
            {
                statusLabel.Text = "ОШИБКА УСТАНОВКИ";
                statusLabel.ForeColor = Color.FromArgb(255, 102, 122);
                installButton.Text = "ПОВТОРИТЬ";
                installButton.AccessibleName = "Повторить установку Majestic Boost";
                installButton.AccessibleDescription = "Повторно запускает установку приложения";
                installButton.Enabled = true;
                closeButton.Enabled = true;
                desktopShortcut.Enabled = true;
                MessageBox.Show(
                    "Не удалось установить Majestic Boost:\r\n" + exception.Message,
                    "Ошибка установки",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void AnimateProgress(int width)
        {
            progressFill.Width = Math.Max(0, Math.Min(480, width));
            progressFill.Refresh();
            Application.DoEvents();
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.Font = MajesticFontProvider.Create(size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = MakeRoundedRectangle(new Rectangle(0, 0, Width, Height), 11))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        private static GraphicsPath MakeRoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
    }
}
