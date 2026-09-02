using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Core.V3.Contracts;

namespace KnowledgeClipper.Core.V3.Client;

internal sealed class CoreV3ProtocolClient
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _pipeName;
    private readonly TimeSpan _requestTimeout;

    internal CoreV3ProtocolClient(string pipeName, TimeSpan requestTimeout)
    {
        _pipeName = pipeName;
        _requestTimeout = requestTimeout;
    }

    internal Task<HandshakeResult> HandshakeAsync(CancellationToken cancellationToken)
    {
        return SendAsync<HandshakeResult>("handshake", JsonSerializer.SerializeToElement(new { }, JsonOptions), cancellationToken);
    }

    internal Task<QueryResult> QueryAsync(QueryEnvelope query, CancellationToken cancellationToken)
    {
        return SendAsync<QueryResult>("query", JsonSerializer.SerializeToElement(query, JsonOptions), cancellationToken);
    }

    internal Task<CommandReceipt> CommandAsync(CommandEnvelope command, CancellationToken cancellationToken)
    {
        return SendAsync<CommandReceipt>("command", JsonSerializer.SerializeToElement(command, JsonOptions), cancellationToken);
    }

    internal async Task<string> SendCompatibilityFrameAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            var requestBytes = Encoding.UTF8.GetBytes(requestJson + "\n");
            if (requestBytes.Length - 1 > MaxMessageBytes)
            {
                throw new CoreV3ClientException("request_frame_too_large", $"Request exceeds {MaxMessageBytes} bytes.");
            }

            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(requestBytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new CoreV3ClientException("request_timeout", $"Compatibility frame did not complete within {_requestTimeout.TotalMilliseconds:0} ms.", ex);
        }
    }

    private async Task<T> SendAsync<T>(string messageType, JsonElement payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        var requestCancellation = timeout.Token;
        var correlationId = $"corr-{Guid.NewGuid():N}";
        var request = new WireRequestEnvelope(
            KernelSchemas.WireRequest,
            correlationId,
            messageType,
            payload);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions) + "\n";
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        if (requestBytes.Length - 1 > MaxMessageBytes)
        {
            throw new CoreV3ClientException("request_frame_too_large", $"Request exceeds {MaxMessageBytes} bytes.");
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(requestCancellation).ConfigureAwait(false);
            await pipe.WriteAsync(requestBytes, requestCancellation).ConfigureAwait(false);
            await pipe.FlushAsync(requestCancellation).ConfigureAwait(false);
            var responseJson = await ReadFrameAsync(pipe, requestCancellation).ConfigureAwait(false);
            ValidateStrictJson(responseJson);
            var response = JsonSerializer.Deserialize<WireResponseEnvelope>(responseJson, JsonOptions)
                ?? throw new CoreV3ClientException("response_invalid", "Core v3 response deserialized to null.");
            var expectedResponseType = messageType + "-result";
            if (response.Schema != KernelSchemas.WireResponse)
            {
                throw new CoreV3ClientException("response_schema_mismatch", "Core v3 response schema does not match the current wire contract.");
            }

            if (response.CorrelationId != correlationId)
            {
                throw new CoreV3ClientException("response_correlation_mismatch", "Core v3 response correlationId does not match the request.");
            }

            if (response.MessageType != expectedResponseType)
            {
                throw new CoreV3ClientException("response_type_mismatch", $"Expected '{expectedResponseType}' but received '{response.MessageType}'.");
            }

            if (!response.Ok || response.Payload is null)
            {
                throw new CoreV3ClientException(response.Error?.Code ?? "request_rejected", response.Error?.Message ?? "Core v3 request failed.");
            }

            return response.Payload.Value.Deserialize<T>(JsonOptions)
                ?? throw new CoreV3ClientException("response_payload_invalid", "Core v3 payload deserialized to null.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new CoreV3ClientException("request_timeout", $"Core v3 did not complete within {_requestTimeout.TotalMilliseconds:0} ms.", ex);
        }
        catch (IOException ex)
        {
            throw new CoreV3ClientException("transport_disconnected", "Core v3 named-pipe transport disconnected.", ex);
        }
    }

    private static async Task<string> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("Core v3 closed the pipe before the LF delimiter.");
            }

            if (single[0] == (byte)'\n')
            {
                break;
            }

            if (buffer.Length >= MaxMessageBytes)
            {
                throw new CoreV3ClientException("response_frame_too_large", $"Response exceeds {MaxMessageBytes} bytes.");
            }

            buffer.WriteByte(single[0]);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            Array.Resize(ref bytes, bytes.Length - 1);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw new CoreV3ClientException("response_bom_forbidden", "Core v3 response contains a forbidden UTF-8 BOM.");
        }

        if (bytes.Length == 0)
        {
            throw new CoreV3ClientException("response_frame_empty", "Core v3 returned an empty response frame.");
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void ValidateStrictJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            ValidateElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CoreV3ClientException("response_json_invalid", "Core v3 response is not strict JSON.", ex);
        }
    }

    private static void ValidateElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new CoreV3ClientException("response_duplicate_property", $"Core v3 response contains duplicate property '{property.Name}'.");
                }

                ValidateElement(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateElement(item);
            }
        }
    }
}

public sealed class CoreV3ClientException : Exception
{
    public CoreV3ClientException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
