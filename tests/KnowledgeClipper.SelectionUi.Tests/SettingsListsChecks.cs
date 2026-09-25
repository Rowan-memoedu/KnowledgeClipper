using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KnowledgeClipper.Workflow;

// Production controls and AnkiConnection, with a LOOPBACK FIXTURE server.
// This tests requests/UI projection only; it never connects to or writes real Anki.
internal static class SettingsListsChecks
{
    private static T Find<T>(Form dialog, string suffix) where T : Control =>
        (T)dialog.Controls.Find("knowledge-clipper.language." + suffix, true).Single();

    // Caller supplies a shown, disposable, unsaved dialog with isolated settings.
    // Close it after this check; do not reuse it for persistence assertions.
    internal static async Task RunAsync(Form dialog, List<string> checks)
    {
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("LOOPBACK FIXTURE: " + name);
            checks.Add("LOOPBACK FIXTURE: " + name);
            Console.WriteLine("PASS LOOPBACK FIXTURE: " + name);
        }
        await using var fixture = new AnkiListFixture();
        var endpoint = Find<TextBox>(dialog, "anki-endpoint");
        var root = Find<ComboBox>(dialog, "deck");
        var model = Find<ComboBox>(dialog, "note-type");
        var mode = Find<ComboBox>(dialog, "mode");
        var path = Find<ComboBox>(dialog, "mode-deck-path");
        var read = Find<Button>(dialog, "read-anki");
        var readModes = Find<Button>(dialog, "read-mode-decks");
        var create = Find<Button>(dialog, "create-mode-deck");
        endpoint.Text = fixture.Endpoint;
        root.Text = AnkiListFixture.Root;
        model.Text = "Unlisted user model";
        var initialPath = path.Text;
        await ClickAndWaitAsync(read, () => fixture.Requests.Count >= 2);
        Check(fixture.Requests.Select(r => r.Action).SequenceEqual(new[] { "deckNames", "modelNames" }),
            "root list load sends deckNames and modelNames through production HTTP client");
        Check(Items(root).Order().SequenceEqual(fixture.Decks.Order()) && Items(model).SequenceEqual(new[] { "Basic", "Fixture Model" }),
            "root and model controls contain the server lists");
        Check(root.Text == AnkiListFixture.Root && model.Text == "Unlisted user model" && path.Text == initialPath,
            "successful loading preserves editable root, model and mode path");
        Check(Items(path).Order().SequenceEqual(new[] { "Language", "Language::English::Reading", "Question" }.Order()),
            "mode list includes only real descendants at all depths and excludes similarly prefixed roots");
        root.Text = "Other Root";
        Check(Items(path).SequenceEqual(new[] { "Deep::Only" }) && path.Text == initialPath,
            "changing root filters the shared list without overwriting mode text");
        root.Text = AnkiListFixture.Root;

        SelectPage(mode);
        Check(mode.Items.Count >= 2, "multiple concrete modes are available for independent target checks");
        var expected = new Dictionary<string, string>();
        for (var index = 0; index < mode.Items.Count; index++)
        {
            mode.SelectedIndex = index;
            var id = ((ModeDefinition)mode.SelectedItem!).Id;
            path.Text = expected[id] = "Independent::" + id;
        }
        for (var index = 0; index < mode.Items.Count; index++)
        {
            mode.SelectedIndex = index;
            var id = ((ModeDefinition)mode.SelectedItem!).Id;
            Check(path.Text == expected[id], "switching modes preserves the separate target for " + id);
        }
        var beforeModeRefresh = fixture.Requests.Count;
        await ClickAndWaitAsync(readModes, () => fixture.Requests.Count >= beforeModeRefresh + 2);
        Check(fixture.Requests.Skip(beforeModeRefresh).Select(r => r.Action).SequenceEqual(new[] { "deckNames", "modelNames" }),
            "mode-page refresh works while the common page is hidden");
        var createdRelative = "New::Nested::Fixture";
        path.Text = createdRelative;
        var beforeCreate = fixture.Requests.Count;
        await ClickAndWaitAsync(create, () => fixture.Requests.Count >= beforeCreate + 2);
        var createRequests = fixture.Requests.Skip(beforeCreate).ToArray();
        Check(createRequests.Select(r => r.Action).SequenceEqual(new[] { "createDeck", "deckNames" }) &&
            createRequests[0].Deck == AnkiListFixture.Root + "::" + createdRelative,
            "create posts exactly root plus relative path and then reloads deckNames");
        Check(Items(root).Contains(AnkiListFixture.Root + "::" + createdRelative) && Items(path).Contains(createdRelative),
            "new nested deck becomes available in both refreshed lists");
        Check(path.Text == createdRelative && path.SelectedItem?.ToString() == createdRelative, "created relative path is selected after refresh");

