using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;

namespace KnowledgeClipper.Desktop.Core
{
    internal interface ICommonIpcClient
    {
        CommonTaskCommandReceiptV1 SendCommand(CommonTaskCommandV1 command);
        CommonTaskQueryResultV1 Query(CommonTaskQueryV1 query);
        CommonStageControlReceiptV1 SendStageControl(CommonStageControlRequestV1 request);
        CommonModuleActionReceiptV1 SendModuleAction(CommonModuleActionRequestV1 request);
        CommonModuleActionReceiptV2 SendModuleActionV2(CommonModuleActionRequestV2 request);
        CommonModuleActionReceiptV3 SendModuleActionV3(CommonModuleActionRequestV3 request);
        CommonModuleActionReceiptV4 SendModuleActionV4(CommonModuleActionRequestV4 request);
        CommonModuleActionReceiptV5 SendModuleActionV5(CommonModuleActionRequestV5 request);
        CommonModuleActionReceiptV6 SendModuleActionV6(CommonModuleActionRequestV6 request);
    }

    internal sealed class CommonIpcException : Exception
    {
        internal CommonIpcException(string code, string message, Exception inner = null) : base(message, inner) { Code = code; }
        internal string Code { get; private set; }
    }

    internal sealed class NamedPipeCommonIpcClient : ICommonIpcClient
    {
        internal const string ProtocolVersion = "1.0";
        // Stable v1 legacy compatibility alias. Do not use as a new product identity.
        internal const string DefaultPipeName = "RemNoteQuickClipper.Orchestrator.v1";
        internal const int MaximumMessageBytes = 1048576;

        private readonly string pipeName;
        private readonly int timeoutMilliseconds;
        private readonly Func<string> correlationIds;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumMessageBytes };

