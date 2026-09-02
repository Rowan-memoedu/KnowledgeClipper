using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Speech.Worker
{
    internal sealed class AttemptStore
    {
        private readonly string jobRoot;
        private readonly string attemptDirectory;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };

        public AttemptStore(string jobRoot, string attemptDirectory)
        {
            this.jobRoot = LongPathFileSystem.GetFullPath(jobRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            this.attemptDirectory = LongPathFileSystem.GetFullPath(attemptDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = this.jobRoot + Path.DirectorySeparatorChar;
            if (!this.attemptDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new SpeechContractException("unsafe_attempt_path", "attempt 必须位于 job 根内");
            if (!LongPathFileSystem.DirectoryExists(this.attemptDirectory)) throw new SpeechContractException("missing_attempt", "attempt 目录不存在");
        }

        public string RequestPath { get { return Path.Combine(attemptDirectory, "request.json"); } }
        public string AttemptId { get { return new DirectoryInfo(attemptDirectory).Name; } }
        public string AttemptDirectory { get { return attemptDirectory; } }
        public string JobRoot { get { return jobRoot; } }

        public void AppendProgress(string state, string message)
        {
            var record = new Dictionary<string, object>
            {
                { "timestampUtc", DateTime.UtcNow.ToString("o") },
                { "state", state },
                { "message", message ?? "" }
            };
            LongPathFileSystem.AppendAllText(Path.Combine(attemptDirectory, "progress.ndjson"), json.Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
        }

        public PublishedArtifact PublishArtifact(string artifactId, string kind, string payloadSourcePath, string payloadName, string mediaType, WorkerRequestEnvelopeV1 envelope, Dictionary<string, object> metadata = null)
        {
            ValidateArtifactId(artifactId);
            if (!LongPathFileSystem.FileExists(payloadSourcePath)) throw new SpeechContractException("missing_output_payload", "待发布 payload 不存在");
            var sourceBytes = LongPathFileSystem.FileLength(payloadSourcePath);
            if (sourceBytes < 1) throw new SpeechContractException("invalid_output_payload", "待发布 payload 为空");
            var sourceHash = SpeechRequestReader.HashFile(payloadSourcePath);
            var outputsDirectory = Path.Combine(attemptDirectory, "outputs");
            var artifactDirectory = Path.Combine(outputsDirectory, artifactId);
            if (LongPathFileSystem.DirectoryExists(artifactDirectory)) throw new SpeechContractException("artifact_already_exists", "输出 artifact 已存在，禁止覆盖");
            LongPathFileSystem.CreateDirectory(outputsDirectory);
            var stagingDirectory = Path.Combine(outputsDirectory, ".tmp-" + artifactId + "-" + Guid.NewGuid().ToString("N"));
            var stagingVersionDirectory = Path.Combine(stagingDirectory, "v0001");
            var stagingPayloadPath = Path.Combine(stagingVersionDirectory, payloadName);
            var stagingManifestPath = Path.Combine(stagingVersionDirectory, "artifact.json");
            var published = false;
            try
            {
                AppendProgress("artifact_staging", artifactId);
                try { LongPathFileSystem.CreateDirectory(stagingVersionDirectory); }
                catch (Exception ex) { throw new IOException("artifact_stage_directory_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                try { LongPathFileSystem.CopyFileNew(payloadSourcePath, stagingPayloadPath); }
                catch (Exception ex) { throw new IOException("artifact_stage_copy_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                try
                {
                    if (LongPathFileSystem.FileLength(stagingPayloadPath) != sourceBytes ||
                        !SpeechRequestReader.HashFile(stagingPayloadPath).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                        throw new SpeechContractException("output_integrity_mismatch", "长路径暂存 payload 完整性校验失败");
                }
                catch (SpeechContractException) { throw; }
                catch (Exception ex) { throw new IOException("artifact_stage_verify_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                var payloadPath = Path.Combine(artifactDirectory, "v0001", payloadName);
                var manifestPath = Path.Combine(artifactDirectory, "v0001", "artifact.json");
            var manifest = new ArtifactManifestV1
            {
                schemaVersion = "1.0.0", artifactId = artifactId, kind = kind, contractVersion = "1.0.0",
                mediaType = mediaType,
                files = new List<HashedFileV1>
                {
                    new HashedFileV1 { path = MakeJobRelative(payloadPath), sha256 = sourceHash, bytes = sourceBytes, mediaType = mediaType }
                },
                createdAt = DateTime.UtcNow.ToString("o"),
                producer = new ArtifactProducerV1 { stageId = envelope.stageId, workerId = envelope.workerId, workerVersion = WorkerCli.WorkerVersion, attemptId = envelope.attemptId },
                inputRefs = envelope.inputRefs ?? new List<ArtifactReferenceV1>(), dataClass = "personal",
                metadata = metadata ?? new Dictionary<string, object>()
            };
                try { WriteJsonOnce(stagingManifestPath, manifest); }
                catch (Exception ex) { throw new IOException("artifact_manifest_stage_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                string manifestHash;
                try { manifestHash = SpeechRequestReader.HashFile(stagingManifestPath); }
                catch (Exception ex) { throw new IOException("artifact_manifest_hash_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                try { LongPathFileSystem.MoveDirectory(stagingDirectory, artifactDirectory); }
                catch (Exception ex) { throw new IOException("artifact_publish_rename_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                published = true;
                try
                {
                    if (LongPathFileSystem.FileLength(payloadPath) != sourceBytes ||
                        !SpeechRequestReader.HashFile(payloadPath).Equals(sourceHash, StringComparison.OrdinalIgnoreCase) ||
                        !SpeechRequestReader.HashFile(manifestPath).Equals(manifestHash, StringComparison.OrdinalIgnoreCase))
                        throw new SpeechContractException("published_output_integrity_mismatch", "原子发布后的长路径 artifact 完整性校验失败");
                }
                catch (SpeechContractException) { throw; }
                catch (Exception ex) { throw new IOException("artifact_publish_verify_failed: " + ex.GetType().Name + ": " + ex.Message, ex); }
                return new PublishedArtifact
                {
                    PayloadPath = MakeJobRelative(payloadPath), PayloadSha256 = sourceHash, PayloadBytes = sourceBytes,
                    Reference = new ArtifactReferenceV1
                    {
                        artifactRef = artifactId, kind = kind, contractVersion = "1.0.0",
                        manifestPath = MakeJobRelative(manifestPath), expectedSha256 = manifestHash
                    }
                };
            }
            catch
            {
                try
                {
                    // Once the staging directory has been atomically renamed to its
                    // immutable artifact id, never erase it. A failure after that point
                    // is represented by a result with no outputs; the unreferenced
                    // bytes remain available as evidence instead of being rewritten.
                    if (!published) LongPathFileSystem.DeleteDirectory(stagingDirectory, true);
                }
                catch { }
                throw;
            }
        }

        public void WriteResultOnce(WorkerResultV1 result)
        {
            WriteJsonOnce(Path.Combine(attemptDirectory, "result.json"), result);
        }

        public string WriteErrorDetailsOnce(string code, string message)
        {
            var directory = Path.Combine(attemptDirectory, "outputs", "error-details", "v0001");
            if (LongPathFileSystem.DirectoryExists(directory)) throw new SpeechContractException("error_details_exists", "error details 已存在");
            LongPathFileSystem.CreateDirectory(directory);
            var path = Path.Combine(directory, "payload.json");
            WriteJsonOnce(path, new Dictionary<string, object> { { "code", code }, { "message", message ?? "" }, { "timestampUtc", DateTime.UtcNow.ToString("o") } });
            return MakeJobRelative(path);
        }

        public string CreateTemporaryPath(string suffix)
        {
            return Path.Combine(attemptDirectory, ".tmp-" + Guid.NewGuid().ToString("N") + suffix);
        }

        private void WriteJsonOnce(string finalPath, object value)
        {
            if (LongPathFileSystem.FileExists(finalPath)) throw new SpeechContractException("immutable_file_exists", "文件已存在，禁止覆盖: " + LongPathFileSystem.GetFileName(finalPath));
            var directory = LongPathFileSystem.GetDirectoryName(finalPath);
            LongPathFileSystem.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("N") + ".json");
            LongPathFileSystem.WriteAllTextNew(temporary, json.Serialize(value), new UTF8Encoding(false));
            try { LongPathFileSystem.MoveFile(temporary, finalPath); }
            catch { try { LongPathFileSystem.DeleteFile(temporary); } catch { } throw; }
        }

        private string MakeJobRelative(string path)
        {
            var root = jobRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = LongPathFileSystem.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new SpeechContractException("unsafe_output_path", "输出路径越出 job 根");
            return full.Substring(root.Length).Replace('\\', '/');
        }

        private static void ValidateArtifactId(string artifactId)
        {
            if (string.IsNullOrWhiteSpace(artifactId) || artifactId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || artifactId.Contains("/") || artifactId.Contains("\\") || artifactId == "." || artifactId == "..")
                throw new SpeechContractException("invalid_artifact_id", "artifactId 无效");
        }
    }
}
