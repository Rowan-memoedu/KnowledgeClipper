import sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]; sys.path.insert(0,str(ROOT/'tools'))
import verify_desktop_language_preview_candidate as gate
C=Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-rta-20260903T133216Z'); E=Path(r'D:\Artifacts\KnowledgeClipper\desktop-read-to-anki-evidence\desktop-rta-20260903T133216Z'); R=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki\rta-20260903T211155Z-90c373ff\real-language-matrix-receipt.v4.json'); Q=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260903T133000Z-real-language-matrix\quality-real-language-matrix-receipt.json'); I=Path(r'D:\Artifacts\KnowledgeClipper\read-to-anki\quality\20260903T130500Z-real-user-failure\quality-real-user-failure-invalidation.json')
class Tests(unittest.TestCase):
 def test_final_candidate(self): self.assertEqual('PASS-USER-HANDTEST-CANDIDATE-BOUND',gate.evaluate(C,E,R,Q,I)['status'])
 def test_old_failure_is_still_invalid(self): self.assertEqual('NO-GO-REAL-USER-FAILURE',gate.load(I)['status'])
 def test_matrix_counts(self):
  q=gate.load(Q); self.assertEqual([1,1,2,1],[len(x['cards']) for x in q['matrix']])
if __name__=='__main__': unittest.main()
