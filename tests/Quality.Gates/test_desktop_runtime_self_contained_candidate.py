import sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT/'tools'))
import verify_desktop_runtime_self_contained_candidate as gate
C=Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260903T140936Z');E=Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki-evidence\desktop-rta-20260903T140936Z')
class Tests(unittest.TestCase):
 @classmethod
 def setUpClass(cls):cls.result=gate.evaluate(C,E)
 def test_candidate_is_conditional(self):
  r=self.result;self.assertFalse(r['decision']['userHandTestGo']);self.assertTrue(r['missing']['actualDesktopSemanticReadToAnkiAction'])
 def test_runtime_tools_are_packaged(self):
  self.assertEqual({'edgeTts','ffplay'},set(self.result['candidate']['runtimeTools']))
if __name__=='__main__':unittest.main()
