import sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];sys.path.insert(0,str(ROOT/'tools'))
import verify_core_package_runtime_candidate as gate
C=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T215840Z-268bd5aa');S=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki-sdk\rta-language-sdk-20260903T220604Z-84224585')
class Tests(unittest.TestCase):
 def test_live_candidate(self):self.assertEqual('PASS-CORE-PACKAGE-RUNTIME-ADMISSION',gate.evaluate(C,S)['status'])
 def test_tools_are_inside_candidate(self):
  d=gate.load(C/'authority/runtime-tools.v1.json')
  for x in d['tools'].values():self.assertTrue((C/x['path']).resolve().is_relative_to(C.resolve()))
 def test_hash_drift_case_is_explicit(self):
  d=gate.load(C/'runtime-admission-self-test-receipt.json');self.assertIn('hash-drift-fails-before-operation',d['cases'])
 def test_environment_is_cleared(self):
  d=gate.load(C/'real-language-matrix-no-env-receipt.v2.json');self.assertTrue(d['runtimeAdmission']['developerEnvironmentRemoved']);self.assertTrue(d['runtimeAdmission']['pathCleared'])
if __name__=='__main__':unittest.main()
