using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;
using KnowledgeClipper.Core.V3.Kernel.Capabilities;
using KnowledgeClipper.Core.V3.Kernel.Common;

namespace KnowledgeClipper.Core.V3.Kernel.Application;

internal sealed record CommandClaim(
    string IdempotencyKey,
    string FirstCommandId,
    string OperationId,
    string Operation,
    string CommandFingerprint,
    string EffectClass,
    string OwnerInstanceId,
    int OwnerProcessId,
    DateTimeOffset ClaimedAt);

internal sealed record CommandClaimResult(bool Acquired, bool OwnedByCurrentInstance, bool OwnerProcessAlive, CommandClaim Claim);

internal interface ICommandIndex
{
    CommandClaimResult TryClaim(CommandEnvelope command, string effectClass);

    CommandReceipt? TryReadReceipt(string idempotencyKey);

    void Complete(string idempotencyKey, CommandReceipt receipt);

    IReadOnlyList<CommandClaim> ReadClaims();
}

internal sealed class FileCommandIndex : ICommandIndex
{
    private readonly string _claimsRoot;
    private readonly string _receiptsRoot;
    private readonly string _ownerInstanceId;
    private readonly int _ownerProcessId;

    public FileCommandIndex(string root, string? ownerInstanceId = null, int? ownerProcessId = null)
    {
        Root = Path.GetFullPath(root);
        _ownerInstanceId = ownerInstanceId ?? $"process-{Environment.ProcessId}-{Guid.NewGuid():N}";
        _ownerProcessId = ownerProcessId ?? Environment.ProcessId;
        _claimsRoot = Path.Combine(Root, "claims");
        _receiptsRoot = Path.Combine(Root, "receipts");
        Directory.CreateDirectory(_claimsRoot);
        Directory.CreateDirectory(_receiptsRoot);
    }

    public string Root { get; }

    public CommandClaimResult TryClaim(CommandEnvelope command, string effectClass)
    {
        ValidateKey(command.IdempotencyKey);
        var claim = new CommandClaim(
            command.IdempotencyKey,
            command.CommandId,
            command.OperationId,
            command.Operation,
            CommandFingerprint.Compute(command),
            effectClass,
            _ownerInstanceId,
            _ownerProcessId,
            DateTimeOffset.UtcNow);
        var destination = ClaimPath(command.IdempotencyKey);
        if (TryPublishCreateOnce(destination, JsonSerializer.SerializeToUtf8Bytes(claim, KernelJson.Options)))
        {
            return new CommandClaimResult(true, true, true, claim);
        }

        var existing = ReadStrict<CommandClaim>(destination);
        if (existing.IdempotencyKey != claim.IdempotencyKey ||
            existing.Operation != claim.Operation ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(existing.CommandFingerprint),
                Convert.FromHexString(claim.CommandFingerprint)))
        {
            throw new KernelContractException(
                "idempotency_conflict",
                "The idempotency key is already bound to a different command identity.");
        }

        var sameOwner = existing.OwnerInstanceId == _ownerInstanceId;
        return new CommandClaimResult(false, sameOwner, sameOwner || IsProcessAlive(existing.OwnerProcessId), existing);
    }

    public CommandReceipt? TryReadReceipt(string idempotencyKey)
    {
        ValidateKey(idempotencyKey);
        var path = ReceiptPath(idempotencyKey);
        return File.Exists(path) ? ReadStrict<CommandReceipt>(path) : null;
    }

    public void Complete(string idempotencyKey, CommandReceipt receipt)
    {
        ValidateKey(idempotencyKey);
        var path = ReceiptPath(idempotencyKey);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, KernelJson.Options);
        if (TryPublishCreateOnce(path, bytes))
        {
            return;
        }

        var existingBytes = File.ReadAllBytes(path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existingBytes), SHA256.HashData(bytes)))
        {
            throw new InvalidDataException("A different durable receipt already exists for the idempotency key.");
        }
    }

    public IReadOnlyList<CommandClaim> ReadClaims()
    {
        return Directory.EnumerateFiles(_claimsRoot, "*.claim.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(ReadStrict<CommandClaim>)
            .ToArray();
    }

    private static bool TryPublishCreateOnce(string destination, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var pending = destination + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(pending, destination, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(pending))
            {
                File.Delete(pending);
            }
        }
    }

    private static T ReadStrict<T>(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var json = StrictJson.Parse(bytes, path);
        return json.RootElement.Deserialize<T>(KernelJson.Options)
            ?? throw new InvalidDataException($"Command index file '{path}' deserialized to null.");
    }

    private string ClaimPath(string key) => Path.Combine(_claimsRoot, key + ".claim.json");

    private string ReceiptPath(string key) => Path.Combine(_receiptsRoot, key + ".receipt.json");

    private static void ValidateKey(string key)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Command index key must be a 64-character hexadecimal idempotency key.");
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal static class CommandFingerprint
{
    public static string Compute(CommandEnvelope command)
    {
        var semantics = JsonSerializer.SerializeToElement(new
        {
            command.Operation,
            command.Subject,
            command.ExpectedRevision,
            command.Authorization,
            command.Inputs,
            command.Body
        }, KernelJson.Options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, semantics);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("Command fingerprint contains an unsupported JSON value.");
        }
    }
}
