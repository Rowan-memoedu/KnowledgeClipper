using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KnowledgeClipper
{
    internal sealed class RemNoteOriginalMediaAsset
    {
        public string AssetId { get; set; }
        public string Role { get; set; }
        public int Ordinal { get; set; }
        public string Path { get; set; }
        public string AbsolutePath { get; set; }
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
        public string MediaType { get; set; }
    }

    internal sealed class RemNoteOriginalMediaSet
    {
        public string SchemaVersion { get; set; }
        public string OrderingPolicy { get; set; }
        public RemNoteOriginalMediaAsset[] Assets { get; set; }
    }

    internal sealed class RemNoteOriginalMediaCapability
    {
        public bool Supported { get; set; }
        public string ReadCapability { get; set; }
        public string WriteCapability { get; set; }
        public string ReasonCode { get; set; }
    }

    internal sealed class RemNoteMediaReadbackSnapshot
    {
        public string MediaId { get; set; }
        public string AssetId { get; set; }
        public string Role { get; set; }
        public int Ordinal { get; set; }
        public string Sha256 { get; set; }
        public long Bytes { get; set; }
        public string MediaType { get; set; }
    }

    internal interface IRemNoteOriginalMediaGateway
    {
        Task<RemNoteOriginalMediaCapability> GetOriginalMediaCapabilityAsync(CancellationToken cancellationToken);
        Task<string> CreateDocumentWithOriginalMediaAsync(string parentRemId, string title, string contentFile, IReadOnlyList<RemNoteOriginalMediaAsset> media, CancellationToken cancellationToken);
        Task<List<RemNoteMediaReadbackSnapshot>> ReadOriginalMediaAsync(string documentRemId, CancellationToken cancellationToken);
    }

    internal static class RemNoteOriginalMediaValidator
    {
        private static readonly HashSet<string> MediaTypes = new HashSet<string>(new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }, StringComparer.Ordinal);

        public static void Validate(RemNoteOriginalMediaSet media)
        {
            if (media == null) return;
            if (media.SchemaVersion != "remnote-original-media.v2" || media.OrderingPolicy != "explicit-role-ordinal")
                throw new StableWorkerContractException("invalid_original_media", "originalMedia 必须为 remnote-original-media.v2 / explicit-role-ordinal");
            var assets = media.Assets ?? new RemNoteOriginalMediaAsset[0];
            if (assets.Length == 0) throw new StableWorkerContractException("invalid_original_media", "originalMedia.assets 不能为空");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var roleOrdinals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                if (asset == null) throw new StableWorkerContractException("invalid_original_media", "originalMedia 含空 asset");
                ExportTaskPackage.RequireId(asset.AssetId, "originalMedia.assetId");
                if (!ids.Add(asset.AssetId)) throw new StableWorkerContractException("duplicate_original_media", "originalMedia.assetId 重复");
                if (asset.Role != "question" && asset.Role != "answer") throw new StableWorkerContractException("invalid_original_media_role", "originalMedia.role 必须为 question 或 answer");
                if (asset.Ordinal < 1 || !roleOrdinals.Add(asset.Role + ":" + asset.Ordinal.ToString(CultureInfo.InvariantCulture)))
                    throw new StableWorkerContractException("duplicate_original_media_ordinal", "originalMedia role/ordinal 必须唯一且 ordinal >= 1");
                if (string.IsNullOrWhiteSpace(asset.Path) || string.IsNullOrWhiteSpace(asset.AbsolutePath)) throw new StableWorkerContractException("invalid_original_media_path", "originalMedia.path 无效");
                if (string.IsNullOrWhiteSpace(asset.Sha256) || asset.Sha256.Length != 64 || asset.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
                    throw new StableWorkerContractException("invalid_original_media_hash", "originalMedia.sha256 无效");
                if (asset.Bytes < 1 || !MediaTypes.Contains(asset.MediaType)) throw new StableWorkerContractException("invalid_original_media_type", "originalMedia bytes/mediaType 无效");
            }
            var expected = assets.OrderBy(item => item.Role == "question" ? 0 : 1).ThenBy(item => item.Ordinal).Select(item => item.AssetId).ToArray();
            if (!assets.Select(item => item.AssetId).SequenceEqual(expected))
                throw new StableWorkerContractException("original_media_order_mismatch", "originalMedia 必须按 question ordinal 后 answer ordinal 的显式顺序排列");
        }

        public static void ValidateReadback(RemNoteOriginalMediaSet expectedSet, IList<RemNoteMediaReadbackSnapshot> actual)
        {
            var expected = expectedSet == null ? new RemNoteOriginalMediaAsset[0] : expectedSet.Assets ?? new RemNoteOriginalMediaAsset[0];
            actual = actual ?? new List<RemNoteMediaReadbackSnapshot>();
            if (actual.Count != expected.Length) throw new InvalidOperationException("RemNote 原图回读数量不匹配");
            for (var index = 0; index < expected.Length; index++)
            {
                var source = expected[index]; var readback = actual[index];
                if (readback == null || string.IsNullOrWhiteSpace(readback.MediaId) || readback.AssetId != source.AssetId || readback.Role != source.Role ||
                    readback.Ordinal != source.Ordinal || !string.Equals(readback.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    readback.Bytes != source.Bytes || readback.MediaType != source.MediaType)
                    throw new InvalidOperationException("RemNote 原图第 " + (index + 1).ToString(CultureInfo.InvariantCulture) + " 项 exact readback 不匹配");
            }
        }

        public static object[] ReceiptItems(IList<RemNoteMediaReadbackSnapshot> media)
        {
            return (media ?? new List<RemNoteMediaReadbackSnapshot>()).Select(item => (object)new Dictionary<string, object>
            {
                { "mediaId", item.MediaId }, { "assetId", item.AssetId }, { "role", item.Role }, { "ordinal", item.Ordinal },
                { "sha256", item.Sha256 }, { "bytes", item.Bytes }, { "mediaType", item.MediaType }
            }).ToArray();
        }
    }
}
