"""Exercise the production helper's SDK calls with an isolated SDK double; no external requests."""
import importlib.util
import json
from pathlib import Path
import sys
import threading
from types import SimpleNamespace as NS
import unittest

module_spec = importlib.util.spec_from_file_location("clipper_native", Path(__file__).parents[2] / "helpers/codex_chat.py")
native = importlib.util.module_from_spec(module_spec)
module_spec.loader.exec_module(native)
output = Path(sys.argv[1])
output.mkdir(parents=True, exist_ok=True)
sys.argv = sys.argv[:1]

class Channel:
    def __init__(self):
        self.cancel = threading.Event()
        self.events = []
        self.client = self.thread_id = self.turn_id = None
    def emit(self, kind, **data): self.events.append({"kind": kind, **data})
    def interrupt(self): self.client.turn_interrupt(self.thread_id, self.turn_id)

class Client:
    def __init__(self):
        self.calls = []
        self.conflict = self.lost_injection = False
        self.account_type = "chatgpt"
        self.status = "completed"
        self.source_status = "completed"
        self.events = [NS(method="item/completed", payload={"item": {"id": "compact", "type": "contextCompaction"}}),
                       NS(method="turn/completed", payload={"turn": {"id": "fixture-turn", "status": "completed"}})]
    def __enter__(self): return self
    def __exit__(self, *args): return False
    def initialize(self): self.calls.append("initialize")
    def account_read(self): return {"account": {"type": self.account_type}}
    def model_list(self): return {"data": [{"model": "fixture", "inputModalities": ["text", "image"], "supportedReasoningEfforts": [{"reasoningEffort": "low"}]}]}
    def thread_start(self, params):
        assert params["ephemeral"] is False
        self.calls.append("thread/start")
        return NS(thread=NS(id="fixture-thread"))
    def thread_resume(self, thread, params):
        self.calls.append("thread/resume")
        if self.conflict: raise RuntimeError("writer occupied")
    def request(self, method, payload, **kwargs):
        self.calls.append(method)
        assert payload["items"][0]["role"] == "developer"
        if self.lost_injection and self.calls.count(method) == 2: raise RuntimeError("lost compaction reinjection receipt")
    def turn_start(self, thread, inputs, params):
        self.calls.append("turn/start")
        return NS(turn=NS(id="fixture-turn"))
    def next_turn_notification(self, turn):
        event = self.events.pop(0)
        self.calls.append(event.method)
        return event
    def unregister_turn_notifications(self, turn): self.calls.append("unregister")
    def thread_read(self, thread, include_turns):
        self.calls.append("thread/read")
        if thread=="source": return {"thread":{"turns":[{"id":"past","status":self.source_status},{"id":"future","status":"completed"}]}}
        if thread=="fork-thread": return {"thread":{"turns":[{"id":"past","status":self.source_status}]}}
        return {"thread": {"turns": [{"id": "fixture-turn", "items": [], "status": self.status}]}}
    def thread_fork(self, thread, params):
        assert thread=="source" and params=={"lastTurnId":"past","excludeTurns":True}
        self.calls.append("thread/fork")
        return NS(thread=NS(id="fork-thread"))

