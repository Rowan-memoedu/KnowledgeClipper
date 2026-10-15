using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeClipper.Workflow;

public sealed record ExcerptDocument(string Id, string Title);
public sealed record ExcerptDocumentList(string RootId, string ExcerptId, ExcerptDocument[] Documents);

public sealed partial class RemNoteConnection(HttpClient http, string endpoint) : ICardImporter
{
    public const string DefaultEndpoint = "http://127.0.0.1:43111";
    private static readonly string[] LegacyDefaultEndpoints = ["http://127.0.0.1:43101", "http://localhost:43101", "http://[::1]:43101"];

    // Resolve the old default at transport time; saved settings and frozen export plans remain unchanged.
    // Match literal root URLs so custom paths, credentials, schemes, and URL suffixes are never migrated.
    public static string ResolveEndpoint(string endpoint) => LegacyDefaultEndpoints.Any(legacy =>
        string.Equals(endpoint, legacy, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(endpoint, legacy + "/", StringComparison.OrdinalIgnoreCase)) ? DefaultEndpoint : endpoint;

    private static readonly JsonSerializerOptions WireJson = new(WorkflowJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public ExportDestination? FixedDestination => ExportDestination.RemNote;
    private Uri Address(string operation)
    {
        if (!Uri.TryCreate(ResolveEndpoint(endpoint), UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidDataException("RemNote SDK 必须使用本机地址。");
        return new Uri(uri.ToString().TrimEnd('/') + "/sdk/knowledge-clipper/" + operation);
    }
    private async Task<JsonElement> RequestAsync(string operation, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, Address(operation));
        request.Headers.Add("X-KnowledgeClipper-Protocol", "1");
        if (payload != null) request.Content = JsonContent.Create(payload, options: WireJson);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string reason = "";
            try { using var error = JsonDocument.Parse(body); reason = error.RootElement.GetProperty("error").GetString() ?? ""; } catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { }
            throw new InvalidDataException("RemNote SDK 导入未完成（" + (int)response.StatusCode + "）" + (reason.Length > 0 ? "：" + reason : "。请检查本机 SDK 桥接。"));
        }
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
    public Task<JsonElement> HealthAsync(CancellationToken ct = default) => RequestAsync("health", null, ct);
    public async Task<ExcerptDocumentList> ListExcerptDocumentsAsync(string? rootRemId = null, CancellationToken ct = default)
    {
        var result = await RequestAsync("excerpt-documents" + (rootRemId == null ? "" : "?rootRemId=" + Uri.EscapeDataString(rootRemId)), null, ct);
        var root = result.GetProperty("rootId").GetString();
        var excerpt = result.GetProperty("excerptId").GetString();
        var documents = result.GetProperty("documents").EnumerateArray().Select(item =>
            new ExcerptDocument(item.GetProperty("id").GetString() ?? "", item.GetProperty("title").GetString() ?? "")).ToArray();
        if (string.IsNullOrWhiteSpace(root) || rootRemId != null && root != rootRemId || string.IsNullOrWhiteSpace(excerpt) ||
            documents.Any(item => string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Title)) ||
            documents.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != documents.Length)
            throw new InvalidDataException("RemNote 摘录目录列表无效。");
        return new(root, excerpt, documents);
    }
    private async Task<JsonElement> RequireHealthyAsync(CancellationToken ct, bool mediaFinalize = false, bool spacingFinalize = false)
    {
        var health = await HealthAsync(ct);
        if (!True(health, "connected"))
            throw new InvalidDataException("RemNote SDK 插件尚未连接；草稿已保留，连接恢复后可继续。");
        if (!True(health, "supported"))
            throw new InvalidDataException("RemNote SDK 插件需要更新以支持剪藏；草稿已保留。");
        if (health.TryGetProperty("sdkResponsive", out var responsive) && responsive.ValueKind == JsonValueKind.False)
            throw new InvalidDataException("RemNote SDK 已连接，但暂未响应；草稿已保留，未重新发送导入。");
        if (!True(health, "acceptWriteOperations"))
            throw new InvalidDataException("RemNote 剪藏插件未允许写入；草稿已保留。");
        if (mediaFinalize && !True(health, "mediaFinalizeSupported"))
            throw new InvalidDataException("RemNote SDK 插件尚未支持现有图片恢复；保留原卡片，更新插件后可继续核对。");
        if (spacingFinalize && !True(health, "spacingFinalizeSupported"))
            throw new InvalidDataException("RemNote SDK 插件尚未支持原批次空行恢复；保留原节点，更新插件后可继续核对。");
        return health;
    }

    public async Task ImportAsync(ClipDraft draft, DraftStore store, CancellationToken ct = default)
    {
        await draft.ImportGate.WaitAsync(ct);
        try
        {
            if (ExportRouting.For(draft) != ExportDestination.RemNote) throw new InvalidDataException("卡片已确认的目标不是 RemNote。");
            if (draft.ImportState == ImportState.Verified) return;
            var plan = draft.ConfirmedRemNote ?? throw new InvalidOperationException("请先确认 RemNote 导入预览。");
            draft.ImportError = null;
            var uncertain = draft.ImportState is ImportState.Unknown or ImportState.Writing || draft.RemNoteResult != null;
            JsonElement result;
            if (uncertain) result = await RequestAsync("readback", plan, ct);
            else
            {
                var health = await RequireHealthyAsync(ct);
                if (plan.Cards.SelectMany(c => c.Front.Concat(c.Back.SelectMany(r => r))).Any(p => p.Kind == "mermaid") &&
                    !True(health, "mermaidSupported"))
                    throw new InvalidDataException("请更新剪藏 RemNote 插件以显示 Mermaid 图；源码与草稿已保留，尚未写入。");
                if ((plan.DocumentIds != null || plan.Cards.Any(c => c.BackParents != null)) && !True(health, "questionCompositionSupported"))
                    throw new InvalidDataException("请更新剪藏专用 RemNote 插件以支持类型目录和答案层级；尚未写入。");
                if (plan.DocumentIds is { Length: 2 } && plan.DocumentPath.Length == 3 &&
                    !True(health, "questionDailyDocumentsSupported"))
                    throw new InvalidDataException("请更新剪藏专用 RemNote 插件以支持类型内的日期目录；尚未写入。");
                if (plan.DocumentPath.FirstOrDefault() == "问答")
                    throw new InvalidDataException("旧问答目标已退役；保留冻结身份。请先核对原导入，重新选材时使用新的类型目录草稿。");
                draft.ImportState = ImportState.Writing; store.Save(draft);
                result = await RequestAsync("export", new { plan, newSource = true }, ct);
            }
            if (True(result, "notStarted"))
            {
                if (result.GetProperty("schemaVersion").GetString() != plan.SchemaVersion ||
                    result.GetProperty("sourceId").GetString() != plan.SourceId || draft.RemNoteResult != null || draft.NoteId != null)
                    throw new InvalidDataException("RemNote 未写入回执与当前草稿身份不符；继续保留待核对状态。");
                // Only a positive, persisted SDK failure checkpoint permits this transition.
                // Missing records never do, and this request never performs a replacement write.
                draft.ImportState = ImportState.Failed;
                throw new InvalidDataException("RemNote 已确认上次导入准备失败，内容尚未开始写入。草稿与目标保持不变，请重试导入。");
            }
            if (uncertain && True(result, "batchSeparatorPending"))
            {
                Require(result.TryGetProperty("spacingRecovery", out var spacing) && True(spacing, "eligible") &&
                    spacing.TryGetProperty("kind", out var spacingKind) && spacingKind.GetString() == "finalize-batch-separator", "批次空行恢复证据");
                var observed = ValidateCore(plan, result, draft.RemNoteResult, allowOriginalImages: true, out _, allowPendingSpacing: true);
                var contentIdentity = IdentityOf(result); var separatorId = Id(result.GetProperty("batchSeparator"), "remId");
                // This is an identity checkpoint, never a completion mark. Lost responses reopen as pure readback.
                draft.RemNoteResult ??= observed;
                draft.ImportState = ImportState.Unknown; store.Save(draft);
                await RequireHealthyAsync(ct, spacingFinalize: true);
                var finalized = await RequestAsync("finalize-spacing", new { plan, expected = contentIdentity, separatorRemId = separatorId }, ct);
                RequireIdentity(finalized, contentIdentity);
                ValidateCore(plan, finalized, draft.RemNoteResult, allowOriginalImages: true, out _);
                result = await RequestAsync("readback", plan, ct);
                RequireIdentity(result, contentIdentity);
                Require(Id(result.GetProperty("batchSeparator"), "remId") == separatorId, "批次空行恢复后的原身份");
            }
            MediaIdentity? mediaIdentity = null;
            if (uncertain && result.TryGetProperty("mediaRecovery", out var recovery) && True(recovery, "eligible"))
            {
                Require(recovery.TryGetProperty("kind", out var kind) && kind.GetString() == "finalize-local-images" &&
                    recovery.TryGetProperty("pendingImages", out var pending) && pending.TryGetInt32(out var count) && count > 0,
                    "图片恢复回执类型");
                var observedIdentity = ValidateCore(plan, result, draft.RemNoteResult, allowOriginalImages: true, out var pendingImages);
                Require(True(result, "mediaPending") && pendingImages > 0 && recovery.GetProperty("pendingImages").GetInt32() == pendingImages,
                    "待托管图片数量");
                mediaIdentity = IdentityOf(result);
                // A receipt can pin fully checked node identities while its original evidence still says mediaPending.
                // Completion is exclusively ImportState.Verified. Save before the media write so a lost response or
                // process exit cannot discard this identity boundary; retain any older receipt rather than replacing it.
                draft.RemNoteResult ??= observedIdentity;
                draft.ImportState = ImportState.Unknown;
                store.Save(draft);
                await RequireHealthyAsync(ct, mediaFinalize: true);
                // This operation can only finish media on these exact existing Rems. It cannot create a card.
                var finalized = await RequestAsync("finalize-media", new { plan, expected = mediaIdentity }, ct);
                RequireIdentity(finalized, mediaIdentity);
                ValidateCore(plan, finalized, draft.RemNoteResult, allowOriginalImages: true, out _);
                result = await RequestAsync("readback", plan, ct);
                RequireIdentity(result, mediaIdentity);
            }
            if (uncertain && True(result, "mediaPending") && result.TryGetProperty("mediaRecovery", out var unavailable) && !True(unavailable, "eligible") &&
                unavailable.TryGetProperty("error", out var reason) && reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString()))
                throw new InvalidDataException("RemNote 图片恢复尚未完成：" + reason.GetString());
            var until = DateTimeOffset.UtcNow.AddSeconds(30);
            while (Pending(plan, result) && DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(600, ct);
                result = await RequestAsync("readback", plan, ct);
                if (mediaIdentity != null) RequireIdentity(result, mediaIdentity);
            }
            draft.RemNoteResult = Validate(plan, result, draft.RemNoteResult);
            draft.ImportState = ImportState.Verified; draft.ImportError = null;
            draft.Times["remnoteVerified"] = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            draft.ImportState = draft.ImportState is ImportState.Writing or ImportState.Unknown || draft.RemNoteResult != null ? ImportState.Unknown : ImportState.Failed;
            draft.ImportError = ex is HttpRequestException or TaskCanceledException ? "暂时无法核对 RemNote；草稿已保留，重试先读回，不重复添加。" : ex.Message;
            throw;
        }
        finally { store.Save(draft); draft.ImportGate.Release(); }
    }

    private sealed record MediaCardIdentity(string Key, string[] RemIds, string[] ChildRemIds, string[] CardIds);
    private sealed record MediaIdentity(string RootRemId, string DocumentRemId, MediaCardIdentity[] Cards);
    private static MediaIdentity IdentityOf(JsonElement result) => new(Id(result, "rootRemId"), Id(result, "documentRemId"),
        result.GetProperty("cards").EnumerateArray().Select(card => new MediaCardIdentity(Id(card, "key"), Ids(card, "remIds"),
            Ids(card, "childRemIds"), Ids(card, "cardIds"))).ToArray());
    private static void RequireIdentity(JsonElement result, MediaIdentity expected)
    {
        var actual = IdentityOf(result);
        Require(actual.RootRemId == expected.RootRemId && actual.DocumentRemId == expected.DocumentRemId && actual.Cards.Length == expected.Cards.Length &&
            actual.Cards.Zip(expected.Cards).All(pair => pair.First.Key == pair.Second.Key && pair.First.RemIds.SequenceEqual(pair.Second.RemIds) &&
                pair.First.ChildRemIds.SequenceEqual(pair.Second.ChildRemIds) && pair.First.CardIds.SequenceEqual(pair.Second.CardIds)), "图片恢复前后的完整节点身份");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("RemNote 读回不符：" + message + "；不会重新添加。");
    }

    private static bool True(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
    private static int ExpectedCards(RemNoteCardPlan card) => card.Kind == "plain" ? 0 : card.Kind == "basic" ? 1 :
        card.Front.Where(part => part.ClozeId != null).Select(part => part.ClozeId).Distinct().Count();
    private static bool Pending(RemNoteExportPlan plan, JsonElement result)
    {
        if (True(result, "mediaPending")) return true;
        if (!result.TryGetProperty("cards", out var cards) || cards.GetArrayLength() != plan.Cards.Length) return false;
        return cards.EnumerateArray().Select((card, index) => card.TryGetProperty("cardIds", out var ids) && ids.GetArrayLength() < ExpectedCards(plan.Cards[index])).Any(value => value);
    }

    public static RemNoteImportReceipt Validate(RemNoteExportPlan plan, JsonElement result, RemNoteImportReceipt? previous = null) =>
        ValidateCore(plan, result, previous, allowOriginalImages: false, out _);

    private static RemNoteImportReceipt ValidateCore(RemNoteExportPlan plan, JsonElement result, RemNoteImportReceipt? previous,
        bool allowOriginalImages, out int pendingImages, bool allowPendingSpacing = false)
    {
        pendingImages = 0;
        Require(result.GetProperty("schemaVersion").GetString() == plan.SchemaVersion && result.GetProperty("sourceId").GetString() == plan.SourceId, "导入身份");
        var rootId = Id(result, "rootRemId"); var directory = Id(result, "documentRemId");
        Require(plan.RootRemId == null || plan.RootRemId == rootId, "根目录");
        Require(plan.TargetDocumentId == null || plan.TargetDocumentId == directory, "所选摘录目录");
        if (plan.DocumentIds != null)
        {
            Require(result.TryGetProperty("directoryNodes", out var directories), "类型目录原始节点");
            var chain = directories.EnumerateArray().ToArray();
            Require(chain.Length == plan.DocumentPath.Length + 1 && Id(chain[0], "remId") == rootId &&
                Id(chain[^1], "remId") == directory, "类型目录完整祖先链");
            for (var i = 0; i < chain.Length; i++)
            {
                var node = chain[i];
                Require(True(node, "isDocument") && (i == 0 ? Id(node, "remId") == rootId :
                    i > plan.DocumentIds.Length || Id(node, "remId") == plan.DocumentIds[i - 1]), "类型目录身份和 document 标记");
                Require(i == 0 ? node.GetProperty("parentId").ValueKind == JsonValueKind.Null : Id(node, "parentId") == Id(chain[i - 1], "remId"), "类型目录祖先位置");
                var title = string.Concat(node.GetProperty("text").EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetProperty("text").GetString()));
                Require(title == (i == 0 ? plan.RootTitle : plan.DocumentPath[i - 1]), "类型目录标题");
            }
        }
        Require(previous == null || previous.RootRemId == rootId && previous.DocumentRemId == directory, "已确认目录");
        Require(allowOriginalImages || !True(result, "mediaPending"), "图片仍未保存到 RemNote");
        if (previous != null) RequireIdentity(result, IdentityOf(previous.Evidence));
        var actualCards = result.GetProperty("cards").EnumerateArray().ToArray();
        Require(actualCards.Length == plan.Cards.Length, "内容数量");
        var remIds = new List<string>(); var cardIds = new List<string>();
        for (var index = 0; index < plan.Cards.Length; index++)
        {
            var expected = plan.Cards[index]; var card = actualCards[index];
            Require(card.GetProperty("key").GetString() == expected.Key && card.GetProperty("kind").GetString() == expected.Kind, "卡片顺序或种类");
            var ids = Ids(card, "remIds"); var practice = Ids(card, "cardIds");
            var children = Ids(card, "childRemIds"); var nodes = card.GetProperty("nodes").EnumerateArray().ToArray();
            Require(ids.Length == 1 && nodes.Length == 1 + expected.Back.Length && children.Length == expected.Back.Length, "正反面节点数量");
            Require(practice.Length == ExpectedCards(expected), "实际生成的复习卡数量");
            Require(Id(nodes[0], "remId") == ids[0] && Id(nodes[0], "parentId") == directory, "正面所在目录");
            // Older confirmed exports set this role on the front incorrectly. Accept
            // their existing identity on readback; new exports set it only on answers.
            Require(expected.Kind == "basic" || !True(nodes[0], "isCardItem"), "非多行正面节点类型");
            Require(nodes[0].TryGetProperty("practiceEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                enabled.GetBoolean() == (expected.Kind != "plain"), "复习启用状态");
            Require(expected.Kind != "basic" || nodes[0].TryGetProperty("practiceDirection", out var direction) && direction.GetString() == "forward", "卡片练习方向");
            Require(Ids(nodes[0], "children").SequenceEqual(children.Where((_,i) => (expected.BackParents?[i] ?? -1) < 0)), "背面节点顺序");
            Require(EqualParts(expected.Front, ActualParts(nodes[0], allowOriginalImages, ref pendingImages), allowOriginalImages), "正面文字、换行、挖空或图片");
            for (var row = 0; row < expected.Back.Length; row++)
            {
                var node = nodes[row + 1];
                var parent = expected.BackParents?[row] ?? -1;
                Require(Id(node, "remId") == children[row] && Id(node, "parentId") == (parent < 0 ? ids[0] : children[parent]), "反面节点父级");
                Require(Ids(node, "children").SequenceEqual(children.Where((_,i) => expected.BackParents?[i] == row)), "答案子层级顺序");
                Require(expected.Kind == "basic" ? True(node, "isCardItem") == (parent < 0) : True(node, "isExtraCardDetail") && !True(node, "isCardItem"), "多行卡或挖空附加说明类型");
                Require(EqualParts(expected.Back[row], ActualParts(node, allowOriginalImages, ref pendingImages), allowOriginalImages), "反面文字、公式或图片顺序");
            }
            Require(nodes.All(node => !True(node, "isDocument") && node.GetProperty("remType").GetInt32() == 0 &&
                node.GetProperty("backText").GetArrayLength() == 0), "内容必须为普通文本节点");
            Require(practice.SequenceEqual(nodes.SelectMany(node => Ids(node, "cardIds"))), "复习卡身份");
            Require(practice.SequenceEqual(Ids(nodes[0], "cardIds")) && nodes.Skip(1).All(node => Ids(node, "cardIds").Length == 0),
                "复习卡必须归属单一正面节点");
            Require(nodes.All(node => node.GetProperty("cardTypes").GetArrayLength() == Ids(node, "cardIds").Length), "复习卡类型数量");
            var expectedTypes = expected.Kind == "plain" ? Array.Empty<string>() : expected.Kind == "basic" ? ["forward"] :
                expected.Front.Where(part => part.ClozeId != null).Select(part => "cloze:kc_" + part.ClozeId).Distinct().ToArray();
            var actualTypes = nodes.SelectMany(node => node.GetProperty("cardTypes").EnumerateArray()).Select(type =>
                type.ValueKind == JsonValueKind.String ? type.GetString()! :
                type.ValueKind == JsonValueKind.Object && type.TryGetProperty("clozeId", out var group) ? "cloze:" + group.GetString() : "unsupported");
            Require(expectedTypes.Order(StringComparer.Ordinal).SequenceEqual(actualTypes.Order(StringComparer.Ordinal)), "原生卡片类型或挖空分组");
            remIds.AddRange(ids); cardIds.AddRange(practice);
        }
        Require(remIds.Count > 0 && remIds.Distinct().Count() == remIds.Count && cardIds.Distinct().Count() == cardIds.Count, "重复节点或卡片身份");
        Require(previous == null || previous.RemIds.SequenceEqual(remIds) && previous.CardIds.SequenceEqual(cardIds), "既有导入身份");
        ValidateBatchSeparator(result, directory, remIds, previous, allowPendingSpacing);
        return new(rootId, directory, remIds.ToArray(), cardIds.ToArray(), result.Clone());
    }

    private static void ValidateBatchSeparator(JsonElement result, string directory, List<string> contentIds, RemNoteImportReceipt? previous, bool allowPending)
    {
        // Legacy journals have no separator. New receipts retain its identity without counting the blank as content.
        var pending = True(result, "batchSeparatorPending");
        Require(!pending || allowPending, "批次空行尚未完成");
        var present = result.TryGetProperty("batchSeparator", out var separator);
        var hadPrevious = previous != null && previous.Evidence.TryGetProperty("batchSeparator", out _);
        Require(!(hadPrevious || pending) || present, "批次空行身份缺失");
        if (!present) return;
        Require(separator.ValueKind == JsonValueKind.Object && separator.TryGetProperty("node", out var rawNode) && rawNode.ValueKind == JsonValueKind.Object,
            "批次空行原始节点缺失");
        var id = Id(separator, "remId"); var node = separator.GetProperty("node");
        var unattached = node.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.Null;
        Require(!contentIds.Contains(id) && id != directory && id != Id(result, "rootRemId") &&
            Id(separator, "previousRemId") == contentIds[^1] && Id(node, "remId") == id && (pending && unattached || !unattached && Id(node, "parentId") == directory),
            "批次空行节点身份或父级");
        if (hadPrevious) Require(Id(previous!.Evidence.GetProperty("batchSeparator"), "remId") == id, "既有批次空行身份");
        bool EmptyText(string field) => node.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.Array &&
            text.EnumerateArray().All(part => part.ValueKind == JsonValueKind.String ? part.GetString() == "" :
                part.ValueKind == JsonValueKind.Object && part.TryGetProperty("i", out var kind) && kind.GetString() == "m" &&
                part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == "" &&
                part.EnumerateObject().All(property => property.Name is "i" or "text"));
        bool FalseFlag(string field) => node.TryGetProperty(field, out var flag) && flag.ValueKind == JsonValueKind.False;
        bool EmptyArray(string field) => node.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 0;
        Require(EmptyText("text") && EmptyText("backText") && node.TryGetProperty("remType", out var type) && type.TryGetInt32(out var typeValue) && typeValue == 0 &&
            FalseFlag("isDocument") && FalseFlag("isCardItem") && FalseFlag("isExtraCardDetail") && (FalseFlag("practiceEnabled") || pending && True(node, "practiceEnabled")) &&
            EmptyArray("children") && EmptyArray("cardIds") && EmptyArray("cardTypes"), "批次空行必须为无内容、无子级、无卡片的普通节点");
        Require(result.TryGetProperty("documentChildren", out var siblingValue) && siblingValue.ValueKind == JsonValueKind.Array,
            "批次空行缺少目录子级顺序");
        var siblings = Ids(result, "documentChildren"); var start = Array.IndexOf(siblings, contentIds[0]);
        var expected = contentIds.Append(id).ToArray();
        if (pending)
        {
            var positions = siblings.Select((value, index) => (value, index)).Where(pair => expected.Contains(pair.value)).Select(pair => pair.index).ToArray();
            Require(positions.Length == contentIds.Count + (unattached ? 0 : 1) && positions[^1] - positions[0] + 1 == positions.Length &&
                contentIds.All(contentId => siblings.Count(value => value == contentId) == 1) && siblings.Count(value => value == id) == (unattached ? 0 : 1),
                "待恢复批次必须保持连续、无混入节点的原身份范围");
        }
        else Require(start >= 0 && start + expected.Length <= siblings.Length &&
                expected.Select((expectedId, offset) => siblings[start + offset] == expectedId && siblings.Count(value => value == expectedId) == 1).All(value => value),
                "批次内容与末尾空行的同级顺序");
    }
    private static string Id(JsonElement value, string name) => value.GetProperty(name).GetString() is { Length: > 0 } text
        ? text : throw new InvalidDataException("RemNote 返回了空身份字段：" + name);
    private static string[] Ids(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().Select(element =>
        element.GetString() is { Length: > 0 } text ? text : throw new InvalidDataException("RemNote 返回了空身份。")).ToArray();
    private static RemNoteRichPart[] ActualParts(JsonElement node, bool allowOriginalImages, ref int pendingImages)
    {
        var parts = new List<RemNoteRichPart>(); var position = 0; var imageIndex = 0;
        var mediaItems = node.GetProperty("media").EnumerateArray().ToArray();
        foreach (var raw in node.GetProperty("text").EnumerateArray())
        {
            if (raw.ValueKind == JsonValueKind.String) parts.Add(new("text", raw.GetString()));
            else
            {
                var kind = raw.GetProperty("i").GetString();
                int? group = raw.TryGetProperty("cId", out var id) && id.GetString() is { Length: > 0 } value ?
                    value.StartsWith("kc_", StringComparison.Ordinal) && int.TryParse(value[3..], out var number) && number > 0 ? number :
                        throw new InvalidDataException("RemNote 挖空组身份无法核对。") : null;
                var hint = raw.TryGetProperty("cloze-hint", out var help) ? help.GetString() : null;
                if (kind is "m" or "x") parts.Add(new(kind == "x" ? "latex" : group != null ? "cloze" :
                    True(raw, "code") && raw.TryGetProperty("language", out var language) && language.GetString() == "mermaid" ? "mermaid" : "text", raw.GetProperty("text").GetString(),
                    ClozeId: group, Hint: hint, Block: kind == "x" ? True(raw, "block") : null));
                else if (kind == "i")
                {
                    var matches = mediaItems.Where(item => item.GetProperty("elementIndex").GetInt32() == position).ToArray();
                    Require(matches.Length == 1, "图片托管证据位置");
                    var media = matches[0];
                    var uri = raw.GetProperty("url").GetString();
                    Require(uri != null && media.GetProperty("uri").GetString() == uri && media.GetProperty("imageIndex").GetInt32() == imageIndex++, "图片托管证据顺序");
                    if (uri!.StartsWith("%LOCAL_FILE%", StringComparison.Ordinal))
                    {
                        Require(media.TryGetProperty("sha256", out var hash) && hash.ValueKind == JsonValueKind.String &&
                            hash.GetString() is { Length: 64 } digest && digest.All(Uri.IsHexDigit), "托管图片指纹缺失");
                        parts.Add(new("image", Uri: uri, Sha256: hash.GetString()));
                    }
                    else
                    {
                        Require(allowOriginalImages && Uri.TryCreate(uri, UriKind.Absolute, out var local) && local.IsFile && local.Host.Length == 0 &&
                            (!media.TryGetProperty("sha256", out var hash) || hash.ValueKind == JsonValueKind.Null), "图片未核对为 RemNote 托管媒体");
                        parts.Add(new("image", Uri: uri));
                        pendingImages++;
                    }
                }
                else throw new InvalidDataException("无法无损核对 RemNote 富文本类型：" + kind);
            }
            position++;
        }
        Require(mediaItems.Length == imageIndex, "图片托管证据数量");
        return parts.ToArray();
    }
    private static bool EqualParts(RemNoteRichPart[] expected, RemNoteRichPart[] actual, bool allowOriginalImages)
    {
        RemNoteRichPart[] Normalize(RemNoteRichPart[] parts)
        {
            var result = new List<RemNoteRichPart>();
            foreach (var original in parts)
            {
                var part = original.Kind == "image" ? new RemNoteRichPart("image", Uri: original.Uri, Sha256: original.Sha256?.ToLowerInvariant()) :
                    original with { Hint = string.IsNullOrEmpty(original.Hint) ? null : original.Hint };
                if (part.Kind == "text" && part.Text == "") continue;
                if (part.Kind == "text" && result.LastOrDefault()?.Kind == "text") result[^1] = result[^1] with { Text = result[^1].Text + part.Text };
                else result.Add(part);
            }
            return result.ToArray();
        }
        var wanted = Normalize(expected); var received = Normalize(actual);
        return wanted.Length == received.Length && wanted.Zip(received).All(pair => pair.First.Kind == "image" && pair.Second.Kind == "image"
            ? pair.Second.Uri?.StartsWith("%LOCAL_FILE%", StringComparison.Ordinal) == true
                ? pair.First.Sha256 != null && pair.First.Sha256 == pair.Second.Sha256
                : allowOriginalImages && pair.First.Uri == pair.Second.Uri && pair.Second.Sha256 == null
            : pair.First == pair.Second);
    }
}
