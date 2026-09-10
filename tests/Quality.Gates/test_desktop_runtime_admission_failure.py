import json,sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]; sys.path.insert(0,str(ROOT/'tools'))
import verify_desktop_runtime_admission_failure as gate
F=ROOT/'tests/Quality.Gates/fixtures/desktop-runtime-admission/t133216-real-user-failure.json'
class Tests(unittest.TestCase):
 def test_real_failure_invalidates_go(self): self.assertFalse(gate.evaluate(json.loads(F.read_text(encoding='utf-8')))['userHandTestGo'])
 def test_screenshot_is_bound(self):
  d=json.loads(F.read_text(encoding='utf-8')); self.assertEqual(d['screenshot']['sha256'],gate.sha(d['screenshot']['path']))
if __name__=='__main__': unittest.main()
