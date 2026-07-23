using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MajesticBoost
{
    internal sealed class UpdateRequiredEventArgs : EventArgs
    {
        public UpdateRequiredEventArgs(string currentVersion, string availableVersion)
        {
            CurrentVersion = currentVersion;
            AvailableVersion = availableVersion;
        }

        public string CurrentVersion { get; private set; }

        public string AvailableVersion { get; private set; }
    }

    /// <summary>
    /// Startup update gate for the unpackaged WPF application.
    /// A missing, unreachable, or malformed manifest never prevents startup. Once a
    /// valid newer release is discovered, however, the gate remains visible until a
    /// verified installer is launched.
    /// </summary>
    internal sealed class UpdateFlowOverlay : Grid
    {
        private enum UpdateState
        {
            Hidden,
            Checking,
            Required,
            Downloading,
            Retry
        }

        private enum UpdateProgressStage
        {
            Downloading,
            Verifying,
            Launching
        }

        private struct UpdateProgressInfo
        {
            public UpdateProgressStage Stage;
            public long DownloadedBytes;
            public long TotalBytes;
        }

        private sealed class UpdateManifest
        {
            public SemanticVersion Version;
            public string InstallerUrl;
            public string Sha256;
            public long Size;
        }

        private struct SemanticVersion : IComparable<SemanticVersion>
        {
            public int Major;
            public int Minor;
            public int Patch;

            public int CompareTo(SemanticVersion other)
            {
                int result = Major.CompareTo(other.Major);
                if (result != 0)
                {
                    return result;
                }
                result = Minor.CompareTo(other.Minor);
                if (result != 0)
                {
                    return result;
                }
                return Patch.CompareTo(other.Patch);
            }

            public override string ToString()
            {
                return Major.ToString(CultureInfo.InvariantCulture) + "." +
                    Minor.ToString(CultureInfo.InvariantCulture) + "." +
                    Patch.ToString(CultureInfo.InvariantCulture);
            }
        }

        private enum JsonValueKind
        {
            String,
            Number,
            Object,
            Array,
            Boolean,
            Null
        }

        private sealed class JsonValue
        {
            public JsonValueKind Kind;
            public string Text;
            public Dictionary<string, JsonValue> ObjectValue;
            public List<JsonValue> ArrayValue;
        }

        /// <summary>
        /// Small bounded JSON parser used to avoid adding a serializer dependency to
        /// the direct-csc .NET Framework build. It accepts normal JSON for future
        /// fields, while required manifest fields are type-checked separately.
        /// </summary>
        private sealed class JsonParser
        {
            private readonly string source;
            private int position;

            public JsonParser(string json)
            {
                source = json ?? string.Empty;
            }

            public bool TryParseRootObject(out Dictionary<string, JsonValue> result)
            {
                result = null;
                try
                {
                    SkipWhitespace();
                    JsonValue root = ParseValue(0);
                    SkipWhitespace();
                    if (position != source.Length || root.Kind != JsonValueKind.Object)
                    {
                        return false;
                    }
                    result = root.ObjectValue;
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }

            private JsonValue ParseValue(int depth)
            {
                if (depth > 16)
                {
                    throw new FormatException("JSON nesting is too deep.");
                }
                if (position >= source.Length)
                {
                    throw new FormatException("Unexpected end of JSON.");
                }

                char current = source[position];
                if (current == '"')
                {
                    return new JsonValue { Kind = JsonValueKind.String, Text = ParseString() };
                }
                if (current == '{')
                {
                    return ParseObject(depth + 1);
                }
                if (current == '[')
                {
                    return ParseArray(depth + 1);
                }
                if (current == 't')
                {
                    ConsumeLiteral("true");
                    return new JsonValue { Kind = JsonValueKind.Boolean, Text = "true" };
                }
                if (current == 'f')
                {
                    ConsumeLiteral("false");
                    return new JsonValue { Kind = JsonValueKind.Boolean, Text = "false" };
                }
                if (current == 'n')
                {
                    ConsumeLiteral("null");
                    return new JsonValue { Kind = JsonValueKind.Null };
                }
                if (current == '-' || (current >= '0' && current <= '9'))
                {
                    return new JsonValue { Kind = JsonValueKind.Number, Text = ParseNumber() };
                }
                throw new FormatException("Unsupported JSON token.");
            }

            private JsonValue ParseObject(int depth)
            {
                Expect('{');
                SkipWhitespace();
                var values = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                if (TryConsume('}'))
                {
                    return new JsonValue { Kind = JsonValueKind.Object, ObjectValue = values };
                }

                while (true)
                {
                    if (position >= source.Length || source[position] != '"')
                    {
                        throw new FormatException("Object key must be a string.");
                    }
                    string key = ParseString();
                    if (values.ContainsKey(key))
                    {
                        throw new FormatException("Duplicate JSON key.");
                    }
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    values.Add(key, ParseValue(depth));
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        break;
                    }
                    Expect(',');
                    SkipWhitespace();
                }
                return new JsonValue { Kind = JsonValueKind.Object, ObjectValue = values };
            }

            private JsonValue ParseArray(int depth)
            {
                Expect('[');
                SkipWhitespace();
                var values = new List<JsonValue>();
                if (TryConsume(']'))
                {
                    return new JsonValue { Kind = JsonValueKind.Array, ArrayValue = values };
                }
                while (true)
                {
                    values.Add(ParseValue(depth));
                    SkipWhitespace();
                    if (TryConsume(']'))
                    {
                        break;
                    }
                    Expect(',');
                    SkipWhitespace();
                }
                return new JsonValue { Kind = JsonValueKind.Array, ArrayValue = values };
            }

            private string ParseString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (position < source.Length)
                {
                    char value = source[position++];
                    if (value == '"')
                    {
                        return builder.ToString();
                    }
                    if (value < 0x20)
                    {
                        throw new FormatException("Control character in JSON string.");
                    }
                    if (value != '\\')
                    {
                        builder.Append(value);
                        continue;
                    }
                    if (position >= source.Length)
                    {
                        throw new FormatException("Invalid JSON escape.");
                    }
                    char escaped = source[position++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u': builder.Append(ParseUnicodeEscape()); break;
                        default: throw new FormatException("Invalid JSON escape.");
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }

            private char ParseUnicodeEscape()
            {
                if (position + 4 > source.Length)
                {
                    throw new FormatException("Incomplete Unicode escape.");
                }
                int value = 0;
                for (int index = 0; index < 4; index++)
                {
                    value = (value << 4) | HexValue(source[position++]);
                }
                return (char)value;
            }

            private static int HexValue(char value)
            {
                if (value >= '0' && value <= '9') return value - '0';
                if (value >= 'a' && value <= 'f') return value - 'a' + 10;
                if (value >= 'A' && value <= 'F') return value - 'A' + 10;
                throw new FormatException("Invalid Unicode escape.");
            }

            private string ParseNumber()
            {
                int start = position;
                TryConsume('-');
                if (position >= source.Length)
                {
                    throw new FormatException("Incomplete number.");
                }
                if (source[position] == '0')
                {
                    position++;
                    if (position < source.Length && char.IsDigit(source[position]))
                    {
                        throw new FormatException("Leading zero in number.");
                    }
                }
                else
                {
                    ConsumeDigits();
                }
                if (TryConsume('.'))
                {
                    ConsumeDigits();
                }
                if (position < source.Length && (source[position] == 'e' || source[position] == 'E'))
                {
                    position++;
                    if (position < source.Length && (source[position] == '+' || source[position] == '-'))
                    {
                        position++;
                    }
                    ConsumeDigits();
                }
                return source.Substring(start, position - start);
            }

            private void ConsumeDigits()
            {
                int start = position;
                while (position < source.Length && char.IsDigit(source[position]))
                {
                    position++;
                }
                if (position == start)
                {
                    throw new FormatException("Expected a digit.");
                }
            }

            private void ConsumeLiteral(string expected)
            {
                if (position + expected.Length > source.Length ||
                    !string.Equals(source.Substring(position, expected.Length), expected, StringComparison.Ordinal))
                {
                    throw new FormatException("Invalid JSON literal.");
                }
                position += expected.Length;
            }

            private void SkipWhitespace()
            {
                while (position < source.Length)
                {
                    char value = source[position];
                    if (value != ' ' && value != '\t' && value != '\r' && value != '\n' && value != '\ufeff')
                    {
                        break;
                    }
                    position++;
                }
            }

            private void Expect(char expected)
            {
                if (!TryConsume(expected))
                {
                    throw new FormatException("Unexpected JSON character.");
                }
            }

            private bool TryConsume(char expected)
            {
                if (position < source.Length && source[position] == expected)
                {
                    position++;
                    return true;
                }
                return false;
            }
        }

        private const string ManifestUrl =
            "https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/update-v2.json";
        private const string ManifestSignatureUrl =
            "https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/update-v2.json.sig";
        private const string RepositoryHeadUrl =
            "https://api.github.com/repos/alosev394-ai/MajesticBoost/git/ref/heads/main";
        private const string RepositoryRawPrefix =
            "https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/";
        private const string InstallerUrlPrefix =
            "https://raw.githubusercontent.com/alosev394-ai/MajesticBoost/main/dist/MajesticBoost-Setup-";
        private const string UpdateSigningPublicKeyXml =
            "<RSAKeyValue><Modulus>vCSgQnLtxkncktDMNkZo6cnqx3cBrLMm8z6R+jj/ljBCAm/yiC8fs1GTy7mzPBkH+LhEiEYJlx/HAVVfVXUI4hMEamtYUffbjkeCwrcpOTm9dBXDEiLOQ4ZV5Niisvws/TVqCHPwZj8ck4c/gISjUWotDGkuViPThl5suJImn4zXSo9pnJS5c2G5Pn62NMk2L3HaCmBPSeuFMbYah3XYgjQj7+K8LQ2HkXIwNl9pcJc/Pt8VarA7lVH5u9boct9YIe811iLAyKZ/h+xxN2stBKEE1Eb+HQnO6X6SrdmY+I0jjqsT1uy7yNwAE+ASlAu7iAw+L+nQB1ndi0F2/TWQ73J9Nw5E/GLtVkco9p0aCsiYvBX99Cu+02EMuICSRzfljKWfCD+TIlyX0HzDnLhFV+M3JVweSRLo1UWlyfOWdda3Re4mSUXk0YNyGegCnW/PFSjKgvm9ufYeEHTFoiLCGrsPknSsH5nrSFqCk/UCefupyJnNLfLB53SM8luedAJ9</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
        private const int ManifestSchemaVersion = 1;
        private const int ManifestMaximumBytes = 16384;
        private const int SignatureMaximumBytes = 1024;
        private const int RepositoryHeadMaximumBytes = 4096;
        private const int Rsa3072SignatureBytes = 384;
        private const long InstallerMaximumBytes = 268435456L;
        private const int ManifestRequestTimeoutMilliseconds = 5000;
        private const int ManifestTotalTimeoutMilliseconds = 20000;
        private const int ManifestFetchAttempts = 3;
        private const int ManifestRetryDelayMilliseconds = 700;
        private const int DownloadReadTimeoutMilliseconds = 20000;
        private const int DownloadTotalTimeoutMilliseconds = 120000;

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
        private readonly bool demoMode;

        private UpdateState state;
        private UpdateManifest availableUpdate;
        private Task<bool> checkTask;
        private Button preferredFocusButton;
        private Border progressFill;
        private TextBlock progressPercentText;
        private TextBlock progressStageText;
        private TextBlock progressBytesText;
        private bool allowOwnerClose;
        private bool checkCompleted;
        private bool updateOperationRunning;

        public UpdateFlowOverlay(
            Window ownerWindow,
            string[] launchArguments,
            FontFamily normalFont,
            FontFamily boldFont)
        {
            if (ownerWindow == null)
            {
                throw new ArgumentNullException("ownerWindow");
            }

            owner = ownerWindow;
            arguments = launchArguments ?? new string[0];
            regularFont = normalFont ?? new FontFamily("Segoe UI");
            semiboldFont = boldFont ?? regularFont;
            demoMode = HasArgument("--demo-update");

            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(BackgroundColor);
            Visibility = Visibility.Collapsed;
            Focusable = true;
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
            AutomationProperties.SetName(this, "Проверка обновлений Majestic Boost");

            card = new Border
            {
                Width = 386,
                Height = 410,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
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

        public event EventHandler<UpdateRequiredEventArgs> UpdateRequired;

        public bool IsFlowVisible
        {
            get { return Visibility == Visibility.Visible; }
        }

        public bool IsBlocking
        {
            get { return IsFlowVisible && !allowOwnerClose; }
        }

        /// <summary>
        /// True while the overlay must own mouse and keyboard input. The host window
        /// should test this before handling its boost shortcuts.
        /// </summary>
        public bool ConsumesApplicationInput
        {
            get { return IsFlowVisible; }
        }

        /// <summary>
        /// Becomes true only after a completed fail-open check found no mandatory
        /// update. It is false before the check, while checking, and while an update
        /// gate is visible.
        /// </summary>
        public bool CanContinueStartup
        {
            get { return checkCompleted && state == UpdateState.Hidden; }
        }

        public string AvailableVersion
        {
            get { return availableUpdate == null ? null : availableUpdate.Version.ToString(); }
        }

        /// <summary>
        /// Checks GitHub once for this overlay instance. True means the caller may
        /// continue into the normal startup flow; false means the update overlay owns
        /// the window until its verified installer is launched.
        /// </summary>
        public Task<bool> CheckForUpdatesAsync()
        {
            if (checkTask == null)
            {
                checkTask = CheckForUpdatesCoreAsync();
            }
            return checkTask;
        }

        public bool ShouldCancelWindowClose()
        {
            return state == UpdateState.Downloading && !allowOwnerClose;
        }

        public void HandleEscape()
        {
            if (!IsFlowVisible)
            {
                return;
            }
            FocusPreferredButton();
        }

        private async Task<bool> CheckForUpdatesCoreAsync()
        {
            allowOwnerClose = false;
            checkCompleted = false;
            ShowChecking();

            if (demoMode)
            {
                await Task.Delay(550);
                SemanticVersion currentDemoVersion = GetCurrentVersion();
                availableUpdate = new UpdateManifest
                {
                    Version = new SemanticVersion
                    {
                        Major = currentDemoVersion.Major + 1,
                        Minor = 0,
                        Patch = 0
                    },
                    InstallerUrl = InstallerUrlPrefix +
                        (currentDemoVersion.Major + 1).ToString(CultureInfo.InvariantCulture) +
                        ".0.0.exe",
                    Sha256 = new string('0', 64),
                    Size = 24L * 1024L * 1024L
                };
                ShowUpdateRequired(GetCurrentVersion(), availableUpdate.Version);
                RaiseUpdateRequired(GetCurrentVersion(), availableUpdate.Version);
                checkCompleted = true;
                return false;
            }

            try
            {
                UpdateManifest manifest = await Task.Run(new Func<UpdateManifest>(FetchAndValidateManifest));
                SemanticVersion currentVersion = GetCurrentVersion();
                if (manifest.Version.CompareTo(currentVersion) <= 0)
                {
                    Log("Update check completed: current version is up to date.");
                    checkCompleted = true;
                    HideFlow();
                    return true;
                }

                availableUpdate = manifest;
                Log("Update required: " + currentVersion + " -> " + manifest.Version + ".");
                ShowUpdateRequired(currentVersion, manifest.Version);
                RaiseUpdateRequired(currentVersion, manifest.Version);
                checkCompleted = true;
                return false;
            }
            catch (Exception ex)
            {
                // Availability and manifest problems are deliberately fail-open. A
                // bad network day must not brick an otherwise usable installed app.
                Log("Update check skipped: " + DescribeException(ex));
                checkCompleted = true;
                HideFlow();
                return true;
            }
        }

        private UpdateManifest FetchAndValidateManifest()
        {
            byte[] payload = null;
            byte[] signaturePayload = null;
            Exception lastTransientFailure = null;
            Stopwatch totalTimer = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= ManifestFetchAttempts; attempt++)
            {
                string cacheToken = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:x16}-{1}",
                    DateTime.UtcNow.Ticks,
                    attempt);
                try
                {
                    byte[] headPayload = DownloadSmallFile(
                        BuildRepositoryHeadRequestAddress(cacheToken),
                        RepositoryHeadMaximumBytes,
                        GetManifestRequestTimeout(totalTimer),
                        totalTimer,
                        ManifestTotalTimeoutMilliseconds);
                    string commitSha = ParseRepositoryHeadCommit(headPayload);
                    payload = DownloadSmallFile(
                        BuildImmutableManifestAddress(
                            commitSha,
                            "update-v2.json"),
                        ManifestMaximumBytes,
                        GetManifestRequestTimeout(totalTimer),
                        totalTimer,
                        ManifestTotalTimeoutMilliseconds);
                    signaturePayload = DownloadSmallFile(
                        BuildImmutableManifestAddress(
                            commitSha,
                            "update-v2.json.sig"),
                        SignatureMaximumBytes,
                        GetManifestRequestTimeout(totalTimer),
                        totalTimer,
                        ManifestTotalTimeoutMilliseconds);
                    lastTransientFailure = null;
                    break;
                }
                catch (WebException ex)
                {
                    if (!IsTransientManifestFailure(ex) ||
                        attempt >= ManifestFetchAttempts)
                    {
                        throw;
                    }
                    lastTransientFailure = ex;
                    Log(
                        "Temporary update check failure; retry " +
                        attempt.ToString(CultureInfo.InvariantCulture) +
                        " of " +
                        ManifestFetchAttempts.ToString(CultureInfo.InvariantCulture) +
                        ": " +
                        DescribeException(ex));
                    WaitForManifestRetry(
                        totalTimer,
                        ManifestRetryDelayMilliseconds * attempt,
                        ex);
                }
            }
            if (payload == null || signaturePayload == null)
            {
                throw new WebException(
                    "The signed update manifest could not be downloaded.",
                    lastTransientFailure);
            }
            byte[] signature = DecodeManifestSignature(signaturePayload);
            VerifyManifestSignature(payload, signature);
            string json = new UTF8Encoding(false, true).GetString(payload);
            Dictionary<string, JsonValue> root;
            if (!new JsonParser(json).TryParseRootObject(out root))
            {
                throw new InvalidDataException("Manifest is not valid JSON.");
            }

            JsonValue schemaValue = RequireField(root, "schemaVersion", JsonValueKind.Number);
            JsonValue versionValue = RequireField(root, "version", JsonValueKind.String);
            JsonValue installerValue = RequireField(root, "installerUrl", JsonValueKind.String);
            JsonValue shaValue = RequireField(root, "sha256", JsonValueKind.String);
            JsonValue sizeValue = RequireField(root, "size", JsonValueKind.Number);

            int schemaVersion;
            if (!TryParseStrictInteger(schemaValue.Text, out schemaVersion) ||
                schemaVersion != ManifestSchemaVersion)
            {
                throw new InvalidDataException("Unsupported manifest schemaVersion.");
            }

            SemanticVersion version;
            if (!TryParseSemanticVersion(versionValue.Text, out version))
            {
                throw new InvalidDataException("Invalid manifest version.");
            }

            string expectedInstallerUrl = InstallerUrlPrefix + version + ".exe";
            if (!string.Equals(installerValue.Text, expectedInstallerUrl, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Installer URL is outside the trusted release path.");
            }
            Uri installerUri;
            if (!Uri.TryCreate(installerValue.Text, UriKind.Absolute, out installerUri) ||
                !string.Equals(installerUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
                !string.Equals(installerUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                !installerUri.IsDefaultPort ||
                !string.IsNullOrEmpty(installerUri.Query) ||
                !string.IsNullOrEmpty(installerUri.Fragment))
            {
                throw new InvalidDataException("Installer URL is invalid.");
            }

            string sha256 = shaValue.Text;
            if (!IsSha256(sha256))
            {
                throw new InvalidDataException("Invalid manifest SHA-256.");
            }

            long size;
            if (!TryParseStrictLong(sizeValue.Text, out size) ||
                size <= 0 || size > InstallerMaximumBytes)
            {
                throw new InvalidDataException("Invalid installer size.");
            }

            return new UpdateManifest
            {
                Version = version,
                InstallerUrl = installerValue.Text,
                Sha256 = sha256.ToUpperInvariant(),
                Size = size
            };
        }

        private static string BuildRepositoryHeadRequestAddress(
            string cacheToken)
        {
            Uri uri;
            if (!Uri.TryCreate(RepositoryHeadUrl, UriKind.Absolute, out uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
                !string.Equals(
                    uri.Host,
                    "api.github.com",
                    StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                string.IsNullOrWhiteSpace(cacheToken))
            {
                throw new InvalidDataException("Manifest request URL is invalid.");
            }

            var builder = new UriBuilder(uri)
            {
                Query = "mb=" + Uri.EscapeDataString(cacheToken)
            };
            return builder.Uri.AbsoluteUri;
        }

        private static string ParseRepositoryHeadCommit(byte[] payload)
        {
            if (payload == null ||
                payload.Length == 0 ||
                payload.Length > RepositoryHeadMaximumBytes)
            {
                throw new InvalidDataException("Repository head response size is invalid.");
            }
            string json = new UTF8Encoding(false, true).GetString(payload);
            Dictionary<string, JsonValue> root;
            if (!new JsonParser(json).TryParseRootObject(out root))
            {
                throw new InvalidDataException("Repository head response is not valid JSON.");
            }
            JsonValue objectValue = RequireField(
                root,
                "object",
                JsonValueKind.Object);
            JsonValue shaValue = RequireField(
                objectValue.ObjectValue,
                "sha",
                JsonValueKind.String);
            JsonValue typeValue = RequireField(
                objectValue.ObjectValue,
                "type",
                JsonValueKind.String);
            if (!string.Equals(typeValue.Text, "commit", StringComparison.Ordinal) ||
                !IsCommitSha(shaValue.Text))
            {
                throw new InvalidDataException("Repository head is not a valid commit.");
            }
            return shaValue.Text;
        }

        private static bool IsCommitSha(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 40)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static string BuildImmutableManifestAddress(
            string commitSha,
            string fileName)
        {
            if (!IsCommitSha(commitSha) ||
                (!string.Equals(
                    fileName,
                    "update-v2.json",
                    StringComparison.Ordinal) &&
                 !string.Equals(
                    fileName,
                    "update-v2.json.sig",
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Immutable manifest path is invalid.");
            }
            return RepositoryRawPrefix + commitSha + "/" + fileName;
        }

        private static int GetManifestRequestTimeout(Stopwatch totalTimer)
        {
            if (totalTimer == null)
            {
                throw new ArgumentNullException("totalTimer");
            }
            long remaining =
                ManifestTotalTimeoutMilliseconds - totalTimer.ElapsedMilliseconds;
            if (remaining < 500)
            {
                throw new WebException(
                    "The update check exceeded its total time budget.",
                    WebExceptionStatus.Timeout);
            }
            return (int)Math.Min(
                ManifestRequestTimeoutMilliseconds,
                remaining);
        }

        private static void WaitForManifestRetry(
            Stopwatch totalTimer,
            int delayMilliseconds,
            Exception previousFailure)
        {
            if (totalTimer == null)
            {
                throw new ArgumentNullException("totalTimer");
            }
            long remaining =
                ManifestTotalTimeoutMilliseconds - totalTimer.ElapsedMilliseconds;
            if (delayMilliseconds <= 0 || remaining <= delayMilliseconds + 500L)
            {
                throw new WebException(
                    "The update check exceeded its total time budget.",
                    previousFailure,
                    WebExceptionStatus.Timeout,
                    null);
            }
            Thread.Sleep(delayMilliseconds);
        }

        private static bool IsTransientManifestFailure(WebException exception)
        {
            if (exception == null)
            {
                return false;
            }
            switch (exception.Status)
            {
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.PipelineFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.Timeout:
                    return true;

                case WebExceptionStatus.ProtocolError:
                    var response = exception.Response as HttpWebResponse;
                    if (response == null)
                    {
                        return false;
                    }
                    int statusCode = (int)response.StatusCode;
                    return statusCode == 408 ||
                           statusCode == 429 ||
                           (statusCode >= 500 && statusCode <= 599);

                default:
                    return false;
            }
        }

        private static void VerifyManifestSignature(byte[] manifestBytes, byte[] signatureBytes)
        {
            if (signatureBytes == null || signatureBytes.Length != Rsa3072SignatureBytes)
            {
                throw new CryptographicException("Manifest signature length is invalid.");
            }
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(UpdateSigningPublicKeyXml);
                if (rsa.KeySize != 3072)
                {
                    throw new CryptographicException("Update signing key size is invalid.");
                }
                string sha256Oid = CryptoConfig.MapNameToOID("SHA256");
                if (string.IsNullOrEmpty(sha256Oid) ||
                    !rsa.VerifyData(manifestBytes, sha256Oid, signatureBytes))
                {
                    throw new CryptographicException("Manifest signature verification failed.");
                }
            }
        }

        private static byte[] DecodeManifestSignature(byte[] signaturePayload)
        {
            if (signaturePayload == null || signaturePayload.Length == 0 ||
                signaturePayload.Length > SignatureMaximumBytes)
            {
                throw new CryptographicException("Manifest signature payload length is invalid.");
            }

            string encoded = new UTF8Encoding(false, true).GetString(signaturePayload).Trim();
            if (encoded.Length != 512)
            {
                throw new CryptographicException("Manifest signature encoding length is invalid.");
            }
            for (int index = 0; index < encoded.Length; index++)
            {
                char value = encoded[index];
                bool valid =
                    (value >= 'A' && value <= 'Z') ||
                    (value >= 'a' && value <= 'z') ||
                    (value >= '0' && value <= '9') ||
                    value == '+' || value == '/' || value == '=';
                if (!valid)
                {
                    throw new CryptographicException("Manifest signature encoding is invalid.");
                }
            }

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new CryptographicException("Manifest signature is not valid base64.", exception);
            }
            if (signature.Length != Rsa3072SignatureBytes)
            {
                throw new CryptographicException("Manifest signature length is invalid.");
            }
            return signature;
        }

        private static JsonValue RequireField(
            Dictionary<string, JsonValue> root,
            string name,
            JsonValueKind expectedKind)
        {
            JsonValue value;
            if (!root.TryGetValue(name, out value) || value == null || value.Kind != expectedKind)
            {
                throw new InvalidDataException("Missing or invalid manifest field: " + name + ".");
            }
            return value;
        }

        private static byte[] DownloadSmallFile(
            string address,
            int maximumBytes,
            int timeoutMilliseconds,
            Stopwatch totalTimer,
            int totalTimeoutMilliseconds)
        {
            if (totalTimer == null)
            {
                throw new ArgumentNullException("totalTimer");
            }
            if (timeoutMilliseconds <= 0 || totalTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            }

            long remaining =
                totalTimeoutMilliseconds - totalTimer.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                throw new WebException(
                    "The update check exceeded its total time budget.",
                    WebExceptionStatus.Timeout);
            }
            int deadlineMilliseconds = (int)Math.Min(
                timeoutMilliseconds,
                Math.Min(remaining, int.MaxValue));
            HttpWebRequest request = CreateRequest(
                address,
                deadlineMilliseconds);
            int deadlineReached = 0;
            using (var deadlineTimer = new Timer(
                state =>
                {
                    Interlocked.Exchange(ref deadlineReached, 1);
                    try
                    {
                        ((HttpWebRequest)state).Abort();
                    }
                    catch
                    {
                    }
                },
                request,
                deadlineMilliseconds,
                Timeout.Infinite))
            {
                try
                {
                    using (HttpWebResponse response =
                        (HttpWebResponse)request.GetResponse())
                    {
                        ValidateResponse(response, maximumBytes, null, address);
                        using (Stream source = response.GetResponseStream())
                        using (var destination = new MemoryStream())
                        {
                            byte[] buffer = new byte[4096];
                            int total = 0;
                            while (true)
                            {
                                int read = source.Read(buffer, 0, buffer.Length);
                                if (Interlocked.CompareExchange(
                                    ref deadlineReached,
                                    0,
                                    0) != 0 ||
                                    totalTimer.ElapsedMilliseconds >=
                                        totalTimeoutMilliseconds)
                                {
                                    throw new WebException(
                                        "The update check exceeded its time budget.",
                                        WebExceptionStatus.Timeout);
                                }
                                if (read <= 0)
                                {
                                    break;
                                }
                                total += read;
                                if (total > maximumBytes)
                                {
                                    throw new InvalidDataException(
                                        "Response exceeds the allowed size.");
                                }
                                destination.Write(buffer, 0, read);
                            }
                            if (total == 0)
                            {
                                throw new InvalidDataException(
                                    "Response is empty.");
                            }
                            return destination.ToArray();
                        }
                    }
                }
                catch (WebException ex)
                {
                    if (Interlocked.CompareExchange(
                        ref deadlineReached,
                        0,
                        0) != 0 ||
                        totalTimer.ElapsedMilliseconds >= totalTimeoutMilliseconds)
                    {
                        throw new WebException(
                            "The update check exceeded its time budget.",
                            ex,
                            WebExceptionStatus.Timeout,
                            null);
                    }
                    throw;
                }
            }
        }

        private async void ContinueButtonClick(object sender, RoutedEventArgs e)
        {
            if (updateOperationRunning)
            {
                return;
            }

            updateOperationRunning = true;
            try
            {
                if (state == UpdateState.Retry && !demoMode &&
                    !await RefreshAvailableUpdateAsync())
                {
                    return;
                }
                await DownloadAndLaunchUpdateAsync();
            }
            finally
            {
                updateOperationRunning = false;
            }
        }

        private async Task<bool> RefreshAvailableUpdateAsync()
        {
            ShowChecking();
            try
            {
                UpdateManifest manifest = await Task.Run(new Func<UpdateManifest>(FetchAndValidateManifest));
                SemanticVersion currentVersion = GetCurrentVersion();
                if (manifest.Version.CompareTo(currentVersion) <= 0)
                {
                    Log("Update retry completed: the mandatory update is no longer required.");
                    availableUpdate = null;
                    HideFlow();
                    return false;
                }

                availableUpdate = manifest;
                Log("Update retry refreshed manifest: " + currentVersion + " -> " + manifest.Version + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log("Update retry check failed: " + DescribeException(ex));
                ShowRetry("Не удалось повторно проверить обновление. Проверьте подключение к интернету и повторите попытку.");
                return false;
            }
        }

        private async Task DownloadAndLaunchUpdateAsync()
        {
            if (availableUpdate == null)
            {
                ShowRetry("Не удалось определить доступную версию. Повторите проверку.");
                return;
            }

            FileStream updateLock = TryAcquireUpdateLock();
            if (updateLock == null)
            {
                ShowRetry("Обновление уже скачивается в другом окне Majestic Boost. Закройте лишнее окно и повторите попытку.");
                return;
            }

            using (updateLock)
            {
                await DownloadAndLaunchUpdateWithLockAsync();
            }
        }

        private async Task DownloadAndLaunchUpdateWithLockAsync()
        {
            ShowDownloading(availableUpdate.Version);
            if (demoMode)
            {
                await RunDemoUpdateProgressAsync(availableUpdate);
                return;
            }

            try
            {
                UpdateManifest update = availableUpdate;
                IProgress<UpdateProgressInfo> progress =
                    new Progress<UpdateProgressInfo>(UpdateProgressDisplay);
                await Task.Run(delegate { DownloadValidateAndLaunch(update, progress); });
                Log("Verified installer was handed to the elevated setup process.");
                allowOwnerClose = true;
                EventHandler closeHandler = RequestApplicationClose;
                if (closeHandler != null)
                {
                    closeHandler(this, EventArgs.Empty);
                }
                else
                {
                    owner.Close();
                }
            }
            catch (Win32Exception ex)
            {
                Log("Installer launch failed: " + DescribeException(ex));
                if (ex.NativeErrorCode == 1223)
                {
                    ShowRetry("Запрос прав администратора был отменён. Для продолжения установите обновление.");
                }
                else
                {
                    ShowRetry("Не удалось запустить установщик. Проверьте доступ к системе и повторите попытку.");
                }
            }
            catch (InvalidDataException ex)
            {
                Log("Downloaded update validation failed: " + DescribeException(ex));
                ShowRetry("Скачанный файл обновления не прошёл проверку безопасности. Повторите попытку; если ошибка сохранится, скачайте актуальный установщик с официального репозитория.");
            }
            catch (WebException ex)
            {
                Log("Update download failed: " + DescribeException(ex));
                ShowRetry("Не удалось скачать обновление. Проверьте подключение к интернету и повторите попытку.");
            }
            catch (Exception ex)
            {
                Log("Update failed: " + DescribeException(ex));
                ShowRetry("Не удалось подготовить обновление. Повторите попытку или скачайте актуальный установщик с официального репозитория.");
            }
        }

        private static FileStream TryAcquireUpdateLock()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost");
                Directory.CreateDirectory(directory);
                return new FileStream(
                    Path.Combine(directory, "update-operation.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private async Task RunDemoUpdateProgressAsync(UpdateManifest update)
        {
            const int step = 2;
            for (int percent = 0; percent <= 100; percent += step)
            {
                long downloaded = update.Size * percent / 100L;
                UpdateProgressDisplay(new UpdateProgressInfo
                {
                    Stage = UpdateProgressStage.Downloading,
                    DownloadedBytes = downloaded,
                    TotalBytes = update.Size
                });
                await Task.Delay(24);
            }

            UpdateProgressDisplay(new UpdateProgressInfo
            {
                Stage = UpdateProgressStage.Verifying,
                DownloadedBytes = update.Size,
                TotalBytes = update.Size
            });
            await Task.Delay(650);

            UpdateProgressDisplay(new UpdateProgressInfo
            {
                Stage = UpdateProgressStage.Launching,
                DownloadedBytes = update.Size,
                TotalBytes = update.Size
            });
            await Task.Delay(500);
            ShowDemoHandoff();
        }

        private static void DownloadValidateAndLaunch(
            UpdateManifest update,
            IProgress<UpdateProgressInfo> progress)
        {
            string directory = CreateUniqueDownloadDirectory();
            string fileName = "MajesticBoost-Setup-" + update.Version + ".exe";
            string installerPath = Path.Combine(directory, fileName);
            bool launched = false;
            try
            {
                using (var downloadStream = new FileStream(
                    installerPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    65536,
                    FileOptions.SequentialScan))
                {
                    DownloadInstallerIntoHeldFile(update, downloadStream, progress);
                }

                // FileVersionInfo opens the executable independently. Keeping the
                // original read/write stream alive with FileShare.Read causes the
                // Windows version API to fail with a sharing violation and return
                // empty metadata. Reopen read-only: FileShare.Read still blocks any
                // writer while hash/version validation and ShellExecute take place.
                using (FileStream verificationStream = OpenInstallerForVerification(installerPath))
                {
                    ReportUpdateProgress(
                        progress,
                        UpdateProgressStage.Verifying,
                        update.Size,
                        update.Size);
                    ValidateHeldInstaller(update, installerPath, verificationStream);

                    ReportUpdateProgress(
                        progress,
                        UpdateProgressStage.Launching,
                        update.Size,
                        update.Size);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = "/updateui",
                        WorkingDirectory = directory,
                        UseShellExecute = true,
                        Verb = "runas",
                        ErrorDialog = false
                    };
                    Process process = Process.Start(startInfo);
                    if (process == null)
                    {
                        throw new InvalidOperationException("The installer process was not created.");
                    }
                    process.Dispose();
                    launched = true;
                }
            }
            finally
            {
                if (!launched)
                {
                    TryDeleteDownload(directory, installerPath);
                }
            }
        }

        private static FileStream OpenInstallerForVerification(string installerPath)
        {
            return new FileStream(
                installerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.SequentialScan);
        }

        private static void DownloadInstallerIntoHeldFile(
            UpdateManifest update,
            FileStream destination,
            IProgress<UpdateProgressInfo> progress)
        {
            HttpWebRequest request = CreateRequest(update.InstallerUrl, DownloadReadTimeoutMilliseconds);
            var stopwatch = Stopwatch.StartNew();
            int lastReportedPercent = -1;
            ReportUpdateProgress(progress, UpdateProgressStage.Downloading, 0, update.Size);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                ValidateResponse(response, InstallerMaximumBytes, update.Size, update.InstallerUrl);
                using (Stream source = response.GetResponseStream())
                {
                    byte[] buffer = new byte[65536];
                    long total = 0;
                    while (true)
                    {
                        if (stopwatch.ElapsedMilliseconds > DownloadTotalTimeoutMilliseconds)
                        {
                            request.Abort();
                            throw new WebException("Installer download timed out.", WebExceptionStatus.Timeout);
                        }
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }
                        total += read;
                        if (total > update.Size || total > InstallerMaximumBytes)
                        {
                            throw new InvalidDataException("Installer exceeds the declared size.");
                        }
                        destination.Write(buffer, 0, read);
                        int percent = (int)(total * 100L / update.Size);
                        if (percent != lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            ReportUpdateProgress(
                                progress,
                                UpdateProgressStage.Downloading,
                                total,
                                update.Size);
                        }
                    }
                    if (total != update.Size)
                    {
                        throw new InvalidDataException("Installer size does not match the manifest.");
                    }
                }
            }
            destination.Flush(true);
            ReportUpdateProgress(
                progress,
                UpdateProgressStage.Downloading,
                update.Size,
                update.Size);
        }

        private static void ReportUpdateProgress(
            IProgress<UpdateProgressInfo> progress,
            UpdateProgressStage stage,
            long downloadedBytes,
            long totalBytes)
        {
            if (progress == null)
            {
                return;
            }
            progress.Report(new UpdateProgressInfo
            {
                Stage = stage,
                DownloadedBytes = downloadedBytes,
                TotalBytes = totalBytes
            });
        }

        private static void ValidateHeldInstaller(
            UpdateManifest update,
            string installerPath,
            FileStream installer)
        {
            if (installer.Length != update.Size)
            {
                throw new InvalidDataException("Installer length changed during verification.");
            }

            ValidatePortableExecutableHeader(installer);
            string actualHash;
            installer.Position = 0;
            using (SHA256 hasher = SHA256.Create())
            {
                actualHash = BytesToHex(hasher.ComputeHash(installer));
            }
            if (!string.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Installer SHA-256 does not match the manifest.");
            }

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(installerPath);
            if (!string.Equals(versionInfo.ProductName, "Majestic Boost", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Installer ProductName is invalid.");
            }

            Version fileVersion;
            if (!Version.TryParse((versionInfo.FileVersion ?? string.Empty).Trim(), out fileVersion) ||
                fileVersion.Major != update.Version.Major ||
                fileVersion.Minor != update.Version.Minor ||
                fileVersion.Build != update.Version.Patch ||
                (fileVersion.Revision != 0 && fileVersion.Revision != -1))
            {
                throw new InvalidDataException("Installer FileVersion does not match the manifest.");
            }

            // Recheck while the non-write-sharing handle is still held. This also
            // catches any unexpected local truncation before ShellExecute.
            if (installer.Length != update.Size)
            {
                throw new InvalidDataException("Installer changed before launch.");
            }
            installer.Position = 0;
        }

        private static void ValidatePortableExecutableHeader(FileStream installer)
        {
            if (installer.Length < 64)
            {
                throw new InvalidDataException("Installer is too small to be a PE file.");
            }
            byte[] header = new byte[64];
            installer.Position = 0;
            ReadExactly(installer, header, 0, header.Length);
            if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new InvalidDataException("Installer is missing the MZ header.");
            }
            int peOffset = header[0x3c] |
                (header[0x3d] << 8) |
                (header[0x3e] << 16) |
                (header[0x3f] << 24);
            if (peOffset < 64 || peOffset > installer.Length - 4 || peOffset > 16777216)
            {
                throw new InvalidDataException("Installer PE offset is invalid.");
            }
            byte[] signature = new byte[4];
            installer.Position = peOffset;
            ReadExactly(installer, signature, 0, signature.Length);
            if (signature[0] != (byte)'P' || signature[1] != (byte)'E' ||
                signature[2] != 0 || signature[3] != 0)
            {
                throw new InvalidDataException("Installer PE signature is invalid.");
            }
        }

        private static void ReadExactly(Stream source, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = source.Read(buffer, offset, count);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
                count -= read;
            }
        }

        private static HttpWebRequest CreateRequest(string address, int timeoutMilliseconds)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            request.CachePolicy = new HttpRequestCachePolicy(
                HttpRequestCacheLevel.NoCacheNoStore);
            request.UserAgent = "MajesticBoost-Updater/" + GetCurrentVersion();
            request.Accept = "application/json, application/octet-stream;q=0.9, */*;q=0.1";
            request.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            request.Headers[HttpRequestHeader.Pragma] = "no-cache";
            request.KeepAlive = false;
            return request;
        }

        private static void ValidateResponse(
            HttpWebResponse response,
            long maximumBytes,
            long? expectedBytes,
            string expectedAddress)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new WebException("Unexpected HTTP status: " + (int)response.StatusCode + ".");
            }
            if (response.ResponseUri == null ||
                !string.Equals(response.ResponseUri.AbsoluteUri, expectedAddress, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unexpected final response URL.");
            }
            if (!string.IsNullOrEmpty(response.ContentEncoding) &&
                !string.Equals(response.ContentEncoding, "identity", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Compressed update responses are not accepted.");
            }
            if (response.ContentLength > maximumBytes)
            {
                throw new InvalidDataException("Response is too large.");
            }
            if (expectedBytes.HasValue && response.ContentLength >= 0 &&
                response.ContentLength != expectedBytes.Value)
            {
                throw new InvalidDataException("HTTP content length does not match the manifest.");
            }
        }

        private static string CreateUniqueDownloadDirectory()
        {
            string root = Path.GetFullPath(Path.GetTempPath());
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string candidate = Path.Combine(
                    root,
                    "MajesticBoost.Update." + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    continue;
                }
                DirectoryInfo created = Directory.CreateDirectory(candidate);
                if ((created.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !IsDirectChild(root, created.FullName))
                {
                    throw new IOException("Unsafe update download directory.");
                }
                return created.FullName;
            }
            throw new IOException("Could not create a unique update directory.");
        }

        private static bool IsDirectChild(string parent, string child)
        {
            string parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string childFull = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
            if (!childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string relative = childFull.Substring(parentFull.Length);
            return relative.Length > 0 &&
                relative.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private static void TryDeleteDownload(string directory, string installerPath)
        {
            try
            {
                string tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (!IsDirectChild(tempRoot, directory))
                {
                    return;
                }
                DirectoryInfo info = new DirectoryInfo(directory);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }
                if (File.Exists(installerPath) &&
                    string.Equals(Path.GetDirectoryName(Path.GetFullPath(installerPath)),
                        Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(installerPath);
                }
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory, false);
                }
            }
            catch
            {
            }
        }

        private void ShowChecking()
        {
            state = UpdateState.Checking;
            Visibility = Visibility.Visible;
            BuildWorkingScreen(
                "ПРОВЕРКА ОБНОВЛЕНИЙ",
                "Проверяем актуальную версию Majestic Boost.",
                "Подождите несколько секунд.");
        }

        private void ShowDownloading(SemanticVersion version)
        {
            state = UpdateState.Downloading;
            Visibility = Visibility.Visible;
            BuildDownloadProgressScreen(version);
        }

        private void BuildWorkingScreen(string title, string subtitle, string status)
        {
            ResetProgressReferences();
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel header = BuildHeader(title, subtitle);
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var track = new Border
            {
                Width = 300,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                ClipToBounds = true
            };
            var indicator = new Border
            {
                Width = 82,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(AccentColor),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TranslateTransform(-82, 0)
            };
            track.Child = indicator;
            body.Children.Add(track);

            TextBlock statusText = MakeText(status, 11, MutedColor, regularFont, FontWeights.Normal);
            statusText.TextAlignment = TextAlignment.Center;
            statusText.Margin = new Thickness(0, 18, 0, 0);
            AutomationProperties.SetName(statusText, status);
            body.Children.Add(statusText);

            var movement = new DoubleAnimation
            {
                From = -82,
                To = 300,
                Duration = TimeSpan.FromMilliseconds(1250),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ((TranslateTransform)indicator.RenderTransform).BeginAnimation(
                TranslateTransform.XProperty,
                movement);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);
            preferredFocusButton = null;
            Focus();
        }

        private void BuildDownloadProgressScreen(SemanticVersion version)
        {
            ResetProgressReferences();
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel header = BuildHeader(
                "ЗАГРУЖАЕМ ОБНОВЛЕНИЕ",
                "Версия " + version + " будет проверена перед запуском.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            progressPercentText = MakeText(
                "0%",
                30,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            progressPercentText.TextAlignment = TextAlignment.Center;
            AutomationProperties.SetName(progressPercentText, "Загружено 0 процентов");
            body.Children.Add(progressPercentText);

            progressStageText = MakeText(
                "Скачиваем установщик...",
                11.5,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            progressStageText.TextAlignment = TextAlignment.Center;
            progressStageText.Margin = new Thickness(0, 7, 0, 16);
            body.Children.Add(progressStageText);

            var track = new Border
            {
                Width = 300,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            progressFill = new Border
            {
                Width = 0,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(AccentColor),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            track.Child = progressFill;
            AutomationProperties.SetName(track, "Прогресс загрузки обновления");
            body.Children.Add(track);

            progressBytesText = MakeText(
                "0 Б из " + FormatBytes(availableUpdate == null ? 0 : availableUpdate.Size),
                10.5,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            progressBytesText.TextAlignment = TextAlignment.Center;
            progressBytesText.Margin = new Thickness(0, 13, 0, 0);
            body.Children.Add(progressBytesText);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);
            preferredFocusButton = null;
            Focus();
        }

        private void UpdateProgressDisplay(UpdateProgressInfo progress)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<UpdateProgressInfo>(UpdateProgressDisplay), progress);
                return;
            }
            if (state != UpdateState.Downloading || progressFill == null ||
                progressPercentText == null || progressStageText == null ||
                progressBytesText == null)
            {
                return;
            }

            long total = progress.TotalBytes <= 0 ? 1 : progress.TotalBytes;
            long downloaded = Math.Max(0, Math.Min(progress.DownloadedBytes, total));
            int percent = (int)(downloaded * 100L / total);
            if (progress.Stage != UpdateProgressStage.Downloading)
            {
                downloaded = total;
                percent = 100;
            }

            progressFill.Width = 300.0 * percent / 100.0;
            progressPercentText.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
            AutomationProperties.SetName(
                progressPercentText,
                "Загружено " + percent.ToString(CultureInfo.InvariantCulture) + " процентов");

            if (progress.Stage == UpdateProgressStage.Verifying)
            {
                progressStageText.Text = "Проверяем целостность и версию...";
                progressBytesText.Text = "Загрузка завершена";
            }
            else if (progress.Stage == UpdateProgressStage.Launching)
            {
                progressStageText.Text = "Открываем установщик...";
                progressBytesText.Text = "Подтвердите запрос Windows";
            }
            else
            {
                progressStageText.Text = "Скачиваем установщик...";
                progressBytesText.Text = FormatBytes(downloaded) + " из " + FormatBytes(total);
            }
            AutomationProperties.SetName(progressStageText, progressStageText.Text);
        }

        private void ResetProgressReferences()
        {
            progressFill = null;
            progressPercentText = null;
            progressStageText = null;
            progressBytesText = null;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture) + " МБ";
            }
            if (bytes >= 1024L)
            {
                return (bytes / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " КБ";
            }
            return bytes.ToString(CultureInfo.CurrentCulture) + " Б";
        }

        private void ShowUpdateRequired(SemanticVersion currentVersion, SemanticVersion newVersion)
        {
            state = UpdateState.Required;
            Visibility = Visibility.Visible;
            ResetProgressReferences();
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            StackPanel header = BuildHeader(
                "ДОСТУПНО ОБНОВЛЕНИЕ",
                "Для продолжения необходимо обновить Majestic Boost.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            var body = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };
            var versionText = MakeText(
                "v. " + currentVersion + "   →   v. " + newVersion,
                16,
                TextColor,
                semiboldFont,
                FontWeights.Bold);
            versionText.TextAlignment = TextAlignment.Center;
            versionText.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(versionText);

            TextBlock description = MakeText(
                "Программа скачает установщик с официального GitHub-репозитория, проверит размер, версию и SHA-256, затем запросит права администратора.",
                10.5,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            description.TextAlignment = TextAlignment.Center;
            description.LineHeight = 14;
            description.Margin = new Thickness(8, 18, 8, 0);
            body.Children.Add(description);

            Grid.SetRow(body, 1);
            cardContent.Children.Add(body);

            StackPanel buttons = BuildButtonRow();
            Button continueButton = MakeActionButton("ПРОДОЛЖИТЬ", true);
            continueButton.Width = 150;
            continueButton.IsDefault = true;
            continueButton.Click += ContinueButtonClick;
            AutomationProperties.SetName(
                continueButton,
                "Скачать, проверить и установить обновление " + newVersion);
            buttons.Children.Add(continueButton);
            preferredFocusButton = continueButton;
            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void ShowRetry(string message)
        {
            state = UpdateState.Retry;
            Visibility = Visibility.Visible;
            ResetProgressReferences();
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            StackPanel header = BuildHeader(
                "НУЖНО ОБНОВЛЕНИЕ",
                "Текущая версия не будет запущена без обязательного обновления.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            TextBlock messageText = MakeText(
                message,
                11.5,
                ErrorColor,
                regularFont,
                FontWeights.Normal);
            messageText.TextAlignment = TextAlignment.Center;
            messageText.VerticalAlignment = VerticalAlignment.Center;
            messageText.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetRow(messageText, 1);
            cardContent.Children.Add(messageText);

            StackPanel buttons = BuildButtonRow();
            Button retryButton = MakeActionButton("ПОВТОРИТЬ", true);
            retryButton.Width = 150;
            retryButton.IsDefault = true;
            retryButton.Click += ContinueButtonClick;
            AutomationProperties.SetName(retryButton, "Повторить загрузку обязательного обновления");
            buttons.Children.Add(retryButton);
            preferredFocusButton = retryButton;
            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private void ShowDemoHandoff()
        {
            state = UpdateState.Retry;
            Visibility = Visibility.Visible;
            ResetProgressReferences();
            cardContent.Children.Clear();
            cardContent.RowDefinitions.Clear();
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

            StackPanel header = BuildHeader(
                "ОБНОВЛЕНИЕ ГОТОВО",
                "Проверенный установщик передаётся отдельному окну обновления.");
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            TextBlock messageText = MakeText(
                "Демо завершено безопасно: сеть, UAC и запуск установщика отключены.",
                11.5,
                AccentColor,
                semiboldFont,
                FontWeights.Bold);
            messageText.TextAlignment = TextAlignment.Center;
            messageText.VerticalAlignment = VerticalAlignment.Center;
            messageText.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetRow(messageText, 1);
            cardContent.Children.Add(messageText);

            StackPanel buttons = BuildButtonRow();
            Button retryButton = MakeActionButton("ПОВТОРИТЬ ДЕМО", true);
            retryButton.Width = 170;
            retryButton.IsDefault = true;
            retryButton.Click += ContinueButtonClick;
            AutomationProperties.SetName(retryButton, "Повторить демонстрацию обновления");
            buttons.Children.Add(retryButton);
            preferredFocusButton = retryButton;
            Grid.SetRow(buttons, 2);
            cardContent.Children.Add(buttons);
            FocusPreferredButton();
        }

        private StackPanel BuildHeader(string title, string subtitle)
        {
            var header = new StackPanel();
            header.Children.Add(MakeText(title, 18, TextColor, semiboldFont, FontWeights.Bold));
            TextBlock subtitleText = MakeText(
                subtitle,
                10.5,
                MutedColor,
                regularFont,
                FontWeights.Normal);
            subtitleText.Margin = new Thickness(0, 5, 0, 0);
            header.Children.Add(subtitleText);
            return header;
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
            Color baseColor = Color.FromRgb(37, 37, 37);
            Color hoverColor = accentOnHover ? AccentColor : Color.FromRgb(49, 49, 49);
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
            UIElement focused = Keyboard.FocusedElement as UIElement;
            if (focused != null)
            {
                FocusNavigationDirection direction =
                    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
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

        private void HideFlow()
        {
            state = UpdateState.Hidden;
            allowOwnerClose = true;
            preferredFocusButton = null;
            ResetProgressReferences();
            Visibility = Visibility.Collapsed;
        }

        private void RaiseUpdateRequired(SemanticVersion currentVersion, SemanticVersion newVersion)
        {
            EventHandler<UpdateRequiredEventArgs> handler = UpdateRequired;
            if (handler != null)
            {
                handler(this, new UpdateRequiredEventArgs(currentVersion.ToString(), newVersion.ToString()));
            }
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

        private static SemanticVersion GetCurrentVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version == null || version.Major < 0 || version.Minor < 0 || version.Build < 0)
            {
                throw new InvalidOperationException("Application assembly version is invalid.");
            }
            return new SemanticVersion
            {
                Major = version.Major,
                Minor = version.Minor,
                Patch = version.Build
            };
        }

        private static bool TryParseSemanticVersion(string text, out SemanticVersion version)
        {
            version = new SemanticVersion();
            if (string.IsNullOrEmpty(text) || text.Length > 32)
            {
                return false;
            }
            string[] parts = text.Split('.');
            int major;
            int minor;
            int patch;
            if (parts.Length != 3 ||
                !TryParseVersionPart(parts[0], out major) ||
                !TryParseVersionPart(parts[1], out minor) ||
                !TryParseVersionPart(parts[2], out patch))
            {
                return false;
            }
            version.Major = major;
            version.Minor = minor;
            version.Patch = patch;
            return true;
        }

        private static bool TryParseVersionPart(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) ||
                (text.Length > 1 && text[0] == '0'))
            {
                return false;
            }
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9')
                {
                    return false;
                }
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseStrictInteger(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9')
                {
                    return false;
                }
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseStrictLong(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] < '0' || text[index] > '9')
                {
                    return false;
                }
            }
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsSha256(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                if (!((value >= '0' && value <= '9') ||
                    (value >= 'a' && value <= 'f') ||
                    (value >= 'A' && value <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static string BytesToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static string DescribeException(Exception exception)
        {
            if (exception == null)
            {
                return "unknown error";
            }
            string message = (exception.Message ?? exception.GetType().Name)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (message.Length > 300)
            {
                message = message.Substring(0, 300);
            }
            return exception.GetType().Name + ": " + message;
        }

        private static void Log(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MajesticBoost");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "update.log");
                string line = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    "  " + message + Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
