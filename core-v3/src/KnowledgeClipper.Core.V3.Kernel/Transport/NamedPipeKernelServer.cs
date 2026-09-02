using System.IO.Pipes;
using System.Text;
using KnowledgeClipper.Core.V3.Kernel.CompatibilityGateway;

namespace KnowledgeClipper.Core.V3.Kernel.Transport;

internal sealed class NamedPipeKernelServer
{
    public const int MaxMessageBytes = 1024 * 1024;
    private readonly string _pipeName;
    private readonly KernelCompatibilityGateway _gateway;

    public NamedPipeKernelServer(string pipeName, KernelCompatibilityGateway gateway)
    {
        _pipeName = pipeName;
        _gateway = gateway;
    }

    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        var handlers = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                handlers.RemoveAll(task => task.IsCompleted);
                handlers.Add(HandleAndDisposeAsync(server, cancellationToken));
            }
        }
        finally
        {
            if (handlers.Count > 0)
            {
                await Task.WhenAll(handlers).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAndDisposeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            try
            {
                var request = await ReadFrameAsync(server, cancellationToken).ConfigureAwait(false);
                var response = await _gateway.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                var responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                await server.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or InvalidDataException ||
                ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                // A malformed/disconnected client is isolated to its connection.
            }
        }
    }

    private static async Task<string> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Named-pipe request ended before the LF delimiter.");
            }

            if (single[0] == (byte)'\n')
            {
                break;
            }

            if (buffer.Length >= MaxMessageBytes)
            {
                throw new InvalidDataException($"Named-pipe request exceeds {MaxMessageBytes} bytes.");
            }

            buffer.WriteByte(single[0]);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw new InvalidDataException("UTF-8 BOM is not allowed.");
        }

        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            Array.Resize(ref bytes, bytes.Length - 1);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
    }
}
