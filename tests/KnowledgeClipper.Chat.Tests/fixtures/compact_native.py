"""Real SDK canary: lower only this child process's automatic compaction threshold."""
import importlib.util
import json
from pathlib import Path
import sys

spec=importlib.util.spec_from_file_location("clipper_native",Path(__file__).parents[3]/"helpers/codex_chat.py")
native=importlib.util.module_from_spec(spec);spec.loader.exec_module(native)
request=json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
original_config=native.CodexConfig
def scoped_config(**kwargs):
    kwargs["config_overrides"]=(*kwargs["config_overrides"],"model_auto_compact_token_limit=8000")
    return original_config(**kwargs)
native.CodexConfig=scoped_config
class EvidenceChannel(native.Channel):
    def emit(self,kind,**data):
        if kind in ("rulesReinjected", "compactionCompleted"):
            with (Path(request["cwd"])/"compactions.jsonl").open("a",encoding="utf-8") as stream:
                stream.write(json.dumps({"threadId":self.thread_id,"turnId":self.turn_id,"kind":kind,**data})+"\n")
        super().emit(kind,**data)
sys.stdin.reconfigure(encoding="utf-8");sys.stdout.reconfigure(encoding="utf-8")
channel=EvidenceChannel()
try:native.run(request,channel)
except Exception as error:
    channel.emit("error",message=str(error));sys.exit(1)
