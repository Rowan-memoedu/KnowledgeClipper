using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KnowledgeClipper
{
    internal static class StartupDiagnostics
    {
        private const string CanonicalSingleInstanceMutex = "Local\\KnowledgeClipper.SingleInstance";
        private const string LegacyCompatibilitySingleInstanceMutex = "Local\\RemNoteQuickClipper.SingleInstance";

        private static readonly string[] RequiredFiles =
        {
            "KnowledgeClipper.exe",
            "KnowledgeClipper.dll",
            "KnowledgeClipper.deps.json",
            "KnowledgeClipper.runtimeconfig.json",
            "appsettings.json",
            "coreclr.dll",
            "desktop-build-receipt.v1.json"
        };

        internal static int Run(string baseDirectory, TextWriter output)
        {
            var failures = new List<string>();
            var files = new List<object>();
            var root = Path.GetFullPath(baseDirectory ?? "");
            foreach (var name in RequiredFiles)
            {
                var path = Path.Combine(root, name);
                var exists = File.Exists(path);
                files.Add(new { name, exists, bytes = exists ? new FileInfo(path).Length : 0L });
                if (!exists) failures.Add("missing-package-file:" + name);
            }

            var configPath = Path.Combine(root, "appsettings.json");
            var configParsed = false;
            var workDirectoryExpanded = false;
            string workDirectory = null;
            var candidateIntegrationConfigured = File.Exists(Path.Combine(root, "candidate-integration.json"));
            var candidateIntegrationParsed = false;
            var candidateOrchestratorPathsVerified = false;
            CandidateIntegrationConfig candidateIntegration = null;
            try
            {
                var config = AppConfig.Read(configPath);
                configParsed = true;
                workDirectory = config.WorkDirectory;
                workDirectoryExpanded = !string.IsNullOrWhiteSpace(workDirectory) && workDirectory.IndexOf('%') < 0;
                if (!workDirectoryExpanded) failures.Add("work-directory-not-expanded");
            }
            catch (Exception ex)
            {
                failures.Add("config-read-failed:" + ex.GetType().Name);
            }

            if (candidateIntegrationConfigured)
            {
                try
                {
                    candidateIntegration = CandidateIntegrationConfig.Load(root);
                    candidateIntegrationParsed = candidateIntegration != null;
                    candidateOrchestratorPathsVerified = candidateIntegration != null &&
                        File.Exists(candidateIntegration.OrchestratorExecutable) &&
                        File.Exists(candidateIntegration.IpcConfig) &&
                        File.Exists(candidateIntegration.Registry) &&
                        Directory.Exists(candidateIntegration.ReleaseRoot);
                    if (!candidateOrchestratorPathsVerified) failures.Add("candidate-orchestrator-path-verification-failed");
                }
                catch (Exception ex)
                {
                    failures.Add("candidate-integration-read-failed:" + ex.GetType().Name);
                }
            }

            var architecture = RuntimeInformation.ProcessArchitecture.ToString();
            if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                failures.Add("process-is-not-x64");

            var packageReceiptParsed = false;
            var packageFilesVerified = false;
            var packageTreeVerified = false;
            var packageFileCount = 0;
            long packageTotalBytes = 0;
            string packageTreeSha256 = null;
            string buildReceiptSha256 = null;
            try
            {
                VerifyPackageReceipt(root, out packageFileCount, out packageTotalBytes, out packageTreeSha256, out buildReceiptSha256);
                packageReceiptParsed = true;
                packageFilesVerified = true;
                packageTreeVerified = true;
            }
            catch (Exception ex)
            {
                failures.Add("package-receipt-verification-failed:" + ex.Message);
            }

            bool singleInstanceCreated;
            using (var probe = new Mutex(false, CanonicalSingleInstanceMutex, out singleInstanceCreated)) { }
            bool legacyCompatibilityInstanceCreated;
            using (var legacyProbe = new Mutex(false, LegacyCompatibilitySingleInstanceMutex, out legacyCompatibilityInstanceCreated)) { }

            var report = new
            {
                schemaVersion = "desktop-startup-diagnostic.v1",
                productName = "零碎知识剪藏",
                entrypoint = "KnowledgeClipper.exe",
                result = failures.Count == 0 ? "PASS" : "FAIL",
                baseDirectory = root,
                framework = RuntimeInformation.FrameworkDescription,
                processArchitecture = architecture,
                is64BitProcess = Environment.Is64BitProcess,
                selfContainedHost = File.Exists(Path.Combine(root, "coreclr.dll")),
                configPath,
                configParsed,
                workDirectoryExpanded,
                workDirectory,
                packageReceiptParsed,
                packageFilesVerified,
                packageTreeVerified,
                packageFileCount,
                packageTotalBytes,
                packageTreeSha256,
                buildReceiptSha256,
                candidateIntegrationConfigured,
                candidateIntegrationParsed,
                candidateOrchestratorAutoStart = candidateIntegration != null && candidateIntegration.AutoStart,
                candidateOrchestratorPathsVerified,
                authorityCheckpointId = candidateIntegration == null ? null : candidateIntegration.AuthorityCheckpointId,
                authorityTreeSha256 = candidateIntegration == null ? null : candidateIntegration.AuthorityTreeSha256,
                singleInstanceAlreadyRunning = !singleInstanceCreated,
                legacyCompatibilityInstanceDetected = !legacyCompatibilityInstanceCreated,
                productionWorkDirectoryCreated = false,
                namedPipeInvoked = false,
                externalConnections = 0,
                remNoteInvoked = false,
                mineruInvoked = false,
                requiredFiles = files,
                failures
            };
            output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            output.Flush();
            return failures.Count == 0 ? 0 : 4;
        }

        private static void VerifyPackageReceipt(string root, out int fileCount, out long totalBytes, out string treeSha256, out string receiptSha256)
        {
            var receiptPath = Path.Combine(root, "desktop-build-receipt.v1.json");
            var receiptBytes = File.ReadAllBytes(receiptPath);
            receiptSha256 = Hex(SHA256.HashData(receiptBytes));
            using var document = JsonDocument.Parse(receiptBytes);
            var receipt = document.RootElement;
            var files = receipt.GetProperty("files");
            fileCount = files.GetArrayLength();
            totalBytes = 0;
            var entries = new List<PackageEntry>(fileCount);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in files.EnumerateArray())
            {
                var name = item.GetProperty("path").GetString();
                var expectedBytes = item.GetProperty("bytes").GetInt64();
                var expectedHash = item.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains('\\') ||
                    name.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == "..") || !seen.Add(name))
                    throw new InvalidDataException("invalid-file-entry");
                var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("invalid-file-entry");
                if (!File.Exists(path)) throw new InvalidDataException("missing-file:" + name);
                var actualBytes = new FileInfo(path).Length;
                if (actualBytes != expectedBytes) throw new InvalidDataException("byte-mismatch:" + name);
                using var stream = File.OpenRead(path);
                var actualHash = Hex(SHA256.HashData(stream));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("hash-mismatch:" + name);
                entries.Add(new PackageEntry(name, actualBytes, actualHash));
                totalBytes += actualBytes;
            }
            if (receipt.GetProperty("fileCount").GetInt32() != fileCount || receipt.GetProperty("totalBytes").GetInt64() != totalBytes)
                throw new InvalidDataException("aggregate-mismatch");
            entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            var material = new StringBuilder();
            foreach (var entry in entries) material.Append(entry.Name).Append('\n').Append(entry.Sha256).Append('\n').Append(entry.Bytes).Append('\n');
            treeSha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
            if (!string.Equals(treeSha256, receipt.GetProperty("treeSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("tree-mismatch");
        }

        private static string Hex(byte[] bytes) { return Convert.ToHexString(bytes).ToLowerInvariant(); }

        private sealed class PackageEntry
        {
            internal PackageEntry(string name, long bytes, string sha256) { Name = name; Bytes = bytes; Sha256 = sha256; }
            internal string Name { get; }
            internal long Bytes { get; }
            internal string Sha256 { get; }
        }
    }
}
