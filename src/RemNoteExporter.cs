using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal sealed class ExportFileRef
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class RemNoteExportInputs
    {
        public ExportFileRef LearningBundle { get; set; }
        public ExportFileRef CardBundle { get; set; }
    }

    internal sealed class RemNoteExportTarget
    {
        public string RootRemId { get; set; }
        public string RootTitle { get; set; }
        public bool DateRouting { get; set; }
    }

    internal sealed class RemNoteExportRoute
    {
        public string Kind { get; set; }
        public string TimeZone { get; set; }
        public string LocalDate { get; set; }
        public string DateFormat { get; set; }
    }

    internal sealed class RemNoteExportManifest
    {
        public string SchemaVersion { get; set; }
        public string TaskPackageId { get; set; }
        public string DocumentTitle { get; set; }
        public string DefaultSink { get; set; }
        public RemNoteExportTarget Target { get; set; }
        public RemNoteExportRoute Route { get; set; }
        public RemNoteExportInputs Inputs { get; set; }
        public RemNoteOriginalMediaSet OriginalMedia { get; set; }
    }

    internal sealed class LearningEntry
    {
        public string ItemId { get; set; }
        public string Text { get; set; }
    }

    internal sealed class LearningBundle
    {
        public string SchemaVersion { get; set; }
        public string BundleId { get; set; }
        public LearningEntry[] Entries { get; set; }
    }

    internal sealed class CardItem
    {
        public string CardId { get; set; }
        public string CardType { get; set; }
        public string Direction { get; set; }
        public string Front { get; set; }
        public string Back { get; set; }
    }

    internal sealed class CardBundle
    {
        public string SchemaVersion { get; set; }
        public string BundleId { get; set; }
        public CardItem[] Cards { get; set; }
    }

    internal sealed class ExportTaskPackage
    {
        private static readonly Regex SafeId = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
        private static readonly string[] ReservedCardDelimiters = { ">>", "==", "<<", "<>", "><" };

        public string RootPath { get; private set; }
        public RemNoteExportManifest Manifest { get; private set; }
        public LearningBundle Learning { get; private set; }
        public CardBundle Cards { get; private set; }
        public RemNoteOriginalMediaSet OriginalMedia { get; private set; }

        internal static ExportTaskPackage CreateForWorker(string jobRoot, RemNoteExportManifest manifest, LearningBundle learning, CardBundle cards)
        {
            if (string.IsNullOrWhiteSpace(jobRoot) || !LongPathFileSystem.DirectoryExists(jobRoot)) throw new DirectoryNotFoundException("job root 不存在");
            if (manifest == null || (manifest.SchemaVersion != "remnote-export-task.v1" && manifest.SchemaVersion != "remnote-export-task.v2" && manifest.SchemaVersion != "remnote-export-task.v3")) throw new InvalidOperationException("不支持的 worker export payload");
            RequireId(manifest.TaskPackageId, "taskPackageId");
            if (manifest.Target == null) throw new InvalidOperationException("target 缺失");
            RequireText(manifest.Target.RootRemId, "target.rootRemId");
            RequireText(manifest.Target.RootTitle, "target.rootTitle");
            ValidateZipSegment(manifest.Target.RootTitle, "target.rootTitle");
            ValidateRoute(manifest);
            ValidateDocumentTitle(manifest.DocumentTitle);
            if (learning != null) ValidateLearning(learning);
            if (cards != null) ValidateCards(cards);
            RemNoteOriginalMediaValidator.Validate(manifest.OriginalMedia);
            if ((learning == null || learning.Entries == null || learning.Entries.Length == 0) &&
                (cards == null || cards.Cards == null || cards.Cards.Length == 0))
                throw new InvalidOperationException("worker request 没有可导出的 LearningBundle 或 CardBundle 项目");
            ValidateWorkflowInputs(manifest, learning, cards);
            return new ExportTaskPackage { RootPath = Path.GetFullPath(jobRoot), Manifest = manifest, Learning = learning, Cards = cards, OriginalMedia = manifest.OriginalMedia };
        }

        public static ExportTaskPackage Load(string packagePath)
        {
            var root = Path.GetFullPath(packagePath);
            if (!LongPathFileSystem.DirectoryExists(root)) throw new DirectoryNotFoundException("任务包目录不存在: " + root);
            var manifestPath = ConfinedPath(root, "manifest.json");
            if (!LongPathFileSystem.FileExists(manifestPath)) throw new FileNotFoundException("任务包缺少 manifest.json", manifestPath);
            RejectReparsePoints(root, manifestPath);
            var manifestText = LongPathFileSystem.ReadAllText(manifestPath, Encoding.UTF8);
            ValidateManifestShape(Json.Dict(Json.New().DeserializeObject(manifestText)));
            var manifest = Json.New().Deserialize<RemNoteExportManifest>(manifestText);
            if (manifest == null || (manifest.SchemaVersion != "remnote-export-task.v1" && manifest.SchemaVersion != "remnote-export-task.v2")) throw new InvalidOperationException("不支持的 manifest schemaVersion");
            RequireId(manifest.TaskPackageId, "taskPackageId");
            if (manifest.Target == null) throw new InvalidOperationException("manifest.target 缺失");
            RequireText(manifest.Target.RootRemId, "target.rootRemId");
            RequireText(manifest.Target.RootTitle, "target.rootTitle");
            ValidateZipSegment(manifest.Target.RootTitle, "target.rootTitle");
            ValidateRoute(manifest);
            ValidateDocumentTitle(manifest.DocumentTitle);
            if (manifest.Inputs == null) throw new InvalidOperationException("manifest.inputs 缺失");

            var result = new ExportTaskPackage { RootPath = root, Manifest = manifest };
            if (manifest.Inputs.LearningBundle != null)
            {
                var path = ValidateFile(root, manifest.Inputs.LearningBundle, "learningBundle");
                var text = LongPathFileSystem.ReadAllText(path, Encoding.UTF8);
                result.Learning = ParseLearningStrict(text);
            }
            if (manifest.Inputs.CardBundle != null)
            {
                var path = ValidateFile(root, manifest.Inputs.CardBundle, "cardBundle");
                var text = LongPathFileSystem.ReadAllText(path, Encoding.UTF8);
                result.Cards = ParseCardsStrict(text);
            }
            if ((result.Learning == null || result.Learning.Entries == null || result.Learning.Entries.Length == 0) &&
                (result.Cards == null || result.Cards.Cards == null || result.Cards.Cards.Length == 0))
                throw new InvalidOperationException("任务包没有可导出的 LearningBundle 或 CardBundle 项目");
            ValidateWorkflowInputs(manifest, result.Learning, result.Cards);
            return result;
        }

        private static void ValidateWorkflowInputs(RemNoteExportManifest manifest, LearningBundle learning, CardBundle cards)
        {
            if (manifest.SchemaVersion != "remnote-export-task.v2" && manifest.SchemaVersion != "remnote-export-task.v3") return;
            if (manifest.Route.Kind == "clip-daily-route" && (learning == null || cards != null))
                throw new InvalidOperationException("clip-daily-route 必须且只能消费 learning.bundle");
            if (manifest.Route.Kind == "card-export" && cards == null)
                throw new InvalidOperationException("card-export 必须消费 card.bundle");
            if (manifest.OriginalMedia != null && manifest.Route.Kind != "card-export")
                throw new StableWorkerContractException("original_media_route_mismatch", "originalMedia 仅允许用于 card-export");
        }

        private static void ValidateRoute(RemNoteExportManifest manifest)
        {
            if (manifest.SchemaVersion == "remnote-export-task.v1")
            {
                if (manifest.Target.DateRouting) throw new InvalidOperationException("RemNote Exporter v1 禁止日期路由");
                if (manifest.Route != null) throw new InvalidOperationException("RemNote Exporter v1 不接受 route");
                return;
            }

            if (manifest.Route == null) throw new InvalidOperationException("RemNote Exporter v2/v3 缺少 route");
            if (manifest.Route.Kind == "clip-daily-route")
            {
                if (!manifest.Target.DateRouting) throw new InvalidOperationException("clip-daily-route 必须启用日期路由");
                if (manifest.Route.TimeZone != "Asia/Shanghai" || manifest.Route.DateFormat != "yyyy-MM-dd")
                    throw new InvalidOperationException("clip-daily-route 仅支持 Asia/Shanghai 与 yyyy-MM-dd");
                DateTime parsed;
                if (!DateTime.TryParseExact(manifest.Route.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ||
                    parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != manifest.Route.LocalDate)
                    throw new InvalidOperationException("clip-daily-route localDate 无效");
                return;
            }
            if (manifest.Route.Kind == "card-export")
            {
                if (manifest.Target.DateRouting) throw new InvalidOperationException("card-export 禁止日期路由");
                if (manifest.Route.TimeZone != null || manifest.Route.LocalDate != null || manifest.Route.DateFormat != null)
                    throw new InvalidOperationException("card-export 不接受日期字段");
                return;
            }
            throw new InvalidOperationException("不支持的 RemNote route: " + manifest.Route.Kind);
        }

        internal static LearningBundle ParseLearningStrict(string text)
        {
            ValidateLearningShape(Json.Dict(Json.New().DeserializeObject(text)));
            var bundle = Json.New().Deserialize<LearningBundle>(text);
            ValidateLearning(bundle);
            return bundle;
        }

        internal static CardBundle ParseCardsStrict(string text)
        {
            ValidateCardShape(Json.Dict(Json.New().DeserializeObject(text)));
            var bundle = Json.New().Deserialize<CardBundle>(text);
            ValidateCards(bundle);
            return bundle;
        }

        public static void RequireId(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || !SafeId.IsMatch(value)) throw new InvalidOperationException(field + " 不是稳定安全 ID");
        }

        public static void ValidateAttemptId(string value)
        {
            RequireId(value, "attemptId");
        }

        private static void ValidateLearning(LearningBundle bundle)
        {
            if (bundle == null || bundle.SchemaVersion != "learning-bundle.v1") throw new InvalidOperationException("不支持的 LearningBundle schemaVersion");
            RequireId(bundle.BundleId, "LearningBundle.bundleId");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in bundle.Entries ?? new LearningEntry[0])
            {
                if (entry == null) throw new InvalidOperationException("LearningBundle 含空 entry");
                RequireId(entry.ItemId, "LearningBundle.itemId");
                if (!seen.Add(entry.ItemId)) throw new InvalidOperationException("LearningBundle itemId 重复: " + entry.ItemId);
                ValidateInline(entry.Text, "LearningBundle.text", true);
            }
        }

        private static void ValidateCards(CardBundle bundle)
        {
            if (bundle == null || bundle.SchemaVersion != "card-bundle.v1") throw new InvalidOperationException("不支持的 CardBundle schemaVersion");
            RequireId(bundle.BundleId, "CardBundle.bundleId");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in bundle.Cards ?? new CardItem[0])
            {
                if (card == null) throw new InvalidOperationException("CardBundle 含空 card");
                RequireId(card.CardId, "CardBundle.cardId");
                if (!seen.Add(card.CardId)) throw new InvalidOperationException("CardBundle cardId 重复: " + card.CardId);
                if (!string.Equals(card.CardType, "basic", StringComparison.Ordinal)) throw new InvalidOperationException("v1 仅支持 basic card");
                if (card.Direction != "forward" && card.Direction != "reverse" && card.Direction != "bidirectional")
                    throw new InvalidOperationException("不支持的卡片方向: " + card.Direction);
                ValidateInline(card.Front, "CardBundle.front", false);
                ValidateInline(card.Back, "CardBundle.back", false);
            }
        }

        private static void ValidateInline(string value, string field, bool rejectCardSyntax)
        {
            RequireText(value, field);
            if (value.Length > 20000) throw new InvalidOperationException(field + " 超过 20000 字符");
            if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new InvalidOperationException(field + " v1 只接受单行文本");
            if (value.Any(ch => char.IsControl(ch))) throw new InvalidOperationException(field + " 含控制字符");
            if (rejectCardSyntax && ReservedCardDelimiters.Any(d => value.IndexOf(d, StringComparison.Ordinal) >= 0))
                throw new InvalidOperationException(field + " 含会意外创建卡片的保留分隔符");
            if (!rejectCardSyntax && ReservedCardDelimiters.Any(d => value.IndexOf(d, StringComparison.Ordinal) >= 0))
                throw new InvalidOperationException(field + " 含无法无损转义的卡片分隔符");
        }

        private static void ValidateDocumentTitle(string value)
        {
            RequireText(value, "documentTitle");
            if (value.Length > 180) throw new InvalidOperationException("documentTitle 过长");
            ValidateZipSegment(value, "documentTitle");
        }

        private static void ValidateZipSegment(string value, string field)
        {
            if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal))
                throw new InvalidOperationException(field + " 不能安全映射为 ZIP 路径段");
        }

        private static void RequireText(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(field + " 缺失或为空");
        }

        private static string ValidateFile(string root, ExportFileRef file, string field)
        {
            if (file == null) return null;
            RequireText(file.Path, field + ".path");
            if (Path.IsPathRooted(file.Path)) throw new InvalidOperationException(field + ".path 必须是任务包内相对路径");
            var path = ConfinedPath(root, file.Path);
            if (!LongPathFileSystem.FileExists(path)) throw new FileNotFoundException(field + " 文件不存在", path);
            RejectReparsePoints(root, path);
            if (string.IsNullOrWhiteSpace(file.Sha256) || !Regex.IsMatch(file.Sha256, "^[A-Fa-f0-9]{64}$"))
                throw new InvalidOperationException(field + ".sha256 无效");
            var actual = Sha256File(path);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(field + " SHA-256 不匹配");
            return path;
        }

        private static void RejectReparsePoints(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrWhiteSpace(current) && current.Length >= rootFull.Length)
            {
                if ((LongPathFileSystem.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("任务包路径包含符号链接或重解析点: " + current);
                if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
            if ((LongPathFileSystem.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("任务包输入文件是符号链接或重解析点: " + path);
        }

        private static void ValidateManifestShape(IDictionary<string, object> value)
        {
            RequireObject(value, "manifest");
            var schemaVersion = Convert.ToString(Get(value, "schemaVersion"), CultureInfo.InvariantCulture);
            if (schemaVersion == "remnote-export-task.v2") RequireOnlyKeys(value, "manifest", "schemaVersion", "taskPackageId", "documentTitle", "defaultSink", "target", "route", "inputs");
            else RequireOnlyKeys(value, "manifest", "schemaVersion", "taskPackageId", "documentTitle", "defaultSink", "target", "inputs");
            var target = Json.Dict(Get(value, "target"));
            RequireObject(target, "manifest.target");
            RequireOnlyKeys(target, "manifest.target", "rootRemId", "rootTitle", "dateRouting");
            if (schemaVersion == "remnote-export-task.v2")
            {
                var route = Json.Dict(Get(value, "route"));
                RequireObject(route, "manifest.route");
                var routeKind = Convert.ToString(Get(route, "kind"), CultureInfo.InvariantCulture);
                if (routeKind == "clip-daily-route") RequireOnlyKeys(route, "manifest.route", "kind", "timeZone", "localDate", "dateFormat");
                else RequireOnlyKeys(route, "manifest.route", "kind");
            }
            var inputs = Json.Dict(Get(value, "inputs"));
            RequireObject(inputs, "manifest.inputs");
            RequireOnlyKeys(inputs, "manifest.inputs", "learningBundle", "cardBundle");
            ValidateFileRefShape(inputs, "learningBundle");
            ValidateFileRefShape(inputs, "cardBundle");
        }

        private static void ValidateFileRefShape(IDictionary<string, object> inputs, string key)
        {
            object raw;
            if (!inputs.TryGetValue(key, out raw) || raw == null) return;
            var value = Json.Dict(raw);
            RequireObject(value, "manifest.inputs." + key);
            RequireOnlyKeys(value, "manifest.inputs." + key, "path", "sha256");
        }

        private static void ValidateLearningShape(IDictionary<string, object> value)
        {
            RequireObject(value, "LearningBundle");
            RequireOnlyKeys(value, "LearningBundle", "schemaVersion", "bundleId", "entries");
            foreach (var raw in Json.Array(Get(value, "entries")))
            {
                var entry = Json.Dict(raw);
                RequireObject(entry, "LearningBundle.entry");
                RequireOnlyKeys(entry, "LearningBundle.entry", "itemId", "text");
            }
        }

        private static void ValidateCardShape(IDictionary<string, object> value)
        {
            RequireObject(value, "CardBundle");
            RequireOnlyKeys(value, "CardBundle", "schemaVersion", "bundleId", "cards");
            foreach (var raw in Json.Array(Get(value, "cards")))
            {
                var card = Json.Dict(raw);
                RequireObject(card, "CardBundle.card");
                RequireOnlyKeys(card, "CardBundle.card", "cardId", "cardType", "direction", "front", "back");
            }
        }

        private static void RequireOnlyKeys(IDictionary<string, object> value, string field, params string[] allowed)
        {
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            var unknown = value.Keys.Where(key => !set.Contains(key)).ToArray();
            if (unknown.Length > 0) throw new InvalidOperationException(field + " 含未知字段: " + string.Join(", ", unknown));
        }

        private static void RequireObject(IDictionary<string, object> value, string field)
        {
            if (value == null) throw new InvalidOperationException(field + " 必须是 JSON object");
        }

        private static object Get(IDictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) ? result : null;
        }

        internal static string ConfinedPath(string root, string relative)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
            if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径逃逸任务包: " + relative);
            return candidate;
        }

        internal static string Sha256File(string path)
        {
            using (var stream = LongPathFileSystem.OpenRead(path))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        internal static string Sha256Text(string text)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text)));
        }

        private static string Hex(byte[] value)
        {
            return string.Concat(value.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    internal static class RemNoteMarkdownRenderer
    {
        public static string Render(ExportTaskPackage package)
        {
            var lines = new List<string>();
            if (package.Learning != null)
                foreach (var entry in package.Learning.Entries ?? new LearningEntry[0]) lines.Add("- " + entry.Text);
            if (package.Cards != null)
                foreach (var card in package.Cards.Cards ?? new CardItem[0]) lines.Add("- " + ExpectedHeadline(card));
            return string.Join("\n", lines) + "\n";
        }

        public static string ExpectedHeadline(CardItem card)
        {
            var delimiter = card.Direction == "reverse" ? " << " : card.Direction == "bidirectional" ? " <> " : " >> ";
            return card.Front + delimiter + card.Back;
        }
    }

    internal static class RemNoteDocumentTitleStrategy
    {
        public static string Resolve(string requestedTitle, string taskId, LearningBundle learning, CardBundle cards)
        {
            if (string.IsNullOrWhiteSpace(requestedTitle) || string.IsNullOrWhiteSpace(taskId)) return requestedTitle;
            var taskSuffix = " · " + taskId;
            if (!requestedTitle.EndsWith(taskSuffix, StringComparison.Ordinal)) return requestedTitle;
            var prefix = requestedTitle.Substring(0, requestedTitle.Length - taskSuffix.Length);
            if (string.IsNullOrWhiteSpace(prefix)) return requestedTitle;

            var identities = new List<string>();
            if (learning != null && learning.Entries != null && learning.Entries.Length > 0)
                identities.Add("learning.bundle:" + learning.BundleId);
            if (cards != null && cards.Cards != null && cards.Cards.Length > 0)
                identities.Add("card.bundle:" + cards.BundleId);
            if (identities.Count == 0) return requestedTitle;

            var canonicalIdentity = string.Join("\n", identities);
            var label = identities.Count == 1
                ? identities[0].Substring(identities[0].IndexOf(':') + 1)
                : "bundle-set-" + ExportTaskPackage.Sha256Text(canonicalIdentity).Substring(0, 24);
            var resolved = prefix + " · " + label;
            if (resolved.Length <= 180) return resolved;
            var compactSuffix = " · bundle-" + ExportTaskPackage.Sha256Text(canonicalIdentity).Substring(0, 24);
            var compactPrefix = prefix.Substring(0, Math.Min(prefix.Length, 180 - compactSuffix.Length));
            return compactPrefix + compactSuffix;
        }
    }

    internal class RemNoteNodeSnapshot
    {
        public string RemId { get; set; }
        public string Title { get; set; }
        public string Headline { get; set; }
        public string RemType { get; set; }
        public string CardDirection { get; set; }
    }

    internal sealed class RemNoteDocumentSnapshot : RemNoteNodeSnapshot
    {
        public string ParentRemId { get; set; }
        public List<RemNoteNodeSnapshot> Children { get; set; }
    }

    internal interface IRemNoteExportGateway
    {
        Task CheckStatusAsync(CancellationToken cancellationToken = default(CancellationToken));
        Task<RemNoteDocumentSnapshot> ReadDocumentAsync(string remId, CancellationToken cancellationToken = default(CancellationToken));
        Task<List<RemNoteDocumentSnapshot>> FindDirectNodesAsync(string parentRemId, string exactTitle, CancellationToken cancellationToken = default(CancellationToken));
        Task<List<RemNoteDocumentSnapshot>> FindDirectDocumentsAsync(string parentRemId, string exactTitle, CancellationToken cancellationToken = default(CancellationToken));
        Task<string> CreateNodeAsync(string parentRemId, string title, string contentFile, bool asDocument, CancellationToken cancellationToken = default(CancellationToken));
        Task<string> CreateDocumentAsync(string parentRemId, string title, string contentFile, CancellationToken cancellationToken = default(CancellationToken));
    }

    internal sealed class CliRemNoteExportGateway : IRemNoteExportGateway, IRemNoteReadOnlyProbeGateway, IRemNoteOriginalMediaGateway
    {
        private readonly AppConfig config;
        public CliRemNoteExportGateway(AppConfig config) { this.config = config; }

        private async Task<IDictionary<string, object>> CallAsync(int timeoutMilliseconds, CancellationToken cancellationToken, params string[] args)
        {
            var all = new List<string> { config.RemnoteCliJsPath, "--mcp-url", config.RemnoteMcpUrl };
            all.AddRange(args);
            var result = await ProcessRunner.RunAsync(config.NodePath, all, Path.GetDirectoryName(config.RemnoteCliJsPath), timeoutMilliseconds, null, cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException("RemNote CLI 失败: " + SafeDetail(result));
            IDictionary<string, object> payload;
            try { payload = Json.Dict(Json.New().DeserializeObject(result.StandardOutput)); }
            catch (Exception ex) { throw new InvalidOperationException("RemNote CLI 返回无效 JSON", ex); }
            if (payload == null) throw new InvalidOperationException("RemNote CLI 返回格式异常");
            object error;
            if (payload.TryGetValue("error", out error) && error != null) throw new InvalidOperationException("RemNote: " + Convert.ToString(error, CultureInfo.InvariantCulture));
            return payload;
        }

        public async Task CheckStatusAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var payload = await CallAsync(30000, cancellationToken, "status");
            object connected, writes;
            if (!payload.TryGetValue("connected", out connected) || !(connected is bool) || !(bool)connected) throw new InvalidOperationException("RemNote Bridge 未连接");
            if (!payload.TryGetValue("acceptWriteOperations", out writes) || !(writes is bool) || !(bool)writes) throw new InvalidOperationException("RemNote Bridge 未允许写入");
        }

        public Task<RemNoteOriginalMediaCapability> GetOriginalMediaCapabilityAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RemNoteOriginalMediaCapability
            {
                Supported = false, ReadCapability = "media.images.v1", WriteCapability = null, ReasonCode = "mcp.media_write_unavailable"
            });
        }

        public Task<string> CreateDocumentWithOriginalMediaAsync(string parentRemId, string title, string contentFile, IReadOnlyList<RemNoteOriginalMediaAsset> media, CancellationToken cancellationToken)
        {
            throw new StableWorkerContractException("destination.original_media_unsupported", "本机 RemNote MCP 仅支持图片读取，不支持可验证的图片上传/附着；未执行任何外部写入");
        }

        public Task<List<RemNoteMediaReadbackSnapshot>> ReadOriginalMediaAsync(string documentRemId, CancellationToken cancellationToken)
        {
            throw new StableWorkerContractException("destination.original_media_unsupported", "本机 RemNote MCP 尚无与写入配对的原图 exact readback 路径");
        }

        public async Task<RemNoteProbeStatusSnapshot> ProbeStatusAsync(int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            var payload = await CallAsync(timeoutMilliseconds, cancellationToken, "status");
            object connected, writes;
            return new RemNoteProbeStatusSnapshot
            {
                Connected = payload.TryGetValue("connected", out connected) && connected is bool && (bool)connected,
                AcceptWriteOperations = payload.TryGetValue("acceptWriteOperations", out writes) && writes is bool && (bool)writes,
                ServerVersion = Json.String(payload, "serverVersion", "version"),
                PluginVersion = Json.String(payload, "pluginVersion")
            };
        }

        public async Task<List<RemNoteDocumentSnapshot>> ProbeSearchAsync(string exactTitle, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            var payload = await CallAsync(timeoutMilliseconds, cancellationToken, "search", exactTitle, "--limit", "50", "--view", "full", "--ancestor-depth", "3");
            return ParseNodes(Json.Array(Get(payload, "results")), null);
        }

        public async Task<List<RemNoteDocumentSnapshot>> ProbeListChildrenAsync(string parentRemId, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            var payload = await CallAsync(timeoutMilliseconds, cancellationToken, "list-children", parentRemId, "--limit", "150", "--view", "full");
            return ParseNodes(Json.Array(Get(payload, "children")), parentRemId);
        }

        public Task<RemNoteDocumentSnapshot> ProbeReadAsync(string remId, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            return ProbeReadCoreAsync(remId, timeoutMilliseconds, cancellationToken);
        }

        private async Task<RemNoteDocumentSnapshot> ProbeReadCoreAsync(string remId, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            var payload = await CallAsync(timeoutMilliseconds, cancellationToken, "read", remId, "--content-mode", "structured", "--depth", "2", "--child-limit", "500", "--view", "full", "--ancestor-depth", "2");
            return ParseDocument(payload);
        }

        public async Task<RemNoteDocumentSnapshot> ReadDocumentAsync(string remId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var payload = await CallAsync(60000, cancellationToken, "read", remId, "--content-mode", "structured", "--depth", "2", "--child-limit", "500", "--view", "full", "--ancestor-depth", "2");
            return ParseDocument(payload);
        }

        public async Task<List<RemNoteDocumentSnapshot>> FindDirectDocumentsAsync(string parentRemId, string exactTitle, CancellationToken cancellationToken = default(CancellationToken))
        {
            return (await FindDirectNodesAsync(parentRemId, exactTitle, cancellationToken)).Where(item => item.RemType == "document").ToList();
        }

        public async Task<List<RemNoteDocumentSnapshot>> FindDirectNodesAsync(string parentRemId, string exactTitle, CancellationToken cancellationToken = default(CancellationToken))
        {
            var result = new List<RemNoteDocumentSnapshot>();
            string cursor = null;
            do
            {
                var args = new List<string> { "list-children", parentRemId, "--limit", "150", "--view", "full" };
                if (!string.IsNullOrWhiteSpace(cursor)) { args.Add("--cursor"); args.Add(cursor); }
                var payload = await CallAsync(60000, cancellationToken, args.ToArray());
                foreach (var raw in Json.Array(Get(payload, "children")))
                {
                    var child = Json.Dict(raw);
                    if (!string.Equals(Json.String(child, "title", "headline"), exactTitle, StringComparison.Ordinal)) continue;
                    result.Add(new RemNoteDocumentSnapshot
                    {
                        RemId = Json.String(child, "remId", "id"),
                        Title = Json.String(child, "title", "headline"),
                        Headline = Json.String(child, "headline"),
                        RemType = Json.String(child, "remType"),
                        ParentRemId = parentRemId,
                        Children = new List<RemNoteNodeSnapshot>()
                    });
                }
                object more;
                var hasMore = payload.TryGetValue("hasMore", out more) && more is bool && (bool)more;
                cursor = hasMore ? Json.String(payload, "nextCursor") : null;
            } while (!string.IsNullOrWhiteSpace(cursor));
            return result;
        }

        public async Task<string> CreateDocumentAsync(string parentRemId, string title, string contentFile, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await CreateNodeAsync(parentRemId, title, contentFile, true, cancellationToken);
        }

        public async Task<string> CreateNodeAsync(string parentRemId, string title, string contentFile, bool asDocument, CancellationToken cancellationToken = default(CancellationToken))
        {
            var args = new List<string> { "create", "--title", title, "--parent-id", parentRemId };
            if (!string.IsNullOrWhiteSpace(contentFile)) { args.Add("--content-file"); args.Add(LongPathFileSystem.ForIo(contentFile)); }
            if (asDocument) args.Add("--as-document");
            var payload = await CallAsync(60000, cancellationToken, args.ToArray());
            var ids = Json.Array(Get(payload, "remIds", "rem_ids"));
            if (ids.Length == 0) throw new InvalidOperationException("RemNote 创建未返回 Rem ID");
            return Convert.ToString(ids[0], CultureInfo.InvariantCulture);
        }

        private static RemNoteDocumentSnapshot ParseDocument(IDictionary<string, object> value)
        {
            var result = new RemNoteDocumentSnapshot
            {
                RemId = Json.String(value, "remId", "id"),
                Title = Json.String(value, "title", "headline"),
                Headline = Json.String(value, "headline"),
                RemType = Json.String(value, "remType"),
                ParentRemId = Json.String(value, "parentRemId", "parentId"),
                Children = new List<RemNoteNodeSnapshot>()
            };
            foreach (var raw in Json.Array(Get(value, "contentStructured")))
            {
                var child = Json.Dict(raw);
                result.Children.Add(new RemNoteNodeSnapshot
                {
                    RemId = Json.String(child, "remId", "id"),
                    Title = Json.String(child, "title"),
                    Headline = Json.String(child, "headline", "title"),
                    RemType = Json.String(child, "remType"),
                    CardDirection = Json.String(child, "cardDirection")
                });
            }
            return result;
        }

        private static List<RemNoteDocumentSnapshot> ParseNodes(object[] values, string parentRemId)
        {
            var result = new List<RemNoteDocumentSnapshot>();
            foreach (var raw in values)
            {
                var value = Json.Dict(raw);
                if (value == null) continue;
                result.Add(new RemNoteDocumentSnapshot
                {
                    RemId = Json.String(value, "remId", "id"), Title = Json.String(value, "title", "headline"),
                    Headline = Json.String(value, "headline", "title"), RemType = Json.String(value, "remType"),
                    ParentRemId = string.IsNullOrWhiteSpace(parentRemId) ? Json.String(value, "parentRemId", "parentId") : parentRemId,
                    Children = new List<RemNoteNodeSnapshot>()
                });
            }
            return result;
        }

        private static object Get(IDictionary<string, object> value, params string[] keys)
        {
            if (value == null) return null;
            foreach (var key in keys) { object result; if (value.TryGetValue(key, out result)) return result; }
            return null;
        }

        private static string SafeDetail(ProcessResult result)
        {
            var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 700 ? text.Substring(0, 700) : text;
        }
    }

    internal sealed class ExportAttempt
    {
        public string DirectoryPath { get; private set; }
        public string ReceiptPath { get { return Path.Combine(DirectoryPath, "receipt.json"); } }

        public static ExportAttempt Create(ExportTaskPackage package, string sink, string attemptId)
        {
            ExportTaskPackage.ValidateAttemptId(attemptId);
            var path = ExportTaskPackage.ConfinedPath(package.RootPath, Path.Combine("stages", "remnote-exporter", sink, attemptId));
            if (LongPathFileSystem.DirectoryExists(path)) throw new IOException("不可变 attempt 已存在: " + path);
            LongPathFileSystem.CreateDirectory(path);
            var attempt = new ExportAttempt { DirectoryPath = path };
            attempt.WriteJsonOnce("plan.json", new Dictionary<string, object>
            {
                { "schemaVersion", "remnote-export-plan.v1" },
                { "taskPackageId", package.Manifest.TaskPackageId },
                { "sink", sink },
                { "attemptId", attemptId },
                { "targetRootRemId", package.Manifest.Target.RootRemId },
                { "targetRootTitle", package.Manifest.Target.RootTitle },
                { "documentTitle", package.Manifest.DocumentTitle },
                { "dateRouting", false }
            });
            return attempt;
        }

        public string WriteTextOnce(string name, string text)
        {
            var path = Path.Combine(DirectoryPath, name);
            using (var stream = LongPathFileSystem.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
            return path;
        }

        public string WriteJsonOnce(string name, object value)
        {
            return WriteTextOnce(name, Json.New().Serialize(value));
        }
    }

    internal sealed class RemNoteExportResult
    {
        public string Status { get; set; }
        public string Sink { get; set; }
        public string AttemptDirectory { get; set; }
        public string ReceiptPath { get; set; }
        public string ArtifactPath { get; set; }
        public string DocumentRemId { get; set; }
    }

    internal sealed class RemNoteExporterEngine
    {
        private readonly IRemNoteExportGateway gateway;
        public RemNoteExporterEngine(IRemNoteExportGateway gateway) { this.gateway = gateway; }

        public async Task<RemNoteExportResult> ExportAsync(ExportTaskPackage package, string sink, string attemptId)
        {
            if (package.Manifest.SchemaVersion == "remnote-export-task.v2")
                throw new InvalidOperationException("remnote-export-task.v2 只允许通过标准 run --request 入口执行");
            sink = string.IsNullOrWhiteSpace(sink) ? (string.IsNullOrWhiteSpace(package.Manifest.DefaultSink) ? "mcp-direct" : package.Manifest.DefaultSink) : sink;
            if (sink != "mcp-direct" && sink != "markdown-zip") throw new InvalidOperationException("不支持的 RemNote sink: " + sink);
            var attempt = ExportAttempt.Create(package, sink, attemptId);
            try
            {
                return sink == "markdown-zip" ? ExportZip(package, attempt) : await ExportDirectAsync(package, attempt);
            }
            catch (Exception ex)
            {
                try
                {
                    attempt.WriteJsonOnce("failure.json", new Dictionary<string, object>
                    {
                        { "status", "failed" }, { "sink", sink }, { "errorType", ex.GetType().Name }, { "message", ex.Message }, { "retryable", IsRetryable(ex) }
                    });
                }
                catch { }
                throw;
            }
        }

        private RemNoteExportResult ExportZip(ExportTaskPackage package, ExportAttempt attempt)
        {
            var markdown = RemNoteMarkdownRenderer.Render(package);
            var markdownPath = attempt.WriteTextOnce("document.md", markdown);
            var zipPath = Path.Combine(attempt.DirectoryPath, "remnote-import.zip");
            using (var file = LongPathFileSystem.Open(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                var entryName = package.Manifest.Target.RootTitle.Trim('/') + "/" + package.Manifest.DocumentTitle + ".md";
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var output = entry.Open())
                using (var input = LongPathFileSystem.OpenRead(markdownPath)) input.CopyTo(output);
            }
            var receipt = new Dictionary<string, object>
            {
                { "status", "prepared" }, { "sink", "markdown-zip" }, { "documentTitle", package.Manifest.DocumentTitle },
                { "artifact", "remnote-import.zip" }, { "sha256", ExportTaskPackage.Sha256File(zipPath) }, { "imported", false }, { "readbackVerified", false }
            };
            attempt.WriteJsonOnce("receipt.json", receipt);
            return new RemNoteExportResult { Status = "prepared", Sink = "markdown-zip", AttemptDirectory = attempt.DirectoryPath, ReceiptPath = attempt.ReceiptPath, ArtifactPath = zipPath };
        }

        private async Task<RemNoteExportResult> ExportDirectAsync(ExportTaskPackage package, ExportAttempt attempt)
        {
            if (gateway == null) throw new InvalidOperationException("mcp-direct 缺少 RemNote gateway");
            var markdown = RemNoteMarkdownRenderer.Render(package);
            var markdownPath = attempt.WriteTextOnce("document.md", markdown);
            var mutexName = "Local\\RemNoteExporter-" + ExportTaskPackage.Sha256Text(package.Manifest.Target.RootRemId).Substring(0, 24);
            using (var mutex = new Mutex(false, mutexName))
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("等待 RemNote Exporter 单写者锁超时");
                try
                {
                    await gateway.CheckStatusAsync();
                    var root = await gateway.ReadDocumentAsync(package.Manifest.Target.RootRemId);
                    if (root == null || root.RemId != package.Manifest.Target.RootRemId || root.Title != package.Manifest.Target.RootTitle || root.RemType != "document")
                        throw new InvalidOperationException("目标根 Rem ID、标题或 document 类型不匹配");

                    var matches = await gateway.FindDirectDocumentsAsync(root.RemId, package.Manifest.DocumentTitle);
                    if (matches.Count > 1) throw new InvalidOperationException("目标根下存在多个同名稳定文档，停止写入");
                    var status = "already_applied";
                    string documentId;
                    if (matches.Count == 1) documentId = matches[0].RemId;
                    else
                    {
                        status = "success";
                        Exception createError = null;
                        documentId = null;
                        try { documentId = await gateway.CreateDocumentAsync(root.RemId, package.Manifest.DocumentTitle, markdownPath); }
                        catch (Exception ex) { createError = ex; }
                        if (createError != null)
                        {
                            matches = await gateway.FindDirectDocumentsAsync(root.RemId, package.Manifest.DocumentTitle);
                            if (matches.Count != 1) throw createError;
                            status = "recovered_after_uncertain_commit";
                            documentId = matches[0].RemId;
                        }
                    }
                    matches = await gateway.FindDirectDocumentsAsync(root.RemId, package.Manifest.DocumentTitle);
                    if (matches.Count != 1 || matches[0].RemId != documentId) throw new InvalidOperationException("写后稳定文档数量或 Rem ID 不一致");
                    var readback = await gateway.ReadDocumentAsync(documentId);
                    ValidateReadback(package, readback);
                    attempt.WriteJsonOnce("readback.json", readback);
                    attempt.WriteJsonOnce("receipt.json", new Dictionary<string, object>
                    {
                        { "status", status }, { "sink", "mcp-direct" }, { "documentRemId", documentId }, { "documentTitle", package.Manifest.DocumentTitle },
                        { "contentSha256", ExportTaskPackage.Sha256Text(markdown) }, { "readbackVerified", true }, { "duplicateCount", 0 }
                    });
                    return new RemNoteExportResult { Status = status, Sink = "mcp-direct", AttemptDirectory = attempt.DirectoryPath, ReceiptPath = attempt.ReceiptPath, DocumentRemId = documentId };
                }
                finally { try { mutex.ReleaseMutex(); } catch { } }
            }
        }

        internal static void ValidateReadback(ExportTaskPackage package, RemNoteDocumentSnapshot snapshot)
        {
            if (snapshot == null) throw new InvalidOperationException("RemNote 回读为空");
            if (snapshot.Title != package.Manifest.DocumentTitle || snapshot.RemType != "document" || snapshot.ParentRemId != package.Manifest.Target.RootRemId)
                throw new InvalidOperationException("RemNote 回读 document 元数据不匹配");
            var expected = new List<Tuple<string, string>>();
            if (package.Learning != null)
                foreach (var entry in package.Learning.Entries ?? new LearningEntry[0]) expected.Add(Tuple.Create(entry.Text, (string)null));
            if (package.Cards != null)
                foreach (var card in package.Cards.Cards ?? new CardItem[0]) expected.Add(Tuple.Create(RemNoteMarkdownRenderer.ExpectedHeadline(card), card.Direction));
            var actual = snapshot.Children ?? new List<RemNoteNodeSnapshot>();
            if (actual.Count != expected.Count) throw new InvalidOperationException("RemNote 回读子项数量不匹配");
            for (var i = 0; i < expected.Count; i++)
            {
                if (actual[i].Headline != expected[i].Item1) throw new InvalidOperationException("RemNote 回读第 " + (i + 1) + " 项内容不匹配");
                if (expected[i].Item2 == null)
                {
                    if (!string.IsNullOrWhiteSpace(actual[i].CardDirection)) throw new InvalidOperationException("普通学习条目被意外创建为卡片");
                }
                else if (actual[i].CardDirection != expected[i].Item2) throw new InvalidOperationException("RemNote 回读第 " + (i + 1) + " 张卡片方向不匹配");
            }
        }

        private static bool IsRetryable(Exception ex)
        {
            return ex is TimeoutException || ex.Message.IndexOf("未连接", StringComparison.OrdinalIgnoreCase) >= 0 || ex.Message.IndexOf("超时", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
