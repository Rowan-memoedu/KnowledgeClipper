using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Application;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.CompatibilityGateway;

internal sealed class KernelCompatibilityGateway
{
    private static readonly HashSet<string> LegacyEnvelopeVersions = new(StringComparer.Ordinal)
    {
        "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7"
    };

    private readonly KernelApplication _application;
    private readonly string _legacyDataRoot;

    public KernelCompatibilityGateway(KernelApplication application, string legacyDataRoot)
    {
        _application = application;
        _legacyDataRoot = Path.GetFullPath(legacyDataRoot);
    }

    public async Task<string> HandleAsync(string requestJson, CancellationToken cancellationToken)
    {
        string correlationId = "unknown";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(requestJson);
            using var document = StrictJson.Parse(bytes, "IPC request");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new KernelContractException("request_invalid", "IPC request must be an object.");
            }

            if (root.TryGetProperty("correlationId", out var correlation) && correlation.ValueKind == JsonValueKind.String)
            {
                correlationId = correlation.GetString()!;
            }

            if (root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String)
            {
                return await HandleCurrentAsync(root, correlationId, cancellationToken).ConfigureAwait(false);
            }

            if (root.TryGetProperty("protocolVersion", out var legacyVersion) && legacyVersion.ValueKind == JsonValueKind.String)
            {
                return await HandleLegacyAsync(root, correlationId, cancellationToken).ConfigureAwait(false);
            }

