using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

// Observe production HttpClient calls without changing handlers, requests or credentials.
// Only host/port/path, operation and status are retained; never headers or payloads.
internal sealed class HttpObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly Func<string> phase;
    private readonly object gate = new();
    private readonly List<IDisposable> subscriptions = [];
    private readonly Dictionary<HttpRequestMessage, HttpObservation> observations = [];
    internal HttpObserver(Func<string> phase)
    {
        this.phase = phase;
        subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "HttpHandlerDiagnosticListener")
            // .NET checks the parent activity name before emitting Start/Stop.
            subscriptions.Add(listener.Subscribe(this, key => key is "System.Net.Http.HttpRequestOut" or
                "System.Net.Http.HttpRequestOut.Start" or "System.Net.Http.HttpRequestOut.Stop"));
    }
    public void OnNext(KeyValuePair<string, object?> pair)
    {
        if (pair.Value == null) return;
        var request = pair.Value.GetType().GetProperty("Request")?.GetValue(pair.Value) as HttpRequestMessage;
        if (request?.RequestUri is not { } uri) return;
        lock (gate)
        {
            if (pair.Key.EndsWith(".Start", StringComparison.Ordinal))
            {
                var operation = uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ? "content" : uri.Segments.Last().Trim('/');
                if (uri.IsLoopback && !uri.AbsolutePath.Contains("/sdk/knowledge-clipper/", StringComparison.Ordinal) && request.Content is ByteArrayContent)
                {
                    try
                    {
                        using var body = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        if (body.RootElement.TryGetProperty("action", out var action)) operation = action.GetString() ?? operation;
                    }
                    catch (JsonException) { }
                }
                observations[request] = new() { Phase = phase(), Host = uri.Host, Port = uri.Port, Path = uri.AbsolutePath,
                    Method = request.Method.Method, Operation = operation };
            }
            else if (observations.TryGetValue(request, out var current) &&
                pair.Value.GetType().GetProperty("Response")?.GetValue(pair.Value) is HttpResponseMessage response)
                current.Status = (int)response.StatusCode;
        }
    }
    internal List<HttpObservation> Snapshot() { lock (gate) return observations.Values.ToList(); }
    public void OnError(Exception error) { }
    public void OnCompleted() { }
    public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
}
