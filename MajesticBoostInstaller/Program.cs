using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Majestic Boost Setup")]
[assembly: AssemblyDescription("Installer for Majestic Boost")]
[assembly: AssemblyCompany("Codex Gaming Optimization")]
[assembly: AssemblyProduct("Majestic Boost")]
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

namespace MajesticBoostSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool uninstall = string.Equals(
                Path.GetFileNameWithoutExtension(Application.ExecutablePath),
                "Uninstall",
                StringComparison.OrdinalIgnoreCase);
            bool quiet = false;
            bool silentInstall = false;
            bool launchAfterInstall = false;
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
            }

            if (uninstall)
            {
                InstallerEngine.Uninstall(quiet);
                return;
            }

            if (silentInstall)
            {
                try
                {
                    InstallerEngine.Install(true);
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ApplicationExit += delegate { MajesticFontProvider.Dispose(); };
            Application.Run(new InstallerForm());
        }
    }

    internal static class InstallerEngine
    {
        public const string ProductName = "Majestic Boost";
        public const string ProductVersion = "1.3.0";
        public static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProductName);
        public static readonly string InstalledExe = Path.Combine(InstallDirectory, "MajesticBoost.exe");
        public static readonly string InstalledGameBoostScript = Path.Combine(InstallDirectory, "Game-Boost.ps1");
        public static readonly string InstalledMaxFpsApplyScript = Path.Combine(InstallDirectory, "MaxFPS-Apply.ps1");
        public static readonly string InstalledMaxFpsRestoreScript = Path.Combine(InstallDirectory, "MaxFPS-Restore.ps1");
        public static readonly string UninstallerExe = Path.Combine(InstallDirectory, "Uninstall.exe");

        private const string UninstallRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MajesticBoost";
        private const string AppPathsRegistryPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\MajesticBoost.exe";

        public static void Install(bool createDesktopShortcut)
        {
            Directory.CreateDirectory(InstallDirectory);
            StopInstalledApplication();

            InstallPayloadsAtomically();
            if (!string.Equals(Application.ExecutablePath, UninstallerExe, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Application.ExecutablePath, UninstallerExe, true);
            }

            string startMenuDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                ProductName);
            Directory.CreateDirectory(startMenuDirectory);
            CreateShortcut(
                Path.Combine(startMenuDirectory, ProductName + ".lnk"),
                InstalledExe,
                InstallDirectory,
                "Animated Majestic MAX FPS launcher.");

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

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey uninstall = baseKey.CreateSubKey(UninstallRegistryPath))
            {
                uninstall.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                uninstall.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
                uninstall.SetValue("Publisher", "Codex Gaming Optimization", RegistryValueKind.String);
                uninstall.SetValue("InstallLocation", InstallDirectory, RegistryValueKind.String);
                uninstall.SetValue("DisplayIcon", InstalledExe + ",0", RegistryValueKind.String);
                uninstall.SetValue("UninstallString", Quote(UninstallerExe) + " /uninstall", RegistryValueKind.String);
                uninstall.SetValue("QuietUninstallString", Quote(UninstallerExe) + " /uninstall /quiet", RegistryValueKind.String);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                uninstall.SetValue("EstimatedSize", CalculateEstimatedSizeKb(), RegistryValueKind.DWord);
                uninstall.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey appPath = baseKey.CreateSubKey(AppPathsRegistryPath))
            {
                appPath.SetValue(string.Empty, InstalledExe, RegistryValueKind.String);
                appPath.SetValue("Path", InstallDirectory, RegistryValueKind.String);
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

        private static void InstallPayloadsAtomically()
        {
            string token = Guid.NewGuid().ToString("N");
            string appStage = Path.Combine(InstallDirectory, ".MajesticBoost-" + token + ".stage");
            string gameBoostStage = Path.Combine(InstallDirectory, ".Game-Boost-" + token + ".stage");
            string maxFpsApplyStage = Path.Combine(InstallDirectory, ".MaxFPS-Apply-" + token + ".stage");
            string maxFpsRestoreStage = Path.Combine(InstallDirectory, ".MaxFPS-Restore-" + token + ".stage");
            string appBackup = Path.Combine(InstallDirectory, ".MajesticBoost-" + token + ".backup");
            string gameBoostBackup = Path.Combine(InstallDirectory, ".Game-Boost-" + token + ".backup");
            string maxFpsApplyBackup = Path.Combine(InstallDirectory, ".MaxFPS-Apply-" + token + ".backup");
            string maxFpsRestoreBackup = Path.Combine(InstallDirectory, ".MaxFPS-Restore-" + token + ".backup");
            bool appExisted = File.Exists(InstalledExe);
            bool gameBoostExisted = File.Exists(InstalledGameBoostScript);
            bool maxFpsApplyExisted = File.Exists(InstalledMaxFpsApplyScript);
            bool maxFpsRestoreExisted = File.Exists(InstalledMaxFpsRestoreScript);
            bool appCommitted = false;
            bool gameBoostCommitted = false;
            bool maxFpsApplyCommitted = false;
            bool maxFpsRestoreCommitted = false;
            bool appRestored = true;
            bool gameBoostRestored = true;
            bool maxFpsApplyRestored = true;
            bool maxFpsRestoreRestored = true;
            bool installationSucceeded = false;

            try
            {
                ExtractResource("MajesticBoost.Payload.exe", appStage);
                ExtractResource("MajesticBoost.GameBoost.ps1", gameBoostStage);
                ExtractResource("MajesticBoost.MaxFPSApply.ps1", maxFpsApplyStage);
                ExtractResource("MajesticBoost.MaxFPSRestore.ps1", maxFpsRestoreStage);

                // Do not alter the existing installation until every payload is ready.
                ValidateStagedPayload(appStage, true);
                ValidateStagedPayload(gameBoostStage, false);
                ValidateStagedPayload(maxFpsApplyStage, false);
                ValidateStagedPayload(maxFpsRestoreStage, false);

                // Publish dependency scripts first and the application itself last.
                CommitStagedFile(gameBoostStage, InstalledGameBoostScript, gameBoostBackup, gameBoostExisted);
                gameBoostCommitted = true;
                CommitStagedFile(maxFpsApplyStage, InstalledMaxFpsApplyScript, maxFpsApplyBackup, maxFpsApplyExisted);
                maxFpsApplyCommitted = true;
                CommitStagedFile(maxFpsRestoreStage, InstalledMaxFpsRestoreScript, maxFpsRestoreBackup, maxFpsRestoreExisted);
                maxFpsRestoreCommitted = true;
                CommitStagedFile(appStage, InstalledExe, appBackup, appExisted);
                appCommitted = true;
                installationSucceeded = true;
            }
            catch
            {
                // Roll back in the exact reverse order of the commits above.
                if (appCommitted)
                {
                    appRestored = RestoreCommittedFile(InstalledExe, appBackup, appExisted);
                }
                if (maxFpsRestoreCommitted)
                {
                    maxFpsRestoreRestored = RestoreCommittedFile(
                        InstalledMaxFpsRestoreScript,
                        maxFpsRestoreBackup,
                        maxFpsRestoreExisted);
                }
                if (maxFpsApplyCommitted)
                {
                    maxFpsApplyRestored = RestoreCommittedFile(
                        InstalledMaxFpsApplyScript,
                        maxFpsApplyBackup,
                        maxFpsApplyExisted);
                }
                if (gameBoostCommitted)
                {
                    gameBoostRestored = RestoreCommittedFile(
                        InstalledGameBoostScript,
                        gameBoostBackup,
                        gameBoostExisted);
                }
                throw;
            }
            finally
            {
                DeleteIfExists(appStage);
                DeleteIfExists(gameBoostStage);
                DeleteIfExists(maxFpsApplyStage);
                DeleteIfExists(maxFpsRestoreStage);

                // Preserve a backup whenever restoring that payload failed.
                if (installationSucceeded || !appCommitted || appRestored)
                {
                    DeleteIfExists(appBackup);
                }
                if (installationSucceeded || !maxFpsRestoreCommitted || maxFpsRestoreRestored)
                {
                    DeleteIfExists(maxFpsRestoreBackup);
                }
                if (installationSucceeded || !maxFpsApplyCommitted || maxFpsApplyRestored)
                {
                    DeleteIfExists(maxFpsApplyBackup);
                }
                if (installationSucceeded || !gameBoostCommitted || gameBoostRestored)
                {
                    DeleteIfExists(gameBoostBackup);
                }
            }
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

        private static bool RestoreCommittedFile(string destination, string backup, bool destinationExisted)
        {
            try
            {
                if (destinationExisted && File.Exists(backup))
                {
                    if (File.Exists(destination))
                    {
                        File.Replace(backup, destination, null, true);
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

            if (Focused && ShowFocusCues)
            {
                Rectangle focusBounds = Rectangle.Inflate(ClientRectangle, -4, -4);
                ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, glyph, fill);
            }
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

            float trackLeft = Width - 36F;
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

            if (Focused && ShowFocusCues)
            {
                Rectangle focusBounds = Rectangle.Round(trackBounds);
                focusBounds.Inflate(-2, -2);
                ControlPaint.DrawFocusRectangle(e.Graphics, focusBounds, textColor, parentColor);
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
            desktopShortcut.Checked = true;
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
