using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KnowledgeClipper
{
    internal sealed class DesktopCredentialSettings
    {
        private const string ProtectedSchemaVersion = "knowledge-clipper-desktop-credentials.dpapi.v1";
        private const string ProtectionScope = "CurrentUser";
        private const string MigrationSchemaVersion = "knowledge-clipper-desktop-credentials-migration.v1";

        public string DeepSeekApiKey { get; set; }
        public string MineruApiToken { get; set; }

        internal string StorageStatus { get; private set; }
        internal static Action<string> MigrationTestHook { get; set; }

        internal static string PathFor(string workDirectory) { return Path.Combine(Path.GetFullPath(workDirectory), "credentials.json"); }
        internal static string MigrationMarkerPathFor(string workDirectory) { return Path.Combine(Path.GetFullPath(workDirectory), "credentials.migration.json"); }

        internal static DesktopCredentialSettings Load(string workDirectory)
        {
            var path = PathFor(workDirectory);
            var backup = path + ".bak";

            DesktopCredentialSettings value;
            if (TryLoadProtected(path, out value)) return WithStatus(value, "protected-current-user");
            if (TryLoadProtected(backup, out value)) return WithStatus(value, "protected-current-user-backup");

            LegacyCredentialDocument legacy;
            string source;
            if (TryLoadLegacy(path, out legacy)) source = path;
            else if (TryLoadLegacy(backup, out legacy)) source = backup;
            else return WithStatus(new DesktopCredentialSettings(), File.Exists(path) || File.Exists(backup) ? "failed-closed" : "empty");

            value = new DesktopCredentialSettings
            {
                DeepSeekApiKey = Normalize(legacy.DeepSeekApiKey),
                MineruApiToken = Normalize(legacy.MineruApiToken)
            };
            try
            {
                MigrateLegacy(workDirectory, source, value);
                DesktopCredentialSettings verified;
                if (!TryLoadProtected(path, out verified) || !Equivalent(value, verified)) throw new CredentialStoreException("credential-migration-verification-failed");
                return WithStatus(verified, "migrated-to-dpapi-current-user");
            }
            catch
            {
                return WithStatus(new DesktopCredentialSettings(), "migration-failed-closed");
            }
        }

        internal void Save(string workDirectory)
        {
            DeepSeekApiKey = Normalize(DeepSeekApiKey);
            MineruApiToken = Normalize(MineruApiToken);
            var path = PathFor(workDirectory);
            var backup = path + ".bak";

            LegacyCredentialDocument legacy;
            string source = null;
            if (TryLoadLegacy(path, out legacy)) source = path;
            else if (TryLoadLegacy(backup, out legacy)) source = backup;
            if (source != null)
            {
                var existing = new DesktopCredentialSettings
                {
                    DeepSeekApiKey = Normalize(legacy.DeepSeekApiKey),
                    MineruApiToken = Normalize(legacy.MineruApiToken)
                };
                MigrateLegacy(workDirectory, source, existing);
            }

            WriteProtectedAtomically(path, this, false);
            DesktopCredentialSettings verified;
            if (!TryLoadProtected(path, out verified) || !Equivalent(this, verified)) throw new CredentialStoreException("credential-save-verification-failed");
            StorageStatus = "protected-current-user";
        }

        internal void ApplyTo(ProcessStartInfo start)
        {
            if (start == null) throw new ArgumentNullException("start");
            if (!string.IsNullOrEmpty(DeepSeekApiKey)) start.Environment["AI_PRODUCER_API_KEY"] = DeepSeekApiKey;
            if (!string.IsNullOrEmpty(MineruApiToken)) start.Environment["MINERU_API_TOKEN"] = MineruApiToken;
        }

        internal DesktopCredentialSettings Copy()
        {
            return new DesktopCredentialSettings { DeepSeekApiKey = DeepSeekApiKey, MineruApiToken = MineruApiToken, StorageStatus = StorageStatus };
        }

        internal ImageOcrCredentialMetadata MineruMetadata()
        {
            var configured = !string.IsNullOrEmpty(Normalize(MineruApiToken));
            var sourceType = StorageStatus == "protected-current-user" ? "desktop-dpapi-current-user" :
                StorageStatus == "protected-current-user-backup" ? "desktop-dpapi-current-user-backup" :
                StorageStatus == "migrated-to-dpapi-current-user" ? "desktop-dpapi-current-user-migrated" :
                configured ? "not-probed" : "not-configured";
            return new ImageOcrCredentialMetadata
            {
                CredentialId = "local-credential://image-ocr/mineru-api-token",
                Configured = configured,
                Verified = configured && sourceType.StartsWith("desktop-dpapi-", StringComparison.Ordinal),
                SourceType = sourceType
            };
        }

        private static void MigrateLegacy(string workDirectory, string source, DesktopCredentialSettings value)
        {
            InvokeMigrationHook("before-protect");
            var path = PathFor(workDirectory);
            WriteProtectedAtomically(path, value, true);
            InvokeMigrationHook("after-protected-commit");

            DesktopCredentialSettings primary;
            DesktopCredentialSettings backup;
            if (!TryLoadProtected(path, out primary) || !Equivalent(value, primary)) throw new CredentialStoreException("credential-migration-primary-verification-failed");
            if (!TryLoadProtected(path + ".bak", out backup) || !Equivalent(value, backup)) throw new CredentialStoreException("credential-migration-backup-verification-failed");

            var marker = new CredentialMigrationMarker
            {
                schemaVersion = MigrationSchemaVersion,
                status = "migrated-to-dpapi-current-user",
                protectionScope = ProtectionScope,
                legacySource = Path.GetFileName(source),
                migratedAt = DateTime.UtcNow.ToString("o"),
                plaintextRetained = false
            };
            WriteTextAtomically(MigrationMarkerPathFor(workDirectory), Json.New().Serialize(marker));
            InvokeMigrationHook("after-marker");
        }

        private static void WriteProtectedAtomically(string path, DesktopCredentialSettings value, bool migration)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var envelope = new ProtectedCredentialEnvelope
            {
                schemaVersion = ProtectedSchemaVersion,
                protectionScope = ProtectionScope,
                deepSeekApiKeyProtected = Protect(value.DeepSeekApiKey),
                mineruApiTokenProtected = Protect(value.MineruApiToken)
            };
            var serialized = Json.New().Serialize(envelope);
            EnsurePlaintextAbsent(serialized, value);

            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, serialized, new UTF8Encoding(false));
                DesktopCredentialSettings temporaryValue;
                if (!TryLoadProtected(temporary, out temporaryValue) || !Equivalent(value, temporaryValue)) throw new CredentialStoreException("credential-temporary-verification-failed");
                if (migration) InvokeMigrationHook("before-commit");

                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);

                DesktopCredentialSettings committed;
                if (!TryLoadProtected(path, out committed) || !Equivalent(value, committed)) throw new CredentialStoreException("credential-commit-verification-failed");

                DesktopCredentialSettings backup;
                if (!TryLoadProtected(path + ".bak", out backup)) ReplaceWithProtectedCopy(path, path + ".bak");
                if (!TryLoadProtected(path + ".bak", out backup)) throw new CredentialStoreException("credential-backup-verification-failed");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void ReplaceWithProtectedCopy(string source, string destination)
        {
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                DesktopCredentialSettings verified;
                if (!TryLoadProtected(temporary, out verified)) throw new CredentialStoreException("credential-backup-copy-verification-failed");
                if (File.Exists(destination)) File.Replace(temporary, destination, null, true);
                else File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void WriteTextAtomically(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, contents, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null, true); else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static bool TryLoadProtected(string path, out DesktopCredentialSettings value)
        {
            value = null;
            if (!File.Exists(path)) return false;
            try
            {
                var envelope = Json.New().Deserialize<ProtectedCredentialEnvelope>(File.ReadAllText(path, Encoding.UTF8));
                if (envelope == null || envelope.schemaVersion != ProtectedSchemaVersion || envelope.protectionScope != ProtectionScope) return false;
                value = new DesktopCredentialSettings
                {
                    DeepSeekApiKey = Normalize(Unprotect(envelope.deepSeekApiKeyProtected)),
                    MineruApiToken = Normalize(Unprotect(envelope.mineruApiTokenProtected))
                };
                return true;
            }
            catch { value = null; return false; }
        }

        private static bool TryLoadLegacy(string path, out LegacyCredentialDocument value)
        {
            value = null;
            if (!File.Exists(path)) return false;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var envelope = Json.New().Deserialize<ProtectedCredentialEnvelope>(text);
                if (envelope != null && envelope.schemaVersion == ProtectedSchemaVersion) return false;
                var legacy = Json.New().Deserialize<LegacyCredentialDocument>(text);
                if (legacy == null || (Normalize(legacy.DeepSeekApiKey) == null && Normalize(legacy.MineruApiToken) == null)) return false;
                value = legacy;
                return true;
            }
            catch { return false; }
        }

        private static string Protect(string value)
        {
            value = Normalize(value);
            if (value == null) return null;
            return Convert.ToBase64String(CurrentUserDataProtection.Protect(Encoding.UTF8.GetBytes(value)));
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return Encoding.UTF8.GetString(CurrentUserDataProtection.Unprotect(Convert.FromBase64String(value)));
        }

        private static void EnsurePlaintextAbsent(string serialized, DesktopCredentialSettings value)
        {
            if ((!string.IsNullOrEmpty(value.DeepSeekApiKey) && serialized.Contains(value.DeepSeekApiKey)) || (!string.IsNullOrEmpty(value.MineruApiToken) && serialized.Contains(value.MineruApiToken)))
                throw new CredentialStoreException("credential-plaintext-serialization-blocked");
        }

        private static DesktopCredentialSettings WithStatus(DesktopCredentialSettings value, string status)
        {
            value = value ?? new DesktopCredentialSettings();
            value.DeepSeekApiKey = Normalize(value.DeepSeekApiKey);
            value.MineruApiToken = Normalize(value.MineruApiToken);
            value.StorageStatus = status;
            return value;
        }

        private static bool Equivalent(DesktopCredentialSettings left, DesktopCredentialSettings right)
        {
            return left != null && right != null && string.Equals(Normalize(left.DeepSeekApiKey), Normalize(right.DeepSeekApiKey), StringComparison.Ordinal) && string.Equals(Normalize(left.MineruApiToken), Normalize(right.MineruApiToken), StringComparison.Ordinal);
        }

        private static void InvokeMigrationHook(string stage)
        {
            var hook = MigrationTestHook;
            if (hook != null) hook(stage);
        }

        private static string Normalize(string value) { return string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

        private sealed class ProtectedCredentialEnvelope
        {
            public string schemaVersion { get; set; }
            public string protectionScope { get; set; }
            public string deepSeekApiKeyProtected { get; set; }
            public string mineruApiTokenProtected { get; set; }
        }

        private sealed class LegacyCredentialDocument
        {
            public string DeepSeekApiKey { get; set; }
            public string MineruApiToken { get; set; }
        }

        private sealed class CredentialMigrationMarker
        {
            public string schemaVersion { get; set; }
            public string status { get; set; }
            public string protectionScope { get; set; }
            public string legacySource { get; set; }
            public string migratedAt { get; set; }
            public bool plaintextRetained { get; set; }
        }
    }

    internal sealed class ImageOcrCredentialMetadata
    {
        internal string CredentialId { get; set; }
        internal bool Configured { get; set; }
        internal bool Verified { get; set; }
        internal string SourceType { get; set; }
        internal string DisplayText { get { return CredentialId + " · configured=" + Configured.ToString().ToLowerInvariant() + " · verified=" + Verified.ToString().ToLowerInvariant() + " · sourceType=" + SourceType + " · process-environment-inherited"; } }
    }

    internal sealed class CredentialStoreException : Exception
    {
        internal CredentialStoreException(string code) : base(code) { }
    }

    internal static class CurrentUserDataProtection
    {
        private const int CryptProtectUiForbidden = 0x1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KnowledgeClipper.DesktopCredentialSettings.dpapi.v1");

        internal static byte[] Protect(byte[] plaintext) { return Transform(plaintext, true); }
        internal static byte[] Unprotect(byte[] ciphertext) { return Transform(ciphertext, false); }

        private static byte[] Transform(byte[] input, bool protect)
        {
            if (input == null) throw new ArgumentNullException("input");
            var inputBlob = default(DataBlob);
            var entropyBlob = default(DataBlob);
            var outputBlob = default(DataBlob);
            try
            {
                inputBlob = Allocate(input);
                entropyBlob = Allocate(Entropy);
                var ok = protect
                    ? CryptProtectData(ref inputBlob, "KnowledgeClipper", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
                if (!ok) throw new CredentialStoreException((protect ? "dpapi-protect-failed-" : "dpapi-unprotect-failed-") + new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode);
                var output = new byte[outputBlob.cbData];
                Marshal.Copy(outputBlob.pbData, output, 0, output.Length);
                return output;
            }
            finally
            {
                FreeAllocated(ref inputBlob);
                FreeAllocated(ref entropyBlob);
                if (outputBlob.pbData != IntPtr.Zero) { LocalFree(outputBlob.pbData); outputBlob.pbData = IntPtr.Zero; outputBlob.cbData = 0; }
            }
        }

        private static DataBlob Allocate(byte[] bytes)
        {
            var blob = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
            return blob;
        }

        private static void FreeAllocated(ref DataBlob blob)
        {
            if (blob.pbData == IntPtr.Zero) return;
            for (var index = 0; index < blob.cbData; index++) Marshal.WriteByte(blob.pbData, index, 0);
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            internal int cbData;
            internal IntPtr pbData;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
