using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal static class RemNoteExporterTests
    {
        private static int failures;

        private static void Check(string name, bool passed, string detail)
        {
            if (passed) Console.WriteLine("PASS " + name);
            else { failures++; Console.WriteLine("FAIL " + name + " " + detail); }
        }

        private static void Equal(string name, string expected, string actual)
        {
            Check(name, expected == actual, "expected=[" + expected + "] actual=[" + actual + "]");
        }

        private static int Main()
        {
            LongPathFileSystem.EnsureConfigured();
            var root = Path.Combine(Path.GetTempPath(), "remnote-exporter-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { Run(root).GetAwaiter().GetResult(); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL unhandled " + ex); }
            finally
            {
                var full = Path.GetFullPath(root);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && LongPathFileSystem.DirectoryExists(full)) LongPathFileSystem.DeleteDirectory(full, true);
            }
            Environment.ExitCode = failures == 0 ? 0 : 1;
            return Environment.ExitCode;
        }

        private static async Task Run(string testRoot)
        {
            var packageRoot = CreatePackage(Path.Combine(testRoot, "package"));
            var package = ExportTaskPackage.Load(packageRoot);
            Equal("render learning and card", "- 来源上下文\n- 2 + 2 等于多少？ >> 4\n", RemNoteMarkdownRenderer.Render(package));

            var zipResult = await new RemNoteExporterEngine(null).ExportAsync(package, "markdown-zip", "zip-attempt-001");
            Equal("zip status", "prepared", zipResult.Status);
            Check("zip artifact exists", File.Exists(zipResult.ArtifactPath), zipResult.ArtifactPath);
            using (var archive = ZipFile.OpenRead(zipResult.ArtifactPath))
            {
                Equal("zip entry", "剪藏/Exporter Fixture · fixture-v1.md", archive.Entries.Single().FullName);
                using (var reader = new StreamReader(archive.Entries.Single().Open(), Encoding.UTF8))
                    Equal("zip content", RemNoteMarkdownRenderer.Render(package), reader.ReadToEnd());
            }

            var gateway = new FakeGateway(package.Manifest.Target.RootRemId, package.Manifest.Target.RootTitle);
            var engine = new RemNoteExporterEngine(gateway);
            var first = await engine.ExportAsync(package, "mcp-direct", "mcp-attempt-001");
            Equal("mcp first status", "success", first.Status);
            var second = await engine.ExportAsync(package, "mcp-direct", "mcp-attempt-002");
            Equal("mcp second status", "already_applied", second.Status);
            Equal("mcp create once", "1", gateway.CreateCount.ToString());
            Equal("mcp stable document", first.DocumentRemId, second.DocumentRemId);

            var conflictPackage = ExportTaskPackage.Load(CreatePackage(Path.Combine(testRoot, "conflict-package"), "3 + 3 等于多少？", "6"));
            var contentConflictRejected = false;
            try { await engine.ExportAsync(conflictPackage, "mcp-direct", "mcp-conflict-001"); }
            catch (InvalidOperationException) { contentConflictRejected = true; }
            Check("same title different content rejected", contentConflictRejected, "same stable title silently accepted different bundle content");

            var uncertainGateway = new FakeGateway(package.Manifest.Target.RootRemId, package.Manifest.Target.RootTitle) { ThrowAfterCreate = true };
            var recovered = await new RemNoteExporterEngine(uncertainGateway).ExportAsync(package, "mcp-direct", "mcp-uncertain-001");
            Equal("uncertain commit reconciled", "recovered_after_uncertain_commit", recovered.Status);
            Equal("uncertain commit create once", "1", uncertainGateway.CreateCount.ToString());

            var immutableRejected = false;
            try { await engine.ExportAsync(package, "mcp-direct", "mcp-attempt-002"); }
            catch (IOException) { immutableRejected = true; }
            Check("attempt is immutable", immutableRejected, "existing attempt was reused");

            var badPackage = CreatePackage(Path.Combine(testRoot, "bad-date-package"));
            var manifestPath = Path.Combine(badPackage, "manifest.json");
            var manifest = Json.Dict(Json.New().DeserializeObject(File.ReadAllText(manifestPath, Encoding.UTF8)));
            Json.Dict(manifest["target"])["dateRouting"] = true;
            File.WriteAllText(manifestPath, Json.New().Serialize(manifest), new UTF8Encoding(false));
            var dateRejected = false;
            try { ExportTaskPackage.Load(badPackage); }
            catch (InvalidOperationException ex) { dateRejected = ex.Message.IndexOf("禁止日期路由", StringComparison.Ordinal) >= 0; }
            Check("date routing rejected", dateRejected, "dateRouting=true was accepted");

            var unknownCardFieldRejected = false;
            try { ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-v1\",\"cards\":[{\"cardId\":\"card-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"Q\",\"back\":\"A\",\"extra\":true}]}"); }
            catch (InvalidOperationException ex) { unknownCardFieldRejected = ex.Message.IndexOf("未知字段", StringComparison.Ordinal) >= 0; }
            Check("strict card unknown field rejected", unknownCardFieldRejected, "unknown card field was ignored");

            var unknownLearningFieldRejected = false;
            try { ExportTaskPackage.ParseLearningStrict("{\"schemaVersion\":\"learning-bundle.v1\",\"bundleId\":\"learning-v1\",\"entries\":[{\"itemId\":\"item-1\",\"text\":\"text\",\"extra\":true}]}"); }
            catch (InvalidOperationException ex) { unknownLearningFieldRejected = ex.Message.IndexOf("未知字段", StringComparison.Ordinal) >= 0; }
            Check("strict learning unknown field rejected", unknownLearningFieldRejected, "unknown learning field was ignored");

            var duplicateCardRejected = false;
            try { ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-v1\",\"cards\":[{\"cardId\":\"card-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"Q1\",\"back\":\"A1\"},{\"cardId\":\"card-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"Q2\",\"back\":\"A2\"}]}"); }
            catch (InvalidOperationException ex) { duplicateCardRejected = ex.Message.IndexOf("重复", StringComparison.Ordinal) >= 0; }
            Check("duplicate card id rejected", duplicateCardRejected, "duplicate cardId was accepted");

            var reservedDelimiterRejected = false;
            try { ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-v1\",\"cards\":[{\"cardId\":\"card-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"Q >> accidental\",\"back\":\"A\"}]}"); }
            catch (InvalidOperationException ex) { reservedDelimiterRejected = ex.Message.IndexOf("分隔符", StringComparison.Ordinal) >= 0; }
            Check("reserved card delimiter rejected", reservedDelimiterRejected, "reserved delimiter was accepted");

            var whitespaceCard = ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-v1\",\"cards\":[{\"cardId\":\"card-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"  exact front  \",\"back\":\"  exact back  \"}]}");
            Equal("inline whitespace preserved", "  exact front  ", whitespaceCard.Cards[0].Front);

            var languageBoth = ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"language-both-v1\",\"cards\":[{\"cardId\":\"language-context-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"The plan is tentative.\",\"back\":\"tentative /ˈtentətɪv/：暂定的。\"},{\"cardId\":\"language-writing-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"The plan is （/ˈtentətɪv/；暂定的）.\",\"back\":\"tentative\"}]}");
            Check("language both preserves two independent cards", languageBoth.Cards.Length == 2, "projected both cards were merged or dropped");
            Equal("language both preserves input order", "language-context-1", languageBoth.Cards[0].CardId);
            Equal("language writing front is verbatim", "The plan is （/ˈtentətɪv/；暂定的）.", languageBoth.Cards[1].Front);
            Check("language writing front omits target term", languageBoth.Cards[1].Front.IndexOf("tentative", StringComparison.Ordinal) < 0, "projected writing front retained target_term");
            Equal("language writing back preserves target term", "tentative", languageBoth.Cards[1].Back);
            var languageLines = string.Join("", languageBoth.Cards.Select(card => "- " + RemNoteMarkdownRenderer.ExpectedHeadline(card) + "\n"));
            Equal("language both renders two ordered card lines", "- The plan is tentative. >> tentative /ˈtentətɪv/：暂定的。\n- The plan is （/ˈtentətɪv/；暂定的）. >> tentative\n", languageLines);

            var languageChoiceLeakRejected = false;
            try { ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"language-leak-v1\",\"cards\":[{\"cardId\":\"language-writing-1\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"sentence（reading；meaning）\",\"back\":\"term\",\"template\":\"language_writing\",\"choice\":\"both\",\"categoryId\":\"language_en\",\"targetTerm\":\"term\"}]}"); }
            catch (InvalidOperationException ex) { languageChoiceLeakRejected = ex.Message.IndexOf("未知字段", StringComparison.Ordinal) >= 0; }
            Check("language classification and choice leakage rejected", languageChoiceLeakRejected, "Exporter accepted upstream template/category/choice fields");

            var stableTitleA = RemNoteDocumentTitleStrategy.Resolve("剪藏 · task-a", "task-a", null, whitespaceCard);
            var stableTitleB = RemNoteDocumentTitleStrategy.Resolve("剪藏 · task-b", "task-b", null, whitespaceCard);
            Equal("same bundle stable across task ids", stableTitleA, stableTitleB);
            Equal("bundle id replaces transient task id", "剪藏 · cards-v1", stableTitleA);
            var distinctCard = ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-v2\",\"cards\":[{\"cardId\":\"card-2\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"Q\",\"back\":\"A\"}]}");
            Check("different bundle gets different stable title", stableTitleA != RemNoteDocumentTitleStrategy.Resolve("剪藏 · task-c", "task-c", null, distinctCard), "distinct bundles collided");
            Equal("explicit stable title preserved", "用户指定标题", RemNoteDocumentTitleStrategy.Resolve("用户指定标题", "task-a", null, whitespaceCard));
            var learningForSet = ExportTaskPackage.ParseLearningStrict("{\"schemaVersion\":\"learning-bundle.v1\",\"bundleId\":\"learning-v1\",\"entries\":[{\"itemId\":\"item-1\",\"text\":\"context\"}]}");
            var bundleSetA = RemNoteDocumentTitleStrategy.Resolve("剪藏 · task-a", "task-a", learningForSet, whitespaceCard);
            var bundleSetB = RemNoteDocumentTitleStrategy.Resolve("剪藏 · task-b", "task-b", learningForSet, whitespaceCard);
            Equal("bundle set title deterministic", bundleSetA, bundleSetB);
            Check("bundle set title is compact", bundleSetA.StartsWith("剪藏 · bundle-set-", StringComparison.Ordinal) && bundleSetA.Length <= 180, bundleSetA);
            var longPrefix = new string('长', 175);
            var compactTitle = RemNoteDocumentTitleStrategy.Resolve(longPrefix + " · task-a", "task-a", null, whitespaceCard);
            Check("long generated title remains valid", compactTitle.Length == 180 && compactTitle.IndexOf(" · bundle-", StringComparison.Ordinal) >= 0, compactTitle);

            await RunV2RouteAndSettingsTests(testRoot);
            await RunReadOnlyProbeTests(testRoot);
            await RunPublicProbeV2Tests(testRoot);
            await RunTargetDiscoveryTests(testRoot);
            await RunOriginalMediaV2Tests(testRoot);

            var longAtomicRoot = Path.Combine(testRoot, "atomic-path");
            var longAtomicDirectory = longAtomicRoot;
            while (longAtomicDirectory.Length < 208)
                longAtomicDirectory = Path.Combine(longAtomicDirectory, "segment-123456789");
            var longAtomicPath = Path.Combine(longAtomicDirectory, "artifact.json");
            StableWorkerAdapter.AtomicWriteBytesOnce(longAtomicPath, Encoding.UTF8.GetBytes("atomic"));
            Check("atomic write supports valid near-MAX_PATH output", File.Exists(longAtomicPath) && File.ReadAllText(longAtomicPath, Encoding.UTF8) == "atomic", longAtomicPath);
            Check("atomic write leaves no sidecar", !File.Exists(longAtomicPath + ".tmp"), longAtomicPath + ".tmp");

            var extendedDirectory = Path.Combine(testRoot, "extended-path");
            while (Path.Combine(extendedDirectory, "artifact.json").Length <= 300)
                extendedDirectory = Path.Combine(extendedDirectory, "segment-123456789");
            var extendedPath = Path.Combine(extendedDirectory, "artifact.json");
            StableWorkerAdapter.AtomicWriteBytesOnce(extendedPath, Encoding.UTF8.GetBytes("extended-path"));
            Check("long path regression exceeds 260 characters", extendedPath.Length > 260, extendedPath.Length.ToString(CultureInfo.InvariantCulture));
            Check("atomic write supports true extended path", LongPathFileSystem.FileExists(extendedPath) && LongPathFileSystem.ReadAllText(extendedPath, Encoding.UTF8) == "extended-path", extendedPath);
            Check("true extended path SHA readable", ExportTaskPackage.Sha256File(extendedPath) == ExportTaskPackage.Sha256Text("extended-path"), extendedPath);
            Check("true extended path leaves no sidecar", !LongPathFileSystem.FileExists(extendedPath + ".tmp"), extendedPath + ".tmp");
        }

        private static string CreatePackage(string root, string front = "2 + 2 等于多少？", string back = "4")
        {
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            var learningPath = Path.Combine(root, "inputs", "learning-bundle.v1.json");
            var cardPath = Path.Combine(root, "inputs", "card-bundle.v1.json");
            File.WriteAllText(learningPath, Json.New().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", "learning-bundle.v1" },
                { "bundleId", "fixture-learning-v1" },
                { "entries", new object[] { new Dictionary<string, object> { { "itemId", "context-v1" }, { "text", "来源上下文" } } } }
            }), new UTF8Encoding(false));
            File.WriteAllText(cardPath, Json.New().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", "card-bundle.v1" },
                { "bundleId", "fixture-card-v1" },
                { "cards", new object[] { new Dictionary<string, object> { { "cardId", "card-v1" }, { "cardType", "basic" }, { "direction", "forward" }, { "front", front }, { "back", back } } } }
            }), new UTF8Encoding(false));
            var manifest = new Dictionary<string, object>
            {
                { "schemaVersion", "remnote-export-task.v1" },
                { "taskPackageId", "fixture-task-v1" },
                { "documentTitle", "Exporter Fixture · fixture-v1" },
                { "defaultSink", "mcp-direct" },
                { "target", new Dictionary<string, object> { { "rootRemId", "root-rem-id" }, { "rootTitle", "剪藏" }, { "dateRouting", false } } },
                { "inputs", new Dictionary<string, object>
                {
                    { "learningBundle", new Dictionary<string, object> { { "path", "inputs/learning-bundle.v1.json" }, { "sha256", ExportTaskPackage.Sha256File(learningPath) } } },
                    { "cardBundle", new Dictionary<string, object> { { "path", "inputs/card-bundle.v1.json" }, { "sha256", ExportTaskPackage.Sha256File(cardPath) } } }
                } }
            };
            File.WriteAllText(Path.Combine(root, "manifest.json"), Json.New().Serialize(manifest), new UTF8Encoding(false));
            return root;
        }

        private static async Task RunV2RouteAndSettingsTests(string testRoot)
        {
            var legacyV2 = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-legacy-reject"), "2026-08-28", "摘录 · legacy-reject", "不能绕过标准入口");
            var legacyRejected = false;
            try { await new RemNoteExporterEngine(new FakeGateway("root-rem-id", "剪藏")).ExportAsync(legacyV2, "mcp-direct", "legacy-v2-attempt"); }
            catch (InvalidOperationException ex) { legacyRejected = ex.Message.Contains("run --request"); }
            Check("clip v2 legacy entrypoint rejected", legacyRejected, "v2 route bypassed the stable worker entrypoint");

            var settings = new Dictionary<string, object>
            {
                { "schemaVersion", "remnote-destination-settings.v1" }, { "enabled", true },
                { "target", new Dictionary<string, object> { { "rootRemId", "root-rem-id" }, { "rootTitle", "剪藏" } } },
                { "advanced", new Dictionary<string, object>
                    {
                        { "sink", "mcp-direct" }, { "clipRouting", "clip-daily-route" }, { "cardRouting", "card-export" },
                        { "timeZone", "Asia/Shanghai" }, { "dateFormat", "yyyy-MM-dd" }
                    }
                }
            };
            var adapted = RemNoteSettingsAdapter.Adapt(settings);
            Equal("settings snapshot schema", "remnote-destination-settings-snapshot.v1", Convert.ToString(adapted["schemaVersion"], CultureInfo.InvariantCulture));
            Check("settings has no RemNote secret", ((object[])adapted["secretReferences"]).Length == 0, "secretReferences was not empty");
            var descriptor = RemNoteSettingsAdapter.Describe(new AppConfig { NodePath = "node.exe", RemnoteCliJsPath = "cli.js", RemnoteMcpUrl = "http://127.0.0.1:43101/mcp" });
            Equal("settings descriptor destination", "remnote", Convert.ToString(descriptor["destinationType"], CultureInfo.InvariantCulture));
            Check("settings descriptor groups", ((object[])descriptor["groups"]).Length == 3, "settings groups were incomplete");
            var secretRejected = false;
            var settingsWithSecret = new Dictionary<string, object>(settings) { { "secret", "must-not-exist" } };
            try { RemNoteSettingsAdapter.Adapt(settingsWithSecret); }
            catch (StableWorkerContractException ex) { secretRejected = ex.Code == "unknown_field"; }
            Check("settings secret field rejected", secretRejected, "secret field was accepted");

            var gateway = new FakeGateway("root-rem-id", "剪藏");
            var firstPackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-first"), "2026-08-28", "摘录 · fixture-v2", "第一条摘录");
            var firstRequest = CreateRequest(firstPackage.RootPath, "clip-v2-first", "run-1", "attempt-1", 'a');
            var first = await RunStable(gateway, firstRequest, firstPackage);
            Equal("clip daily first status", "completed", first.Status);
            Equal("clip daily first route", "clip-daily-route", Convert.ToString(first.Payload["route"], CultureInfo.InvariantCulture));
            Equal("clip daily first outcome", "created", Convert.ToString(first.Payload["outcome"], CultureInfo.InvariantCulture));
            Check("clip daily date created once", gateway.CountDirect("root-rem-id", "2026-08-28") == 1, gateway.RenderTree());
            var dateId = (await gateway.FindDirectNodesAsync("root-rem-id", "2026-08-28"))[0].RemId;
            Check("clip daily clip under date", gateway.CountDirect(dateId, "摘录 · fixture-v2") == 1, gateway.RenderTree());
            var firstReceipt = ReadReceipt(firstRequest);
            Equal("clip receipt timezone", "Asia/Shanghai", Convert.ToString(firstReceipt["timeZone"], CultureInfo.InvariantCulture));
            Equal("clip receipt date", "2026-08-28", Convert.ToString(firstReceipt["localDate"], CultureInfo.InvariantCulture));
            Equal("clip receipt date outcome", "created", Convert.ToString(firstReceipt["dateOutcome"], CultureInfo.InvariantCulture));
            Equal("clip receipt root reused", "reused", Convert.ToString(firstReceipt["rootOutcome"], CultureInfo.InvariantCulture));
            Check("clip receipt exact IDs", Convert.ToString(firstReceipt["rootRemId"], CultureInfo.InvariantCulture) == "root-rem-id" && Convert.ToString(firstReceipt["dateRemId"], CultureInfo.InvariantCulture) == dateId && !string.IsNullOrWhiteSpace(Convert.ToString(firstReceipt["clipRemId"], CultureInfo.InvariantCulture)), "receipt IDs missing");

            var replayRequest = CreateRequest(firstPackage.RootPath, "clip-v2-first", "run-2", "attempt-2", 'a');
            var replay = await RunStable(gateway, replayRequest, firstPackage);
            Equal("clip daily replay outcome", "already_applied", Convert.ToString(replay.Payload["outcome"], CultureInfo.InvariantCulture));
            Equal("clip daily replay date reused", "reused", Convert.ToString(ReadReceipt(replayRequest)["dateOutcome"], CultureInfo.InvariantCulture));
            Equal("clip daily replay creates no duplicate", "2", gateway.CreateCount.ToString(CultureInfo.InvariantCulture));

            var existingDateGateway = new FakeGateway("root-rem-id", "剪藏");
            var existingDateId = existingDateGateway.SeedNode("root-rem-id", "2026-08-28");
            var existingPackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-existing"), "2026-08-28", "摘录 · existing-date", "已有日期下的新摘录");
            var existingRequest = CreateRequest(existingPackage.RootPath, "clip-v2-existing", "run-1", "attempt-1", 'b');
            await RunStable(existingDateGateway, existingRequest, existingPackage);
            var existingReceipt = ReadReceipt(existingRequest);
            Equal("clip daily existing date reused", "reused", Convert.ToString(existingReceipt["dateOutcome"], CultureInfo.InvariantCulture));
            Equal("clip daily existing date ID stable", existingDateId, Convert.ToString(existingReceipt["dateRemId"], CultureInfo.InvariantCulture));

            var duplicateGateway = new FakeGateway("root-rem-id", "剪藏");
            duplicateGateway.SeedNode("root-rem-id", "2026-08-28");
            duplicateGateway.SeedNode("root-rem-id", "2026-08-28");
            var duplicatePackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-duplicate"), "2026-08-28", "摘录 · duplicate-date", "不会写入");
            var duplicateRequest = CreateRequest(duplicatePackage.RootPath, "clip-v2-duplicate", "run-1", "attempt-1", 'c');
            var duplicateRejected = false;
            try { await RunStable(duplicateGateway, duplicateRequest, duplicatePackage); }
            catch (StableWorkerContractException ex) { duplicateRejected = ex.Code == "date_node_collision"; }
            Check("clip daily duplicate date rejected", duplicateRejected, "duplicate date was accepted");

            var nextDayPackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-next-day"), "2026-08-29", "摘录 · next-day", "跨日摘录");
            var nextDayRequest = CreateRequest(nextDayPackage.RootPath, "clip-v2-next-day", "run-1", "attempt-1", 'd');
            await RunStable(gateway, nextDayRequest, nextDayPackage);
            Check("clip daily cross-day separates nodes", gateway.CountDirect("root-rem-id", "2026-08-28") == 1 && gateway.CountDirect("root-rem-id", "2026-08-29") == 1, gateway.RenderTree());

            var concurrentGateway = new FakeGateway("root-rem-id", "剪藏");
            var concurrentPackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-concurrent"), "2026-08-28", "摘录 · concurrent", "并发摘录");
            var concurrentA = CreateRequest(concurrentPackage.RootPath, "clip-v2-concurrent", "run-a", "attempt-a", 'e');
            var concurrentB = CreateRequest(concurrentPackage.RootPath, "clip-v2-concurrent", "run-b", "attempt-b", 'e');
            var concurrentResults = await Task.WhenAll(RunStable(concurrentGateway, concurrentA, concurrentPackage), RunStable(concurrentGateway, concurrentB, concurrentPackage));
            Check("clip daily concurrent outcomes", concurrentResults.Count(item => Convert.ToString(item.Payload["outcome"], CultureInfo.InvariantCulture) == "created") == 1 && concurrentResults.Count(item => Convert.ToString(item.Payload["outcome"], CultureInfo.InvariantCulture) == "already_applied") == 1, "unexpected concurrent outcomes");
            Equal("clip daily concurrent creates once", "2", concurrentGateway.CreateCount.ToString(CultureInfo.InvariantCulture));

            var cardGateway = new FakeGateway("root-rem-id", "剪藏");
            var cardPackage = CreateCardV2Package(Path.Combine(testRoot, "jobs", "card-v2"));
            var cardRequest = CreateRequest(cardPackage.RootPath, "card-v2", "run-1", "attempt-1", 'f');
            var cardResult = await RunStable(cardGateway, cardRequest, cardPackage);
            Equal("card export route", "card-export", Convert.ToString(cardResult.Payload["route"], CultureInfo.InvariantCulture));
            var cardReceipt = ReadReceipt(cardRequest);
            Check("card export has no date routing", Convert.ToString(cardReceipt["route"], CultureInfo.InvariantCulture) == "card-export" && !(bool)cardReceipt["dateRouting"] && !cardReceipt.ContainsKey("dateRemId"), "card export leaked date routing");

            var recoveredGateway = new FakeGateway("root-rem-id", "剪藏") { ThrowAfterCreate = true };
            recoveredGateway.SeedNode("root-rem-id", "2026-08-28");
            var recoveredPackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-recovered"), "2026-08-28", "摘录 · recovered", "不确定提交后回读恢复");
            var recoveredRequest = CreateRequest(recoveredPackage.RootPath, "clip-v2-recovered", "run-1", "attempt-1", '9');
            var recovered = await RunStable(recoveredGateway, recoveredRequest, recoveredPackage);
            Equal("clip daily uncertain commit reconciled", "recovered_after_uncertain_commit", Convert.ToString(recovered.Payload["outcome"], CultureInfo.InvariantCulture));
            Equal("clip daily uncertain commit create once", "1", recoveredGateway.CreateCount.ToString(CultureInfo.InvariantCulture));
            Check("clip daily recovered receipt readback", (bool)ReadReceipt(recoveredRequest)["readbackVerified"], "recovered receipt was not read back");

            var unknownGateway = new FakeGateway("root-rem-id", "剪藏") { ThrowAfterCreate = true, HideCreatedDuringReconcile = true };
            unknownGateway.SeedNode("root-rem-id", "2026-08-28");
            var unknownPackage = CreateClipV2Package(Path.Combine(testRoot, "jobs", "clip-v2-unknown"), "2026-08-28", "摘录 · unknown", "未知外部状态摘录");
            var unknownRequest = CreateRequest(unknownPackage.RootPath, "clip-v2-unknown", "run-1", "attempt-1", '0');
            var unknownRaised = false;
            try { await RunStable(unknownGateway, unknownRequest, unknownPackage); }
            catch (ExternalStateUnknownException) { unknownRaised = true; }
            Check("clip daily unknown external state preserved", unknownRaised, "unknown commit was treated as safe failure");

            var expectedTreePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tests", "RemNoteQuickClipper.RemNoteExporter.Tests", "fixtures", "clip-daily-route-readable-tree.md");
            var expectedTree = LongPathFileSystem.ReadAllText(Path.GetFullPath(expectedTreePath), Encoding.UTF8).Replace("\r\n", "\n");
            Equal("clip daily readable tree", expectedTree, new FakeGatewayTreeFixture().Build());
        }

        private static async Task RunReadOnlyProbeTests(string testRoot)
        {
            var companion = RemNoteSettingsAdapter.DescribeCompanion(new AppConfig { NodePath = "node.exe", RemnoteCliJsPath = "cli.js", RemnoteMcpUrl = "http://127.0.0.1:43101/mcp" });
            Check("companion destination optional and Anki peer", (bool)companion["optional"] && ((string[])companion["peerDestinationTypes"]).Single() == "anki", "destination peer metadata mismatch");
            var companionTarget = Json.Dict(Json.Dict(companion["settings"])["target"]);
            Equal("companion root title is readback only", "readback-only-not-authority", Convert.ToString(companionTarget["rootTitleRole"], CultureInfo.InvariantCulture));
            var companionProbe = Json.Dict(companion["probe"]);
            Check("companion probe is strictly read only", Convert.ToInt32(companionProbe["timeoutMs"], CultureInfo.InvariantCulture) == 15000 && Convert.ToInt32(companionProbe["externalWriteCount"], CultureInfo.InvariantCulture) == 0, "probe timeout/write boundary mismatch");

            var probeRoot = Path.Combine(testRoot, "probe");
            Directory.CreateDirectory(probeRoot);
            var settingsRef = WriteProbeEvidence(probeRoot, "settings-snapshot.json", SettingsSnapshot("canary-root-id", "零碎知识剪藏 Canary"));
            var healthRequest = new RemNoteReadOnlyProbeRequest
            {
                ProbeId = "probe-health", Mode = "health", AuthorizationScope = "isolated-canary",
                RootRemId = "canary-root-id", RootTitle = "零碎知识剪藏 Canary", SettingsSnapshot = settingsRef
            };
            var healthyGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary");
            var healthy = await new RemNoteReadOnlyProbe(healthyGateway).RunAsync(healthRequest, System.Threading.CancellationToken.None);
            Equal("probe healthy state", "healthy", healthy.State);
            Equal("probe healthy admission", "ready", healthy.Admission);
            Check("probe exact root binding", healthy.RootBindingMatched && healthy.ObservedRootTitle == "零碎知识剪藏 Canary", "root binding not exact");
            Check("probe status search list read passed", healthy.Checks.Count == 4 && healthy.Checks.All(item => item.Outcome == "passed"), "read-only operation missing");
            Check("probe external writes remain zero", Convert.ToInt32(healthy.ToDictionary()["externalWriteCount"], CultureInfo.InvariantCulture) == 0 && healthyGateway.WriteCount == 0, "probe wrote external state");

            var timeoutGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary") { TimeoutOperation = "search" };
            var degraded = await new RemNoteReadOnlyProbe(timeoutGateway).RunAsync(healthRequest, System.Threading.CancellationToken.None);
            Equal("connected tool timeout is degraded", "degraded", degraded.State);
            Equal("degraded probe blocks admission", "blocked", degraded.Admission);
            Check("timeout never reports healthy", degraded.Connected == true && degraded.Checks.Single(item => item.Operation == "search").Outcome == "timeout" && degraded.Checks.Single(item => item.Operation == "list").Outcome == "not_run", "timeout was green or continued unsafely");

            var permissionGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary") { AcceptWriteOperations = false };
            var permissionBlocked = await new RemNoteReadOnlyProbe(permissionGateway).RunAsync(healthRequest, System.Threading.CancellationToken.None);
            Check("read-only probe records permission block", permissionBlocked.State == "blocked" && permissionBlocked.AcceptWriteOperations == false && permissionBlocked.Checks.All(item => item.Outcome == "passed"), "permission status not separated from read-only health");

            var spoofSettings = WriteProbeEvidence(probeRoot, "settings-spoof.json", SettingsSnapshot("canary-root-id", "伪造标题"));
            var spoofRequest = new RemNoteReadOnlyProbeRequest { ProbeId = "probe-spoof", Mode = "health", AuthorizationScope = "isolated-canary", RootRemId = "canary-root-id", RootTitle = "伪造标题", SettingsSnapshot = spoofSettings };
            var spoof = await new RemNoteReadOnlyProbe(new FakeProbeGateway("canary-root-id", "实际隔离根")).RunAsync(spoofRequest, System.Threading.CancellationToken.None);
            Check("UI root title cannot impersonate authority", spoof.State == "blocked" && !spoof.RootBindingMatched && spoof.ObservedRootTitle == "实际隔离根", "spoofed rootTitle was trusted");

            var formalSettings = WriteProbeEvidence(probeRoot, "settings-formal.json", SettingsSnapshot("formal-root-id", "剪藏"));
            var formalRequestPath = WriteProbeRequest(probeRoot, "formal-probe.json", "health", "isolated-canary", "formal-root-id", "剪藏", formalSettings, null, null);
            var formalRejected = false;
            try { RemNoteReadOnlyProbeRequest.Load(formalRequestPath); }
            catch (StableWorkerContractException ex) { formalRejected = ex.Code == "formal_root_forbidden"; }
            Check("formal clip root forbidden for Canary", formalRejected, "formal 剪藏 root accepted as Canary");

            var workerRequestRef = WriteProbeEvidence(probeRoot, "worker-request-clip.json", WorkerRequestEvidence("clip-daily-route", "canary-root-id", "零碎知识剪藏 Canary", "2026-08-28"));
            var completedResultRef = WriteProbeEvidence(probeRoot, "worker-result-completed.json", WorkerResultEvidence("completed", false));
            var reconcilePath = WriteProbeRequest(probeRoot, "reconcile-clip.json", "reconcile", "isolated-canary", "canary-root-id", "零碎知识剪藏 Canary", settingsRef, workerRequestRef, completedResultRef);
            var reconcileRequest = RemNoteReadOnlyProbeRequest.Load(reconcilePath);
            var reconcileGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary");
            var dateId = reconcileGateway.Seed("canary-root-id", "2026-08-28");
            var clipId = reconcileGateway.Seed(dateId, "摘录 · probe-fixture");
            var reconcile = await new RemNoteReadOnlyProbe(reconcileGateway).RunAsync(reconcileRequest, System.Threading.CancellationToken.None);
            Check("settings payload result reconcile chain", reconcile.State == "healthy" && reconcile.Evidence.Status == "valid" && reconcile.Evidence.Route == "clip-daily-route" && reconcile.ObservedRemIds.Contains(dateId) && reconcile.ObservedRemIds.Contains(clipId), "clip reconcile evidence chain failed");

            var unknownResultRef = WriteProbeEvidence(probeRoot, "worker-result-unknown.json", WorkerResultEvidence("unknown_external_state", true));
            var unknownPath = WriteProbeRequest(probeRoot, "reconcile-unknown.json", "reconcile", "isolated-canary", "canary-root-id", "零碎知识剪藏 Canary", settingsRef, workerRequestRef, unknownResultRef);
            var unknown = await new RemNoteReadOnlyProbe(reconcileGateway).RunAsync(RemNoteReadOnlyProbeRequest.Load(unknownPath), System.Threading.CancellationToken.None);
            var unknownChain = Json.Dict(unknown.ToDictionary()["evidenceChain"]);
            Check("unknown external state never auto retries", unknown.State == "blocked" && unknown.Evidence.UnknownExternalState && !(bool)unknownChain["automaticRetryAllowed"], "unknown state allowed automatic retry");

            var cardRequestRef = WriteProbeEvidence(probeRoot, "worker-request-card.json", WorkerRequestEvidence("card-export", "canary-root-id", "零碎知识剪藏 Canary", null));
            var cardPath = WriteProbeRequest(probeRoot, "reconcile-card.json", "reconcile", "isolated-canary", "canary-root-id", "零碎知识剪藏 Canary", settingsRef, cardRequestRef, completedResultRef);
            var cardGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary");
            var cardDocumentId = cardGateway.Seed("canary-root-id", "卡片 · probe-fixture", "document");
            var cardProbe = await new RemNoteReadOnlyProbe(cardGateway).RunAsync(RemNoteReadOnlyProbeRequest.Load(cardPath), System.Threading.CancellationToken.None);
            Check("card reconcile has no date route", cardProbe.State == "healthy" && cardProbe.Evidence.Route == "card-export" && cardProbe.Evidence.LocalDate == null && cardProbe.ObservedRemIds.Contains(cardDocumentId), "card route leaked date routing");

            var kindOnly = WorkerRequestEvidence("clip-daily-route", "canary-root-id", "零碎知识剪藏 Canary", "2026-08-28");
            Json.Dict(Json.Dict(kindOnly["payload"])["route"]).Remove("timeZone");
            var kindOnlyRef = WriteProbeEvidence(probeRoot, "worker-request-kind-only.json", kindOnly);
            var invalidPath = WriteProbeRequest(probeRoot, "reconcile-invalid.json", "reconcile", "isolated-canary", "canary-root-id", "零碎知识剪藏 Canary", settingsRef, kindOnlyRef, completedResultRef);
            var invalidGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary");
            var invalid = await new RemNoteReadOnlyProbe(invalidGateway).RunAsync(RemNoteReadOnlyProbeRequest.Load(invalidPath), System.Threading.CancellationToken.None);
            Check("invalid evidence fails before external call", invalid.State == "blocked" && invalid.Evidence.Status == "invalid" && invalid.ExternalCallCount == 0 && invalidGateway.CallCount == 0, "kind-only evidence reached MCP");
        }

        private static IDictionary<string, object> SettingsSnapshot(string rootRemId, string rootTitle)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", RemNoteSettingsAdapter.SnapshotSchemaVersion }, { "destinationType", "remnote" }, { "enabled", true },
                { "target", new Dictionary<string, object> { { "rootRemId", rootRemId }, { "rootTitle", rootTitle } } },
                { "advanced", new Dictionary<string, object>
                    {
                        { "sink", "mcp-direct" }, { "clipRouting", "clip-daily-route" }, { "cardRouting", "card-export" },
                        { "timeZone", "Asia/Shanghai" }, { "dateFormat", "yyyy-MM-dd" }
                    }
                }, { "secretReferences", new object[0] }
            };
        }

        private static async Task RunPublicProbeV2Tests(string testRoot)
        {
            var descriptor = RemNoteSettingsAdapter.DescribeCompanionV2();
            Equal("public companion descriptor v2", "remnote-destination-companion-descriptor.v2", Convert.ToString(descriptor["schemaVersion"], CultureInfo.InvariantCulture));
            var settings = Json.Dict(descriptor["settings"]); var target = Json.Dict(settings["target"]);
            Equal("target change applies next task", "next-task", Convert.ToString(target["changeEffectiveAt"], CultureInfo.InvariantCulture));
            Check("public companion hides deployment values", Convert.ToString(Json.Dict(Json.Dict(descriptor["diagnostics"])["deploymentPath"])["publicValue"], CultureInfo.InvariantCulture) == "redacted", "deployment path leaked");

            var root = Path.Combine(testRoot, "public-probe-v2"); Directory.CreateDirectory(root);
            var requestPath = Path.Combine(root, "request.json");
            File.WriteAllText(requestPath, Json.New().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", RemNotePublicProbeRequest.SchemaVersionValue }, { "probeId", "public-probe-fixture" },
                { "authorizationScope", "isolated-canary" }, { "timeoutMs", 15000 }, { "operations", new[] { "status", "search", "list", "read" } },
                { "target", new Dictionary<string, object> { { "rootRemId", "canary-root-id" }, { "rootTitle", "零碎知识剪藏 Canary" } } }
            }), new UTF8Encoding(false));
            var request = RemNotePublicProbeRequest.Load(requestPath);
            var gateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary");
            var healthy = await new RemNotePublicProbe(gateway).RunAsync(request, System.Threading.CancellationToken.None);
            Check("public probe typed root binding", healthy.State == "healthy" && healthy.Matched && healthy.ObservedType == "document", "typed root binding failed");
            var publicValue = healthy.ToDictionary();
            Check("public probe is sanitized zero-write", (bool)publicValue["sanitized"] && Convert.ToInt32(publicValue["externalWriteCount"], CultureInfo.InvariantCulture) == 0 && !Json.New().Serialize(publicValue).Contains("nodePath"), "public status leaked deployment or writes");
            var mediaCapability = Json.Dict(Json.Dict(publicValue["routeCapabilities"])["originalMedia"]);
            Check("public probe reports media write unsupported", Convert.ToString(mediaCapability["status"], CultureInfo.InvariantCulture) == "unsupported" && Convert.ToString(mediaCapability["reasonCode"], CultureInfo.InvariantCulture) == "mcp.media_write_unavailable", "media capability falsely healthy");

            var timeoutGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary") { TimeoutOperation = "read" };
            var degraded = await new RemNotePublicProbe(timeoutGateway).RunAsync(request, System.Threading.CancellationToken.None);
            Check("public connected timeout is degraded blocked", degraded.State == "degraded" && degraded.Admission == "blocked" && degraded.ReasonCodes.Contains("tool.timeout.15s"), "connected timeout reported healthy");
            Check("public timeout detail is sanitized", !Json.New().Serialize(degraded.ToDictionary()).Contains("simulated"), "raw tool error leaked");
            var permissionGateway = new FakeProbeGateway("canary-root-id", "零碎知识剪藏 Canary") { AcceptWriteOperations = false };
            var permission = await new RemNotePublicProbe(permissionGateway).RunAsync(request, System.Threading.CancellationToken.None);
            var permissionClip = Json.Dict(Json.Dict(permission.ToDictionary()["routeCapabilities"])["clip"]);
            Check("public route preflight respects write permission", permission.State == "blocked" && Convert.ToString(permissionClip["preflight"], CultureInfo.InvariantCulture) == "blocked", "date route preflight ignored acceptWriteOperations=false");
        }

        private static async Task RunTargetDiscoveryTests(string testRoot)
        {
            var descriptor = RemNoteSettingsAdapter.DescribeCompanionV3();
            Equal("target discovery companion descriptor v3", "remnote-destination-companion-descriptor.v3", Convert.ToString(descriptor["schemaVersion"], CultureInfo.InvariantCulture));
            var discoveryDescriptor = Json.Dict(descriptor["targetDiscovery"]);
            Check("target discovery descriptor is zero-write and next-task", Convert.ToInt32(discoveryDescriptor["externalWriteCount"], CultureInfo.InvariantCulture) == 0 && Convert.ToString(discoveryDescriptor["changeEffectiveAt"], CultureInfo.InvariantCulture) == "next-task", "target discovery descriptor boundary drifted");
            var media = Json.Dict(descriptor["originalMedia"]);
            Check("RemNote original media is by-design unsupported", Convert.ToString(media["productPolicy"], CultureInfo.InvariantCulture) == "by-design-unsupported" && Convert.ToString(media["supportedDestination"], CultureInfo.InvariantCulture) == "anki", "original-media product policy drifted");

            var root = Path.Combine(testRoot, "target-discovery"); Directory.CreateDirectory(root);
            var requestPath = Path.Combine(root, "request.json");
            File.WriteAllText(requestPath, Json.New().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", RemNoteTargetDiscoveryRequest.SchemaVersionValue }, { "discoveryId", "discover-fixture" },
                { "authorizationScope", "configured-destination" }, { "timeoutMs", 15000 }, { "operations", new[] { "status", "search", "list", "read" } },
                { "selector", new Dictionary<string, object> { { "rootTitle", "剪藏" }, { "expectedType", "document" }, { "match", "exact" }, { "uniqueness", "required" } } }
            }), new UTF8Encoding(false));
            var request = RemNoteTargetDiscoveryRequest.Load(requestPath);

            var healthyGateway = new FakeProbeGateway("formal-root-id", "剪藏");
            var healthy = await new RemNoteTargetDiscovery(healthyGateway).RunAsync(request, System.Threading.CancellationToken.None);
            var healthySelection = Json.Dict(healthy.ToDictionary()["selection"]);
            Check("target discovery resolves unique exact document", healthy.State == "healthy" && healthy.Admission == "ready" && healthy.ExactReadback && healthy.ResolvedRootRemId == "formal-root-id" && (bool)healthySelection["persistableBinding"], "unique target discovery failed");
            Check("target discovery is sanitized zero-write", healthyGateway.WriteCount == 0 && healthy.ExternalCallCount == 4 && Convert.ToInt32(healthy.ToDictionary()["externalWriteCount"], CultureInfo.InvariantCulture) == 0 && !Json.New().Serialize(healthy.ToDictionary()).Contains("nodePath"), "target discovery leaked deployment or wrote externally");

            var missing = await new RemNoteTargetDiscovery(new FakeProbeGateway("other-root", "其他")).RunAsync(request, System.Threading.CancellationToken.None);
            Check("target discovery not-found fails closed", missing.State == "blocked" && missing.Resolution == "not_found" && missing.ReasonCodes.Contains("target.discovery_not_found") && missing.Checks.Count == 4, "missing target did not fail closed");

            var ambiguousGateway = new FakeProbeGateway("formal-root-id", "剪藏");
            ambiguousGateway.Seed(null, "剪藏", "document");
            var ambiguous = await new RemNoteTargetDiscovery(ambiguousGateway).RunAsync(request, System.Threading.CancellationToken.None);
            Check("target discovery ambiguity fails closed", ambiguous.State == "blocked" && ambiguous.Resolution == "ambiguous" && ambiguous.CandidateCount == 2 && ambiguous.ReasonCodes.Contains("target.discovery_ambiguous"), "ambiguous target was accepted");

            var timeoutGateway = new FakeProbeGateway("formal-root-id", "剪藏") { TimeoutOperation = "search" };
            var timeout = await new RemNoteTargetDiscovery(timeoutGateway).RunAsync(request, System.Threading.CancellationToken.None);
            Check("target discovery timeout is degraded blocked", timeout.State == "degraded" && timeout.Admission == "blocked" && timeout.ReasonCodes.Contains("tool.timeout.15s"), "target discovery timeout reported healthy");

            var permissionGateway = new FakeProbeGateway("formal-root-id", "剪藏") { AcceptWriteOperations = false };
            var permission = await new RemNoteTargetDiscovery(permissionGateway).RunAsync(request, System.Threading.CancellationToken.None);
            Check("target discovery permission fails closed after exact readback", permission.State == "blocked" && permission.ExactReadback && permission.ReasonCodes.Contains("permission.write_operations_disabled"), "write permission was ignored");

            var invalidPath = Path.Combine(root, "invalid-root-id-in-selector.json");
            File.WriteAllText(invalidPath, "{\"schemaVersion\":\"remnote-target-discovery-request.v1\",\"discoveryId\":\"invalid\",\"authorizationScope\":\"configured-destination\",\"timeoutMs\":15000,\"operations\":[\"status\",\"search\",\"list\",\"read\"],\"selector\":{\"rootTitle\":\"剪藏\",\"rootRemId\":\"spoof\",\"expectedType\":\"document\",\"match\":\"exact\",\"uniqueness\":\"required\"}}", new UTF8Encoding(false));
            var invalidRejected = false;
            try { RemNoteTargetDiscoveryRequest.Load(invalidPath); } catch (StableWorkerContractException) { invalidRejected = true; }
            Check("target discovery rejects UI-supplied authority id", invalidRejected, "discovery selector accepted spoofed rootRemId");
        }

        private static async Task RunOriginalMediaV2Tests(string testRoot)
        {
            var mediaRoot = Path.Combine(testRoot, "original-media-v2"); Directory.CreateDirectory(mediaRoot);
            var assets = new List<RemNoteOriginalMediaAsset>();
            foreach (var item in new[] { "question:1:q1", "question:2:q2", "answer:1:a1", "answer:2:a2" })
            {
                var parts = item.Split(':'); var path = Path.Combine(mediaRoot, parts[2] + ".png");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("synthetic-" + parts[2]));
                assets.Add(new RemNoteOriginalMediaAsset
                {
                    AssetId = parts[2], Role = parts[0], Ordinal = int.Parse(parts[1], CultureInfo.InvariantCulture), Path = "media/" + parts[2] + ".png",
                    AbsolutePath = path, Sha256 = ExportTaskPackage.Sha256File(path).ToLowerInvariant(), Bytes = new FileInfo(path).Length, MediaType = "image/png"
                });
            }
            var media = new RemNoteOriginalMediaSet { SchemaVersion = "remnote-original-media.v2", OrderingPolicy = "explicit-role-ordinal", Assets = assets.ToArray() };
            RemNoteOriginalMediaValidator.Validate(media);
            var packageRoot = Path.Combine(testRoot, "jobs", "media-v3"); Directory.CreateDirectory(packageRoot);
            var manifest = new RemNoteExportManifest
            {
                SchemaVersion = "remnote-export-task.v3", TaskPackageId = "media-v3", DocumentTitle = "卡片 · original-media-v2", DefaultSink = "mcp-direct",
                Target = new RemNoteExportTarget { RootRemId = "root-rem-id", RootTitle = "剪藏", DateRouting = false },
                Route = new RemNoteExportRoute { Kind = "card-export" }, Inputs = new RemNoteExportInputs(), OriginalMedia = media
            };
            var cards = ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-media-v2\",\"cards\":[{\"cardId\":\"card-media\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"题目见原图\",\"back\":\"答案见原图\"}]}");
            var package = ExportTaskPackage.CreateForWorker(packageRoot, manifest, null, cards);

            var gateway = new FakeGateway("root-rem-id", "剪藏");
            var firstRequest = CreateRequest(packageRoot, "media-v3", "run-media-1", "attempt-media-1", 'm');
            var first = await RunStable(gateway, firstRequest, package);
            var firstReceipt = ReadReceipt(firstRequest);
            var receiptMedia = Json.Array(firstReceipt["originalMedia"]).Select(Json.Dict).ToArray();
            Check("original media first create exact readback", first.Status == "completed" && Convert.ToString(first.Payload["outcome"], CultureInfo.InvariantCulture) == "created" && (bool)firstReceipt["imagesHandled"], "media create not verified");
            Equal("original media preserves role order", "question:1,question:2,answer:1,answer:2", string.Join(",", receiptMedia.Select(item => item["role"] + ":" + item["ordinal"]))) ;
            Check("original media preserves SHA", receiptMedia.Select(item => Convert.ToString(item["sha256"], CultureInfo.InvariantCulture)).SequenceEqual(assets.Select(item => item.Sha256)), "media SHA changed");

            var replayRequest = CreateRequest(packageRoot, "media-v3", "run-media-2", "attempt-media-2", 'm');
            var replay = await RunStable(gateway, replayRequest, package);
            Check("original media replay already applied", Convert.ToString(replay.Payload["outcome"], CultureInfo.InvariantCulture) == "already_applied" && gateway.CreateCount == 1, "media replay duplicated write");

            var recoveredGateway = new FakeGateway("root-rem-id", "剪藏") { ThrowAfterCreate = true };
            var recoveredRequest = CreateRequest(packageRoot, "media-v3", "run-media-3", "attempt-media-3", 'n');
            var recovered = await RunStable(recoveredGateway, recoveredRequest, package);
            Check("original media uncertain commit reconciled", Convert.ToString(recovered.Payload["outcome"], CultureInfo.InvariantCulture) == "recovered_after_uncertain_commit" && recoveredGateway.CreateCount == 1, "media uncertain commit not recovered");

            var unknownGateway = new FakeGateway("root-rem-id", "剪藏") { ThrowAfterCreate = true, HideCreatedDuringReconcile = true };
            var unknownRequest = CreateRequest(packageRoot, "media-v3", "run-media-4", "attempt-media-4", 'o');
            var unknown = false;
            try { await RunStable(unknownGateway, unknownRequest, package); }
            catch (ExternalStateUnknownException) { unknown = true; }
            Check("original media unknown state blocks retry", unknown && unknownGateway.CreateCount == 1, "unknown media commit was not preserved");

            var unsupportedGateway = new FakeGateway("root-rem-id", "剪藏") { MediaSupported = false };
            var unsupportedRequest = CreateRequest(packageRoot, "media-v3", "run-media-5", "attempt-media-5", 'p');
            var unsupported = false;
            try { await RunStable(unsupportedGateway, unsupportedRequest, package); }
            catch (StableWorkerContractException ex) { unsupported = ex.Code == "destination.original_media_unsupported"; }
            Check("unsupported media fails before external write", unsupported && unsupportedGateway.CreateCount == 0 && unsupportedGateway.StatusCallCount == 0, "unsupported media silently dropped or called external writer");

            var badOrder = new RemNoteOriginalMediaSet { SchemaVersion = media.SchemaVersion, OrderingPolicy = media.OrderingPolicy, Assets = new[] { assets[2], assets[0] } };
            var orderRejected = false;
            try { RemNoteOriginalMediaValidator.Validate(badOrder); }
            catch (StableWorkerContractException ex) { orderRejected = ex.Code == "original_media_order_mismatch"; }
            Check("original media inferred order rejected", orderRejected, "unordered assets were accepted");
        }

        private static IDictionary<string, object> WorkerRequestEvidence(string routeKind, string rootRemId, string rootTitle, string localDate)
        {
            var route = new Dictionary<string, object> { { "kind", routeKind } };
            if (routeKind == "clip-daily-route")
            {
                route["timeZone"] = "Asia/Shanghai"; route["localDate"] = localDate; route["dateFormat"] = "yyyy-MM-dd";
            }
            return new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "requestId", "request-probe" }, { "operationId", "operation-probe" },
                { "taskId", "task-probe" }, { "runId", "run-probe" }, { "stageId", "export-remnote" }, { "attemptId", "attempt-probe" },
                { "workerId", "export.remnote" }, { "operation", "remnote.export" }, { "idempotencyKey", new string('a', 64) },
                { "payload", new Dictionary<string, object>
                    {
                        { "schemaVersion", "2.0.0" }, { "documentTitle", routeKind == "card-export" ? "卡片 · probe-fixture" : "摘录 · probe-fixture" },
                        { "sink", "mcp-direct" }, { "target", new Dictionary<string, object> { { "rootRemId", rootRemId }, { "rootTitle", rootTitle } } }, { "route", route }
                    }
                }
            };
        }

        private static IDictionary<string, object> WorkerResultEvidence(string status, bool unknown)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", "1.0.0" }, { "requestId", "request-probe" }, { "operationId", "operation-probe" },
                { "taskId", "task-probe" }, { "runId", "run-probe" }, { "stageId", "export-remnote" }, { "attemptId", "attempt-probe" },
                { "status", status }, { "outputs", new object[0] },
                { "error", unknown ? (object)new Dictionary<string, object> { { "code", "effect.commit_unknown" }, { "retryable", false } } : null }
            };
        }

        private static RemNoteProbeFileRef WriteProbeEvidence(string root, string name, IDictionary<string, object> value)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, Json.New().Serialize(value), new UTF8Encoding(false));
            return new RemNoteProbeFileRef { Path = Path.GetFullPath(path), Sha256 = ExportTaskPackage.Sha256File(path) };
        }

        private static string WriteProbeRequest(string root, string name, string mode, string scope, string rootRemId, string rootTitle, RemNoteProbeFileRef settings, RemNoteProbeFileRef workerRequest, RemNoteProbeFileRef workerResult)
        {
            var evidence = new Dictionary<string, object> { { "settingsSnapshot", FileRef(settings) } };
            if (workerRequest != null) evidence["workerRequest"] = FileRef(workerRequest);
            if (workerResult != null) evidence["workerResult"] = FileRef(workerResult);
            var value = new Dictionary<string, object>
            {
                { "schemaVersion", RemNoteReadOnlyProbeRequest.SchemaVersionValue }, { "probeId", Path.GetFileNameWithoutExtension(name) }, { "mode", mode },
                { "timeoutMs", RemNoteReadOnlyProbeRequest.TimeoutMilliseconds }, { "authorizationScope", scope },
                { "target", new Dictionary<string, object> { { "rootRemId", rootRemId }, { "rootTitle", rootTitle } } },
                { "operations", new[] { "status", "search", "list", "read" } }, { "evidence", evidence }
            };
            var path = Path.Combine(root, name);
            File.WriteAllText(path, Json.New().Serialize(value), new UTF8Encoding(false));
            return Path.GetFullPath(path);
        }

        private static IDictionary<string, object> FileRef(RemNoteProbeFileRef value)
        {
            return new Dictionary<string, object> { { "path", value.Path }, { "sha256", value.Sha256 } };
        }

        private static ExportTaskPackage CreateClipV2Package(string jobRoot, string localDate, string title, string text)
        {
            Directory.CreateDirectory(jobRoot);
            var manifest = new RemNoteExportManifest
            {
                SchemaVersion = "remnote-export-task.v2", TaskPackageId = new DirectoryInfo(jobRoot).Name, DocumentTitle = title, DefaultSink = "mcp-direct",
                Target = new RemNoteExportTarget { RootRemId = "root-rem-id", RootTitle = "剪藏", DateRouting = true },
                Route = new RemNoteExportRoute { Kind = "clip-daily-route", TimeZone = "Asia/Shanghai", LocalDate = localDate, DateFormat = "yyyy-MM-dd" },
                Inputs = new RemNoteExportInputs()
            };
            var learning = ExportTaskPackage.ParseLearningStrict("{\"schemaVersion\":\"learning-bundle.v1\",\"bundleId\":\"learning-" + localDate.Replace("-", "") + "\",\"entries\":[{\"itemId\":\"clip-item\",\"text\":\"" + text + "\"}]}");
            return ExportTaskPackage.CreateForWorker(jobRoot, manifest, learning, null);
        }

        private static ExportTaskPackage CreateCardV2Package(string jobRoot)
        {
            Directory.CreateDirectory(jobRoot);
            var manifest = new RemNoteExportManifest
            {
                SchemaVersion = "remnote-export-task.v2", TaskPackageId = new DirectoryInfo(jobRoot).Name, DocumentTitle = "卡片 · card-v2", DefaultSink = "mcp-direct",
                Target = new RemNoteExportTarget { RootRemId = "root-rem-id", RootTitle = "剪藏", DateRouting = false },
                Route = new RemNoteExportRoute { Kind = "card-export" }, Inputs = new RemNoteExportInputs()
            };
            var cards = ExportTaskPackage.ParseCardsStrict("{\"schemaVersion\":\"card-bundle.v1\",\"bundleId\":\"cards-v2\",\"cards\":[{\"cardId\":\"card-v2\",\"cardType\":\"basic\",\"direction\":\"forward\",\"front\":\"问题\",\"back\":\"答案\"}]}");
            return ExportTaskPackage.CreateForWorker(jobRoot, manifest, null, cards);
        }

        private static StableWorkerRequest CreateRequest(string jobRoot, string taskId, string runId, string attemptId, char keyCharacter)
        {
            var attempt = Path.Combine(jobRoot, "runs", runId, "stages", "export-remnote", "attempts", attemptId);
            Directory.CreateDirectory(attempt);
            return new StableWorkerRequest
            {
                JobRoot = jobRoot, AttemptDirectory = attempt, RequestId = "request-" + attemptId, OperationId = "operation-" + attemptId,
                TaskId = taskId, RunId = runId, StageId = "export-remnote", AttemptId = attemptId, IdempotencyKey = new string(keyCharacter, 64),
                OutputPath = Path.Combine(attempt, "outputs"), ProgressPath = Path.Combine(attempt, "progress.ndjson"), ResultPath = Path.Combine(attempt, "result.json"),
                CancelPath = Path.Combine(attempt, "cancel.json"), Payload = new Dictionary<string, object> { { "sink", "mcp-direct" } },
                ConfigSnapshot = new Dictionary<string, object>(), InputRefs = new object[0]
            };
        }

        private static async Task<StableWorkerRunResult> RunStable(FakeGateway gateway, StableWorkerRequest request, ExportTaskPackage package)
        {
            using (var cancellation = new WorkerCancellationMonitor(request)) return await new RemNoteStableWorker(gateway).RunAsync(request, package, cancellation);
        }

        private static IDictionary<string, object> ReadReceipt(StableWorkerRequest request)
        {
            var path = Path.Combine(request.OutputPath, "remnote-write-receipt", "v0001", "receipt.json");
            return Json.Dict(Json.New().DeserializeObject(LongPathFileSystem.ReadAllText(path, Encoding.UTF8)));
        }

        private sealed class FakeGatewayTreeFixture
        {
            public string Build()
            {
                var gateway = new FakeGateway("root-rem-id", "剪藏");
                var dateId = gateway.SeedNode("root-rem-id", "2026-08-28");
                var clipId = gateway.SeedNode(dateId, "摘录 · fixture-v2");
                var clip = gateway.ReadDocumentAsync(clipId).Result;
                clip.Children.Add(new RemNoteNodeSnapshot { RemId = clipId + "-child-1", Headline = "第一条摘录", Title = "第一条摘录", RemType = "text" });
                return gateway.RenderTree();
            }
        }

        private sealed class FakeProbeGateway : IRemNoteReadOnlyProbeGateway
        {
            private readonly List<RemNoteDocumentSnapshot> nodes = new List<RemNoteDocumentSnapshot>();
            public string TimeoutOperation { get; set; }
            public bool Connected { get; set; } = true;
            public bool AcceptWriteOperations { get; set; } = true;
            public int CallCount { get; private set; }
            public int WriteCount { get { return 0; } }

            public FakeProbeGateway(string rootRemId, string rootTitle)
            {
                nodes.Add(new RemNoteDocumentSnapshot { RemId = rootRemId, Title = rootTitle, Headline = rootTitle, RemType = "document", Children = new List<RemNoteNodeSnapshot>() });
            }

            public string Seed(string parentRemId, string title, string remType = "rem")
            {
                var id = "probe-node-" + (nodes.Count + 1).ToString(CultureInfo.InvariantCulture);
                nodes.Add(new RemNoteDocumentSnapshot { RemId = id, ParentRemId = parentRemId, Title = title, Headline = title, RemType = remType, Children = new List<RemNoteNodeSnapshot>() });
                return id;
            }

            public Task<RemNoteProbeStatusSnapshot> ProbeStatusAsync(int timeoutMilliseconds, System.Threading.CancellationToken cancellationToken)
            {
                Before("status", timeoutMilliseconds);
                return Task.FromResult(new RemNoteProbeStatusSnapshot { Connected = Connected, AcceptWriteOperations = AcceptWriteOperations, ServerVersion = "fake-0.17.0", PluginVersion = "fake-0.17.0" });
            }

            public Task<List<RemNoteDocumentSnapshot>> ProbeSearchAsync(string exactTitle, int timeoutMilliseconds, System.Threading.CancellationToken cancellationToken)
            {
                Before("search", timeoutMilliseconds);
                return Task.FromResult(nodes.Where(item => item.Title == exactTitle).ToList());
            }

            public Task<List<RemNoteDocumentSnapshot>> ProbeListChildrenAsync(string parentRemId, int timeoutMilliseconds, System.Threading.CancellationToken cancellationToken)
            {
                Before("list", timeoutMilliseconds);
                return Task.FromResult(nodes.Where(item => item.ParentRemId == parentRemId).ToList());
            }

            public Task<RemNoteDocumentSnapshot> ProbeReadAsync(string remId, int timeoutMilliseconds, System.Threading.CancellationToken cancellationToken)
            {
                Before("read", timeoutMilliseconds);
                return Task.FromResult(nodes.Single(item => item.RemId == remId));
            }

            private void Before(string operation, int timeoutMilliseconds)
            {
                CallCount++;
                if (timeoutMilliseconds != 15000) throw new InvalidOperationException("probe timeout drifted");
                if (TimeoutOperation == operation) throw new TimeoutException("simulated 15s timeout");
            }
        }

        private sealed class FakeGateway : IRemNoteExportGateway, IRemNoteOriginalMediaGateway
        {
            private readonly RemNoteDocumentSnapshot root;
            private readonly List<RemNoteDocumentSnapshot> nodes = new List<RemNoteDocumentSnapshot>();
            private readonly object sync = new object();
            public int CreateCount { get; private set; }
            public bool ThrowAfterCreate { get; set; }
            public bool HideCreatedDuringReconcile { get; set; }
            public bool MediaSupported { get; set; } = true;
            public int StatusCallCount { get; private set; }
            private bool hideNextFind;
            private readonly Dictionary<string, List<RemNoteMediaReadbackSnapshot>> mediaByDocument = new Dictionary<string, List<RemNoteMediaReadbackSnapshot>>(StringComparer.Ordinal);

            public FakeGateway(string rootId, string rootTitle)
            {
                root = new RemNoteDocumentSnapshot { RemId = rootId, Title = rootTitle, Headline = rootTitle, RemType = "document", Children = new List<RemNoteNodeSnapshot>() };
                nodes.Add(root);
            }

            public Task CheckStatusAsync(System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) { StatusCallCount++; return Task.FromResult(0); }

            public Task<RemNoteOriginalMediaCapability> GetOriginalMediaCapabilityAsync(System.Threading.CancellationToken cancellationToken)
            {
                return Task.FromResult(new RemNoteOriginalMediaCapability { Supported = MediaSupported, ReadCapability = "media.images.v1", WriteCapability = MediaSupported ? "fake.attach.v1" : null, ReasonCode = MediaSupported ? "ok" : "mcp.media_write_unavailable" });
            }

            public async Task<string> CreateDocumentWithOriginalMediaAsync(string parentRemId, string title, string contentFile, IReadOnlyList<RemNoteOriginalMediaAsset> media, System.Threading.CancellationToken cancellationToken)
            {
                if (!MediaSupported) throw new StableWorkerContractException("destination.original_media_unsupported", "fake media unsupported");
                var shouldThrow = ThrowAfterCreate; ThrowAfterCreate = false;
                string documentId;
                try { documentId = await CreateNodeAsync(parentRemId, title, contentFile, true, cancellationToken); }
                finally { ThrowAfterCreate = shouldThrow; }
                lock (sync)
                {
                    mediaByDocument[documentId] = media.Select((item, index) => new RemNoteMediaReadbackSnapshot
                    {
                        MediaId = documentId + "-media-" + (index + 1).ToString(CultureInfo.InvariantCulture), AssetId = item.AssetId,
                        Role = item.Role, Ordinal = item.Ordinal, Sha256 = item.Sha256, Bytes = item.Bytes, MediaType = item.MediaType
                    }).ToList();
                    if (shouldThrow)
                    {
                        if (HideCreatedDuringReconcile) hideNextFind = true;
                        throw new IOException("simulated connection loss after media commit");
                    }
                }
                return documentId;
            }

            public Task<List<RemNoteMediaReadbackSnapshot>> ReadOriginalMediaAsync(string documentRemId, System.Threading.CancellationToken cancellationToken)
            {
                lock (sync)
                {
                    List<RemNoteMediaReadbackSnapshot> value;
                    return Task.FromResult(mediaByDocument.TryGetValue(documentRemId, out value) ? value.ToList() : new List<RemNoteMediaReadbackSnapshot>());
                }
            }

            public Task<RemNoteDocumentSnapshot> ReadDocumentAsync(string remId, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                lock (sync) return Task.FromResult(nodes.Single(d => d.RemId == remId));
            }

            public Task<List<RemNoteDocumentSnapshot>> FindDirectDocumentsAsync(string parentRemId, string exactTitle, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                lock (sync)
                {
                    if (hideNextFind) { hideNextFind = false; return Task.FromResult(new List<RemNoteDocumentSnapshot>()); }
                    return Task.FromResult(nodes.Where(d => d.ParentRemId == parentRemId && d.Title == exactTitle && d.RemType == "document").ToList());
                }
            }

            public Task<List<RemNoteDocumentSnapshot>> FindDirectNodesAsync(string parentRemId, string exactTitle, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                lock (sync)
                {
                    if (hideNextFind) { hideNextFind = false; return Task.FromResult(new List<RemNoteDocumentSnapshot>()); }
                    return Task.FromResult(nodes.Where(d => d.ParentRemId == parentRemId && d.Title == exactTitle).ToList());
                }
            }

            public Task<string> CreateDocumentAsync(string parentRemId, string title, string contentFile, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                return CreateNodeAsync(parentRemId, title, contentFile, true, cancellationToken);
            }

            public Task<string> CreateNodeAsync(string parentRemId, string title, string contentFile, bool asDocument, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
            {
                lock (sync)
                {
                    CreateCount++;
                    var document = new RemNoteDocumentSnapshot
                    {
                        RemId = "fake-node-" + CreateCount,
                        ParentRemId = parentRemId,
                        Title = title,
                        Headline = title,
                        RemType = asDocument ? "document" : "rem",
                        Children = new List<RemNoteNodeSnapshot>()
                    };
                    var childIndex = 0;
                    if (!string.IsNullOrWhiteSpace(contentFile)) foreach (var raw in File.ReadAllLines(contentFile, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        var headline = raw.StartsWith("- ", StringComparison.Ordinal) ? raw.Substring(2) : raw;
                        string direction = null;
                        if (headline.IndexOf(" >> ", StringComparison.Ordinal) >= 0) direction = "forward";
                        else if (headline.IndexOf(" << ", StringComparison.Ordinal) >= 0) direction = "reverse";
                        else if (headline.IndexOf(" <> ", StringComparison.Ordinal) >= 0) direction = "bidirectional";
                        var titleText = direction == null ? headline : headline.Split(new[] { direction == "forward" ? " >> " : direction == "reverse" ? " << " : " <> " }, StringSplitOptions.None)[0];
                        document.Children.Add(new RemNoteNodeSnapshot { RemId = document.RemId + "-child-" + (++childIndex), Title = titleText, Headline = headline, RemType = "text", CardDirection = direction });
                    }
                    nodes.Add(document);
                    if (ThrowAfterCreate)
                    {
                        if (HideCreatedDuringReconcile) hideNextFind = true;
                        throw new IOException("simulated connection loss after commit");
                    }
                    return Task.FromResult(document.RemId);
                }
            }

            public string SeedNode(string parentRemId, string title, string remType = "rem")
            {
                lock (sync)
                {
                    var id = "seed-node-" + (nodes.Count + 1);
                    nodes.Add(new RemNoteDocumentSnapshot { RemId = id, ParentRemId = parentRemId, Title = title, Headline = title, RemType = remType, Children = new List<RemNoteNodeSnapshot>() });
                    return id;
                }
            }

            public int CountDirect(string parentRemId, string title)
            {
                lock (sync) return nodes.Count(item => item.ParentRemId == parentRemId && item.Title == title);
            }

            public string RenderTree()
            {
                lock (sync)
                {
                    var lines = new List<string>();
                    RenderNode(root, 0, lines);
                    return string.Join("\n", lines) + "\n";
                }
            }

            private void RenderNode(RemNoteDocumentSnapshot node, int depth, List<string> lines)
            {
                lines.Add(new string(' ', depth * 2) + "- " + node.Title + " [" + node.RemId + "]");
                foreach (var child in nodes.Where(item => item.ParentRemId == node.RemId)) RenderNode(child, depth + 1, lines);
                foreach (var content in node.Children) lines.Add(new string(' ', (depth + 1) * 2) + "- " + content.Headline + " [" + content.RemId + "]");
            }
        }
    }
}