        root.Text = AnkiListFixture.Root;
        path.Text = "Keep::Unsaved";
        model.Text = "Keep model";
        var oldRoots = Items(root);
        var oldPaths = Items(path);
        fixture.FailReads = true;
        var beforeFailure = fixture.Requests.Count;
        await ClickAndWaitAsync(readModes, () => fixture.Requests.Count > beforeFailure);
        Check(root.Text == AnkiListFixture.Root && path.Text == "Keep::Unsaved" && model.Text == "Keep model" &&
            Items(root).SequenceEqual(oldRoots) && Items(path).SequenceEqual(oldPaths),
            "failed list request preserves existing lists and unsaved text");
        Check(dialog.Controls.Cast<Control>().SelectMany(Descendants).OfType<Label>().Any(l => l.Text.Contains("fixture read failure", StringComparison.Ordinal)),
            "failed request remains visible as a fixture error");
        Check(fixture.Requests.All(r => r.Version == 6 && r.Action is "deckNames" or "modelNames" or "createDeck") &&
            fixture.Requests.Count(r => r.Action == "createDeck") == 1,
            "fixture receives only expected v6 list requests and one fixture deck creation; no card operations");
    }

    private static string[] Items(ComboBox control) => control.Items.Cast<object>().Select(x => x.ToString()!).ToArray();
    private static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void SelectPage(Control control)
    {
        Control? parent = control;
        while (parent != null && parent is not TabPage) parent = parent.Parent;
        if (parent is TabPage page)
        {
            ((TabControl)page.Parent!).SelectedTab = page;
            page.ScrollControlIntoView(control);
        }
    }
    private static async Task ClickAndWaitAsync(Button button, Func<bool> observed)
    {
        SelectPage(button);
        if (!button.Visible || !button.Enabled) throw new InvalidOperationException("Fixture button is not available: " + button.Name);
        button.PerformClick();
        var until = DateTime.UtcNow.AddSeconds(12);
        while ((!observed() || !button.Enabled) && DateTime.UtcNow < until) await Task.Delay(20);
        if (!observed() || !button.Enabled) throw new TimeoutException("Fixture request did not finish: " + button.Name);
    }

    private sealed record FixtureRequest(string Action, int Version, string? Deck);
    private sealed class AnkiListFixture : IAsyncDisposable
    {
        internal const string Root = "Selection UI Fixture";
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serving;
        private readonly List<string> decks = [Root, Root + "::Language", Root + "::Language::English::Reading", Root + "::Question",
            Root + " Else::Excluded", "Other Root", "Other Root::Deep::Only"];
        internal readonly ConcurrentQueue<FixtureRequest> Requests = new();
        internal volatile bool FailReads;
        internal string[] Decks { get { lock (decks) return decks.ToArray(); } }
        internal string Endpoint { get; }
        internal AnkiListFixture()
        {
            listener.Start();
            Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            serving = Task.Run(ServeAsync);
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream = client.GetStream();
                    var header = new List<byte>();
                    var one = new byte[1];
                    while (header.Count < 16384)
                    {
                        await stream.ReadExactlyAsync(one, lifetime.Token);
                        header.Add(one[0]);
                        if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    if (!lines[0].StartsWith("POST / HTTP/", StringComparison.Ordinal)) throw new InvalidDataException("Fixture expects POST /");
                    var length = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    if (length < 0 || length > 65536) throw new InvalidDataException("Fixture request is too large");
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, lifetime.Token);
                    using var json = JsonDocument.Parse(body);
                    var request = json.RootElement;
                    var action = request.GetProperty("action").GetString()!;
                    var deck = action == "createDeck" ? request.GetProperty("params").GetProperty("deck").GetString() : null;
                    Requests.Enqueue(new FixtureRequest(action, request.GetProperty("version").GetInt32(), deck));
                    object? result = null;
                    string? error = null;
                    if (FailReads && action is "deckNames" or "modelNames") error = "fixture read failure";
                    else if (action == "deckNames") result = Decks;
                    else if (action == "modelNames") result = new[] { "Basic", "Fixture Model" };
                    else if (action == "createDeck" && deck != null)
                    {
                        lock (decks) if (!decks.Contains(deck)) decks.Add(deck);
                        result = 123456L;
                    }
                    else error = "fixture rejects unexpected action: " + action;
                    var response = JsonSerializer.SerializeToUtf8Bytes(new { result, error });
                    var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(responseHeader, lifetime.Token);
                    await stream.WriteAsync(response, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            try { await serving; }
            finally { lifetime.Dispose(); }
        }
    }
}