class NativeContract(unittest.TestCase):
    def test_interrupted_terminal_turn_can_fork_without_regeneration(self):
        self.client.source_status="interrupted"
        self.request.update(action="fork",sourceThreadId="source",lastTurnId="past")
        native.run(self.request,self.channel)
        self.assertEqual(self.client.calls,["initialize","thread/read","thread/fork","thread/read"])
    def test_in_progress_turn_is_not_a_fork_boundary(self):
        self.client.source_status="inProgress"
        self.request.update(action="fork",sourceThreadId="source",lastTurnId="past")
        with self.assertRaisesRegex(RuntimeError,"尚未创建"):native.run(self.request,self.channel)
        self.assertNotIn("thread/fork",self.client.calls)
    def test_native_fork_copies_only_the_exact_completed_prefix_without_generation(self):
        self.request.update(action="fork",sourceThreadId="source",lastTurnId="past")
        native.run(self.request,self.channel)
        self.assertEqual(self.client.calls,["initialize","thread/read","thread/fork","thread/read"])
        self.assertEqual([e["kind"] for e in self.channel.events],["stage","forked","forkVerified"])
        self.assertTrue(self.channel.events[1]["durable"])
    def test_fork_recovery_only_reads_saved_identity(self):
        self.request.update(action="verifyFork",sourceThreadId="source",lastTurnId="past",threadId="fork-thread")
        native.run(self.request,self.channel)
        self.assertEqual(self.client.calls,["initialize","thread/read","thread/read"])
        self.assertEqual(self.channel.events[-1]["kind"],"forkVerified")
    def test_bad_fork_boundary_rejected_before_mutation(self):
        self.request.update(action="fork",sourceThreadId="source",lastTurnId="absent")
        with self.assertRaisesRegex(RuntimeError,"尚未创建"):native.run(self.request,self.channel)
        self.assertNotIn("thread/fork",self.client.calls)
    def setUp(self):
        self.client = Client()
        native.CodexClient = lambda config: self.client
        native.resolve_runtime = lambda request: "fixture-codex.exe"
        self.channel = Channel()
        self.request = {"action": "send", "cwd": str(output / "workspace"), "mediaRoot": str(output / "media"),
                        "model": "fixture", "instructions": "current rules", "input": "public problem"}
    def test_compaction_reinjects_before_terminal_receipt(self):
        native.run(self.request, self.channel)
        self.assertEqual(self.client.calls.count("thread/inject_items"), 2)
        self.assertEqual(self.client.calls.count("turn/start"), 1)
        self.assertGreater(len(self.client.calls)-1-self.client.calls[::-1].index("thread/inject_items"), self.client.calls.index("turn/completed"))
        kinds = [e["kind"] for e in self.channel.events]
        self.assertLess(kinds.index("rulesReinjected"), kinds.index("completed"))
    def test_repeated_compaction_events_are_coalesced_without_mid_turn_injection(self):
        self.client.events.insert(1, self.client.events[0])
        self.client.events.insert(2, NS(method="item/completed", payload={"item":{"id":"compact-two","type":"contextCompaction"}}))
        native.run(self.request, self.channel)
        self.assertEqual(self.client.calls.count("thread/inject_items"), 2)
        self.assertEqual(len([e for e in self.channel.events if e["kind"] == "compactionCompleted"]), 2)
        self.assertEqual(next(e for e in self.channel.events if e["kind"] == "rulesReinjected")["itemIds"], ["compact", "compact-two"])
        self.assertGreater(len(self.client.calls)-1-self.client.calls[::-1].index("thread/inject_items"), self.client.calls.index("turn/completed"))
    def test_compaction_lost_receipt_retains_identity_without_replay(self):
        self.client.lost_injection = True
        with self.assertRaisesRegex(RuntimeError, "lost compaction"): native.run(self.request, self.channel)
        self.assertEqual((self.channel.thread_id, self.channel.turn_id), ("fixture-thread", "fixture-turn"))
        self.assertEqual(self.client.calls.count("turn/start"), 1)
        self.assertFalse(any(e["kind"] == "completed" for e in self.channel.events))
    def test_writer_conflict_does_not_create_replacement(self):
        self.request["threadId"] = "original-thread"
        self.client.conflict = True
        with self.assertRaisesRegex(RuntimeError, "occupied"): native.run(self.request, self.channel)
        self.assertEqual(self.channel.thread_id, "original-thread")
        self.assertNotIn("thread/start", self.client.calls)
        self.assertNotIn("turn/start", self.client.calls)
    def test_in_progress_readback_never_resends(self):
        self.request.update(action="recover", threadId="original-thread", turnId="fixture-turn")
        self.client.status = "inProgress"
        with self.assertRaisesRegex(RuntimeError, "仍在运行"): native.run(self.request, self.channel)
        self.assertEqual(self.client.calls, ["initialize", "thread/read"])
    def test_login_failure_is_detected_without_changing_auth(self):
        self.request["action"] = "models"
        self.client.account_type = "apiKey"
        with self.assertRaisesRegex(RuntimeError, "ChatGPT 登录"): native.run(self.request, self.channel)
        self.assertEqual(self.client.calls, ["initialize"])
    def test_catalog_preserves_actual_capabilities(self):
        self.request["action"] = "models"
        native.run(self.request, self.channel)
        model = self.channel.events[0]["models"][0]
        self.assertEqual(model["inputModalities"], ["text", "image"])
        self.assertEqual(model["efforts"], ["low"])

result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(NativeContract))
(output / "native-contract.json").write_text(json.dumps({"passed": result.wasSuccessful(), "tests": result.testsRun,
    "boundary": "production helper with isolated SDK double, no network; real automatic compaction timing remains separate"}), encoding="utf-8")
raise SystemExit(0 if result.wasSuccessful() else 1)