        internal NamedPipeCommonIpcClient(string pipeName = DefaultPipeName, int timeoutMilliseconds = 5000, Func<string> correlationIds = null)
        {
            if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipeName");
            if (timeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            this.pipeName = pipeName;
            this.timeoutMilliseconds = timeoutMilliseconds;
            this.correlationIds = correlationIds ?? delegate { return "desktop-correlation-" + Guid.NewGuid().ToString("N"); };
        }

        public CommonTaskCommandReceiptV1 SendCommand(CommonTaskCommandV1 command)
        {
            if (command == null) throw new ArgumentNullException("command");
            return Exchange<CommonTaskCommandReceiptV1>("task-command", command.ToWireObject(), "task-command-receipt");
        }

        public CommonTaskQueryResultV1 Query(CommonTaskQueryV1 query)
        {
            if (query == null) throw new ArgumentNullException("query");
            return Exchange<CommonTaskQueryResultV1>("task-query", query.ToWireObject(), "task-query-result");
        }

        public CommonStageControlReceiptV1 SendStageControl(CommonStageControlRequestV1 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonStageControlReceiptV1>("1.1", "stage-control", request.ToWireObject(), "stage-control-receipt");
        }

        public CommonModuleActionReceiptV1 SendModuleAction(CommonModuleActionRequestV1 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonModuleActionReceiptV1>("1.2", "module-action", request.ToWireObject(), "module-action-receipt");
        }

        public CommonModuleActionReceiptV2 SendModuleActionV2(CommonModuleActionRequestV2 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonModuleActionReceiptV2>("1.3", "module-action", request.ToWireObject(), "module-action-receipt");
        }

        public CommonModuleActionReceiptV3 SendModuleActionV3(CommonModuleActionRequestV3 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonModuleActionReceiptV3>("1.4", "module-action", request.ToWireObject(), "module-action-receipt");
        }

        public CommonModuleActionReceiptV4 SendModuleActionV4(CommonModuleActionRequestV4 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonModuleActionReceiptV4>("1.5", "module-action", request.ToWireObject(), "module-action-receipt");
        }

        public CommonModuleActionReceiptV5 SendModuleActionV5(CommonModuleActionRequestV5 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonModuleActionReceiptV5>("1.6", "module-action", request.ToWireObject(), "module-action-receipt");
        }

        public CommonModuleActionReceiptV6 SendModuleActionV6(CommonModuleActionRequestV6 request)
        {
            if (request == null) throw new ArgumentNullException("request");
            return Exchange<CommonModuleActionReceiptV6>("1.7", "module-action", request.ToWireObject(), "module-action-receipt");
        }

        private T Exchange<T>(string messageType, IDictionary<string, object> payload, string expectedResponseType)
        {
            return Exchange<T>(ProtocolVersion, messageType, payload, expectedResponseType);
        }

        private T Exchange<T>(string protocolVersion, string messageType, IDictionary<string, object> payload, string expectedResponseType)
        {
            var correlationId = correlationIds();
            if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128) throw new CommonIpcException("ipc.invalid-correlation-id", "The correlation ID is invalid.");
            var request = new Dictionary<string, object>
            {
                { "protocolVersion", protocolVersion },
                { "correlationId", correlationId },
                { "messageType", messageType },
                { "payload", payload }
            };
            var requestBytes = new UTF8Encoding(false, true).GetBytes(serializer.Serialize(request));
            if (requestBytes.Length > MaximumMessageBytes) throw new CommonIpcException("ipc.frame-too-large", "The request exceeds the frozen IPC byte limit.");

            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(timeoutMilliseconds);
                    pipe.Write(requestBytes, 0, requestBytes.Length);
                    pipe.WriteByte((byte)'\n');
                    pipe.Flush();
                    var responseBytes = ReadFrame(pipe);
                    var response = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(responseBytes)) as IDictionary<string, object>;
                    if (response == null) throw new CommonIpcException("ipc.invalid-envelope", "The IPC response is not an object.");
                    var responseProtocol = GetString(response, "protocolVersion");
                    var responseCorrelation = GetString(response, "correlationId");
                    var responseType = GetString(response, "messageType");
                    var ok = response.ContainsKey("ok") && Convert.ToBoolean(response["ok"]);
                    if (responseProtocol != protocolVersion) throw new CommonIpcException("ipc.unsupported-protocol", "The IPC response protocolVersion does not match the request.");
                    if (responseCorrelation != correlationId) throw new CommonIpcException("ipc.correlation-mismatch", "The IPC response correlationId does not match the request.");
                    if (!ok || responseType == "protocol-error") throw ProtocolError(response);
                    if (responseType != expectedResponseType) throw new CommonIpcException("ipc.response-type-mismatch", "The IPC response messageType does not match the request.");
                    if (!response.ContainsKey("payload") || response["payload"] == null) throw new CommonIpcException("ipc.response-payload-missing", "The IPC response payload is missing.");
                    return serializer.ConvertToType<T>(response["payload"]);
                }
            }
            catch (CommonIpcException) { throw; }
            catch (TimeoutException ex) { throw new CommonIpcException("ipc.timeout", "The local orchestrator Named Pipe timed out.", ex); }
            catch (IOException ex) { throw new CommonIpcException("ipc.unavailable", "The local orchestrator Named Pipe is unavailable.", ex); }
        }

        private byte[] ReadFrame(Stream stream)
        {
            using (var payload = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var asyncRead = stream.BeginRead(buffer, 0, buffer.Length, null, null);
                    if (!asyncRead.AsyncWaitHandle.WaitOne(timeoutMilliseconds)) throw new TimeoutException("IPC response timed out.");
                    var count = stream.EndRead(asyncRead);
                    asyncRead.AsyncWaitHandle.Close();
                    if (count == 0) throw new CommonIpcException("ipc.incomplete-frame", "The server closed before the LF delimiter.");
                    var delimiter = Array.IndexOf(buffer, (byte)'\n', 0, count);
                    var copyCount = delimiter < 0 ? count : delimiter;
                    payload.Write(buffer, 0, copyCount);
                    if (payload.Length > MaximumMessageBytes + 1L) throw new CommonIpcException("ipc.frame-too-large", "The response exceeds the frozen IPC byte limit.");
                    if (delimiter >= 0) break;
                }
                var bytes = payload.ToArray();
                if (bytes.Length > 0 && bytes[bytes.Length - 1] == (byte)'\r') Array.Resize(ref bytes, bytes.Length - 1);
                if (bytes.Length == 0) throw new CommonIpcException("ipc.empty-frame", "The IPC response frame is empty.");
                if (bytes.Length > MaximumMessageBytes) throw new CommonIpcException("ipc.frame-too-large", "The response exceeds the frozen IPC byte limit.");
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) throw new CommonIpcException("ipc.bom-forbidden", "UTF-8 BOM is forbidden by the frozen IPC contract.");
                return bytes;
            }
        }

        private static string GetString(IDictionary<string, object> value, string key)
        {
            object raw;
            return value.TryGetValue(key, out raw) && raw != null ? Convert.ToString(raw) : null;
        }

        private static CommonIpcException ProtocolError(IDictionary<string, object> response)
        {
            object raw;
            var error = response.TryGetValue("error", out raw) ? raw as IDictionary<string, object> : null;
            var code = error == null ? "ipc.protocol-error" : GetString(error, "code");
            var message = error == null ? "The orchestrator rejected the IPC request." : GetString(error, "message");
            return new CommonIpcException(string.IsNullOrWhiteSpace(code) ? "ipc.protocol-error" : code, string.IsNullOrWhiteSpace(message) ? "The orchestrator rejected the IPC request." : message);
        }
    }
}
