using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        // Offline real Form host only. Never composes Desktop/Core or invokes providers.
        private static int PreviewLifecycleCanary(string root, bool host)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("immutable-preview-evidence-exists");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var invoked = 0;
            using var form = new FlashcardPreviewForm(
                () => { invoked++; return Task.CompletedTask; },
                () => { invoked++; return Task.CompletedTask; },
                _ => { invoked++; return Task.CompletedTask; });
            var oldDraft = new FlashcardDraftRevision("fixture-old-operation", "not-exposed-old", "1", new string('a', 64), FlashcardDraftSource.ReadAloud,
                new[] { new FlashcardDraftCard("old-1", "1", new string('c', 64), "Old front one", "Old back one"), new FlashcardDraftCard("old-2", "1", new string('d', 64), "Old front two", "Old back two") }, new[] { "Anki" });
            var newDraft = oldDraft with { OperationId = "fixture-new-operation", DraftToken = "not-exposed-new", Revision = "2", Sha256 = new string('b', 64),
                Cards = new[] { new FlashcardDraftCard("new-1", "1", new string('e', 64), "New front one", "New back one"), new FlashcardDraftCard("new-2", "1", new string('f', 64), "New front two", "New back two") } };
            var replacement = newDraft with { Revision = "3", Sha256 = new string('c', 64), Cards = newDraft.Cards.Select(c => c with { Front = "Replacement " + c.Front }).ToArray() };
            FlashcardDraftPreviewState State(FlashcardDraftRevision draft, FlashcardDraftStatus status = FlashcardDraftStatus.AwaitingConfirmation) => new(status, draft, 1, "offline-fixture", true, false);
            var list = Find<ListBox>(form, FlashcardPreviewAutomation.Cards);
            var front = Find<TextBox>(form, FlashcardPreviewAutomation.Front);
            form.Render(State(oldDraft));
            form.Show(); Pump(30);
            void Write(string name, object value) => File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            string Identity() => list.AccessibleDescription;
            if (host)
            {
                // Runner writes ordered, append-only render-NNNN.json commands with a
                // fixture key. UI content is fixed here, never supplied by an oracle.
                Write("ready.json", new { pid = Environment.ProcessId, hwnd = form.Handle.ToInt64(), identityAutomationId = FlashcardPreviewAutomation.Cards, identityProperty = "HelpText", oldIdentity = JsonSerializer.Deserialize<JsonElement>(Identity()), fixtureKeys = new[] { "old", "new", "new-refresh", "new-status", "replacement", "empty" } });
                var sequence = 1;
                var started = DateTime.UtcNow;
                using var timer = new System.Windows.Forms.Timer { Interval = 20 };
                timer.Tick += delegate
                {
                    if (File.Exists(Path.Combine(root, "stop")) || (DateTime.UtcNow - started).TotalSeconds > 120) { form.Close(); return; }
                    var file = Path.Combine(root, "render-" + sequence.ToString("D4") + ".json");
                    if (!File.Exists(file)) return;
                    try
                    {
                        using var command = JsonDocument.Parse(File.ReadAllText(file));
                        var key = command.RootElement.GetProperty("fixture").GetString();
                        switch (key)
                        {
                            case "old": form.Render(State(oldDraft)); break;
                            case "new": form.Render(State(newDraft)); break;
                            case "new-refresh": form.Render(State(newDraft with { Cards = newDraft.Cards.ToArray() })); break;
                            case "new-status": form.Render(State(newDraft, FlashcardDraftStatus.Completed)); break;
                            case "replacement": form.Render(State(replacement)); break;
                            case "empty": form.Render(FlashcardDraftPreviewState.Empty); break;
                            default: throw new InvalidOperationException("unknown-offline-fixture");
                        }
                        Write("rendered-" + sequence.ToString("D4") + ".json", new { fixture = key, identity = JsonSerializer.Deserialize<JsonElement>(Identity()) });
                        sequence++;
                    }
                    catch (Exception ex) { Write("host-failure.json", new { code = ex.GetType().Name }); form.Close(); }
                };
                timer.Start();
                Application.Run(form);
                Write("host-exit.json", new { commands = sequence - 1, businessInvocations = invoked });
                return File.Exists(Path.Combine(root, "host-failure.json")) ? 1 : 0;
            }
            try
            {
                var firstItem = list.Items[0]; var secondItem = list.Items[1];
                var oldIdentity = Identity(); list.SelectedIndex = 1;
                form.Render(State(oldDraft with { Cards = oldDraft.Cards.ToArray() }, FlashcardDraftStatus.Completed));
                Assert(ReferenceEquals(firstItem, list.Items[0]) && ReferenceEquals(secondItem, list.Items[1]), "same content status update retains real ListItems");
                Assert(list.SelectedIndex == 1 && front.Text == "Old front two", "status-only render preserves selected card and visible face");
                using (var identity = JsonDocument.Parse(Identity()))
                {
                    Assert(identity.RootElement.GetProperty("renderGeneration").GetInt64() == 1, "status-only render does not change content generation");
                    Assert(!identity.RootElement.GetProperty("ready").GetBoolean(), "completed draft is not confirm-ready");
                }
                Assert(!Identity().Contains("fixture-new-operation"), "old visible draft cannot claim pending new operation");
                form.Render(State(newDraft));
                Assert(!ReferenceEquals(secondItem, list.Items[1]), "new draft replaces the old item references retained by a reader");
                Assert(list.SelectedIndex == 0 && front.Text == "New front one", "new operation resets selection and renders its own card");
                using (var identity = JsonDocument.Parse(Identity()))
                {
                    Assert(identity.RootElement.GetProperty("operationId").GetString() == newDraft.OperationId && identity.RootElement.GetProperty("draftSha256").GetString() == newDraft.Sha256 && identity.RootElement.GetProperty("revision").GetString() == "2", "published identity matches actual rendered revision");
                    Assert(identity.RootElement.GetProperty("renderGeneration").GetInt64() == 2 && identity.RootElement.GetProperty("ready").GetBoolean(), "new draft identity is published after cards and confirmation state");
                }
                Assert(!Identity().Contains("not-exposed") && !Identity().Contains("New front"), "identity surface excludes tokens and card bodies");
                var before = Identity(); form.Render(State(replacement));
                Assert(Identity() != before && front.Text == "Replacement New front one", "mid-read revision replacement changes UI identity and visible content");
                var sameHashChangedBody = replacement with { Cards = replacement.Cards.Select(c => c with { Back = "Changed body" }).ToArray() };
                var previous = Identity(); form.Render(State(sameHashChangedBody));
                Assert(Identity() != previous, "content change under unchanged upstream identity still advances render generation");
                form.Render(FlashcardDraftPreviewState.Empty);
                Assert(list.Items.Count == 0 && front.Text == "" && Identity().Contains("\"ready\":false"), "never-ready empty view cannot impersonate a draft");
                // Real UIA is queried off the owning UI thread; verify the exact seam.
                form.Render(State(newDraft));
                var handle = list.Handle;
                var uia = Task.Run(() => System.Windows.Automation.AutomationElement.FromHandle(handle).Current.HelpText);
                WaitUntil(() => uia.IsCompleted, 5000, "preview identity real UIA lookup");
                Write("uia-identity.json", new { observed = uia.GetAwaiter().GetResult(), expected = Identity() });
                Assert(uia.GetAwaiter().GetResult() == Identity(), "actual UIA HelpText returns the rendered identity");
                Assert(invoked == 0, "lifecycle regression invokes no business commands");
                Write("receipt.json", new { status = "PASS", assertions, oldIdentity = JsonSerializer.Deserialize<JsonElement>(oldIdentity), finalIdentity = JsonSerializer.Deserialize<JsonElement>(Identity()), businessInvocations = invoked, externalCalls = 0 });
                Console.WriteLine("WINDOWS_PREVIEW_LIFECYCLE PASS assertions=" + assertions);
                return 0;
            }
            catch (Exception ex) { Write("failure.json", new { status = "FAIL", assertions, error = ex.ToString() }); Console.Error.WriteLine(ex); return 1; }
            finally { form.Close(); }
        }
    }
}
