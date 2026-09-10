import copy,json,sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]; sys.path.insert(0,str(ROOT/'tools'))
import verify_real_language_matrix_candidate as gate
C=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T211155Z-90c373ff'); S=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki-sdk\rta-language-sdk-20260903T212316Z-90c28c7f'); R=C/'real-language-matrix-receipt.v4.json'
class Tests(unittest.TestCase):
 def test_live(self): self.assertEqual('PASS-CORE-CANDIDATE-BOUND-REAL-LANGUAGE-MATRIX',gate.evaluate(C,S)['status'])
 def test_missing_mode_detected(self):
  d=gate.load(R); d['matrix'].pop(); self.assertNotEqual(set(x['mode'] for x in d['matrix']),{'meaning-reading','writing','both'})
 def test_readback_drift_rejected(self):
  d=gate.load(R); x=copy.deepcopy(d['matrix'][0]); x['semanticReadback']['cards'][0]['front']='drift'
  with self.assertRaises(gate.GateFailure): gate.verify_scenario(x,Path(d['runtimeDataRoot']),['language_context'])
 def test_extra_anki_attempt_rejected(self):
  d=gate.load(R); x=copy.deepcopy(d['matrix'][0]); x['after']['ankiExportAttempts']+=1
  with self.assertRaises(gate.GateFailure): gate.verify_scenario(x,Path(d['runtimeDataRoot']),['language_context'])
if __name__=='__main__': unittest.main()