            throw new KernelContractException("request_schema_missing", "IPC request has neither a current schema nor a legacy protocolVersion.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ex is KernelContractException contract
                ? new KernelError(contract.Code, contract.Message, false)
                : new KernelError("request_failed", ex.Message, false);
            return JsonSerializer.Serialize(
                new WireResponseEnvelope(
                    KernelSchemas.WireResponse,
                    correlationId,
                    "protocol-error",
                    Ok: false,
                    Payload: null,
                    error),
                KernelJson.Options);
        }
    }

    private async Task<string> HandleCurrentAsync(JsonElement root, string correlationId, CancellationToken cancellationToken)
    {
        var request = root.Deserialize<WireRequestEnvelope>(KernelJson.Options)
            ?? throw new KernelContractException("request_invalid", "Current request deserialized to null.");
        if (request.Schema != KernelSchemas.WireRequest)
        {
            throw new KernelContractException("wire_schema_unsupported", $"Unsupported wire schema '{request.Schema}'.");
        }

        object payload = request.MessageType switch
        {
            "handshake" => new HandshakeResult(
                KernelSchemas.HandshakeResult,
                "3.0.0-foundation.1",
                KernelSchemas.WireRequest,
                _application.Catalog.Document.CatalogId,
                _application.Catalog.Sha256,
                "Kernel.CompatibilityGateway"),
            "query" => await _application.HandleQueryAsync(
                request.Payload.Deserialize<QueryEnvelope>(KernelJson.Options)
                    ?? throw new KernelContractException("query_invalid", "Query payload deserialized to null."),
                cancellationToken).ConfigureAwait(false),
            "command" => await _application.HandleCommandAsync(
                request.Payload.Deserialize<CommandEnvelope>(KernelJson.Options)
                    ?? throw new KernelContractException("command_invalid", "Command payload deserialized to null."),
                cancellationToken).ConfigureAwait(false),
            _ => throw new KernelContractException("message_type_unsupported", $"Unsupported current message type '{request.MessageType}'.")
        };

        return JsonSerializer.Serialize(
            new WireResponseEnvelope(
                KernelSchemas.WireResponse,
                correlationId,
                request.MessageType + "-result",
                Ok: true,
                JsonSerializer.SerializeToElement(payload, KernelJson.Options),
                Error: null),
            KernelJson.Options);
    }

    private async Task<string> HandleLegacyAsync(JsonElement root, string correlationId, CancellationToken cancellationToken)
    {
        var protocolVersion = GetRequiredString(root, "protocolVersion");
        if (!LegacyEnvelopeVersions.Contains(protocolVersion))
        {
            throw new KernelContractException("legacy_protocol_unsupported", $"Unknown legacy protocolVersion '{protocolVersion}'.");
        }

        var messageType = GetRequiredString(root, "messageType");
        if (protocolVersion != "1.6" || messageType != "module-action")
        {
            throw new KernelContractException(
                "legacy_adapter_not_implemented",
                $"The foundation recognizes legacy protocol {protocolVersion}, but only the effect-free content.settings.describe slice is converted in Phase 1.");
        }

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            throw new KernelContractException("legacy_payload_invalid", "Legacy module-action payload must be an object.");
        }

        var schemaVersion = GetRequiredString(payload, "schemaVersion");
        var actionId = GetRequiredString(payload, "actionId");
        var operation = GetRequiredString(payload, "operation");
        var payloadRef = GetRequiredString(payload, "payloadRef");
        var idempotencyKey = GetRequiredString(payload, "idempotencyKey");
        if (schemaVersion != "knowledge-clipper-module-action.v5" || operation != "content.settings.describe")
        {
            throw new KernelContractException("legacy_payload_unsupported", "Legacy Phase 1 conversion requires module-action.v5 content.settings.describe.");
        }

        RequireIdentifier(actionId, "actionId");
        var expectedPayloadRef = $"module-actions/{actionId}/input.json";
        if (!string.Equals(payloadRef, expectedPayloadRef, StringComparison.Ordinal))
        {
            throw new KernelContractException("legacy_payload_ref_invalid", "Legacy payloadRef is not the canonical action input path.");
        }

        var inputPath = ResolveLegacyPath(payloadRef);
        var inputBytes = await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false);
        using var inputJson = StrictJson.Parse(inputBytes, inputPath);
        var operationId = "legacy-" + actionId;
        RequireIdentifier(operationId, "operationId");
        var command = new CommandEnvelope(
            KernelSchemas.Command,
            actionId,
            operationId,
            operation,
            new KernelSubject("legacy-module-action", actionId),
            idempotencyKey,
            ExpectedRevision: null,
            Authorization: null,
            Inputs: Array.Empty<ArtifactReference>(),
            new ContractBody("kc.content-settings-describe-command/1", inputJson.RootElement.Clone()));
        var receipt = await _application.HandleCommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (!receipt.Accepted)
        {
            throw new KernelContractException("legacy_operation_failed", "Converted legacy operation did not complete.");
        }

        var completed = receipt.Events.Single(item => item.EventType == "operation.completed");
        var result = completed.Data.GetProperty("result");
        var outputRef = $"module-actions/{actionId}/result.json";
        var outputPath = ResolveLegacyPath(outputRef);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(result, KernelJson.Options);
        await File.WriteAllBytesAsync(outputPath, outputBytes, cancellationToken).ConfigureAwait(false);
        var inputSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant();
        var outputSha256 = Convert.ToHexString(SHA256.HashData(outputBytes)).ToLowerInvariant();

        var legacyReceipt = new
        {
            schemaVersion = "knowledge-clipper-module-action-receipt.v5",
            actionId,
            operation,
            accepted = true,
            status = "completed",
            inputRef = payloadRef,
            inputSha256,
            outputRef,
            outputSha256,
            externalWriteCount = 0,
            physicalAudio = false,
            automaticRetryAllowed = false,
            idempotencyKey,
            completedAt = DateTimeOffset.UtcNow,
            executionStatus = "completed",
            error = (object?)null,
            outputRefs = new[]
            {
                new
                {
                    referenceType = "file",
                    kind = "content-settings-description",
                    contractVersion = "1.0.0",
                    rootRef = "jobs-root",
                    path = outputRef,
                    expectedSha256 = outputSha256
                }
            }
        };

        return JsonSerializer.Serialize(new
        {
            protocolVersion,
            correlationId,
            messageType = "module-action-receipt",
            ok = true,
            payload = legacyReceipt,
            error = (object?)null
        }, KernelJson.Options);
    }

    private string ResolveLegacyPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\') || relativePath.Contains(':'))
        {
            throw new KernelContractException("legacy_path_invalid", "Legacy path must be a forward-slash relative path.");
        }

        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new KernelContractException("legacy_path_invalid", "Legacy path is not canonical.");
        }

        var path = Path.GetFullPath(Path.Combine(_legacyDataRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var boundary = _legacyDataRoot.EndsWith(Path.DirectorySeparatorChar) ? _legacyDataRoot : _legacyDataRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            throw new KernelContractException("legacy_path_escape", "Legacy path escapes the configured data root.");
        }

        return path;
    }

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new KernelContractException("legacy_property_missing", $"Legacy property '{name}' is required.");
        }

        return property.GetString()!;
    }

    private static void RequireIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            throw new KernelContractException("legacy_identifier_invalid", $"Legacy {name} is invalid.");
        }
    }
}
