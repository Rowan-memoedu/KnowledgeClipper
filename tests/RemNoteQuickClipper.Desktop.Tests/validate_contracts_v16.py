import hashlib,json,pathlib,sys
ROOT=pathlib.Path(__file__).resolve().parents[2];CUR=ROOT/'contracts'/'desktop-automation'/'v1.6';PREV=ROOT/'contracts'/'desktop-automation'/'v1.5'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
 c=json.loads((CUR/'desktop-automation.contract.json').read_text(encoding='utf-8'));p=json.loads((PREV/'contract-receipt.json').read_text(encoding='utf-8'))
 assert c['contractVersion']=='1.6.0' and c['extends']['receiptSha256']==sha(PREV/'contract-receipt.json') and c['extends']['treeSha256']==p['treeSha256']
 expected={'flashcard-preview.cards.observe':'knowledge-clipper.desktop.flashcard-preview.cards','flashcard-preview.front.observe':'knowledge-clipper.desktop.flashcard-preview.front','flashcard-preview.back.observe':'knowledge-clipper.desktop.flashcard-preview.back'}
 assert {x['semanticAction']:x['automationId'] for x in c['automationIds']}==expected
 source=(ROOT/'src'/'FlashcardPreviewUi.cs').read_text(encoding='utf-8'); assert all(value in source for value in expected.values())
 receipt=json.loads((CUR/'contract-receipt.json').read_text(encoding='utf-8'));paths=sorted((x for x in CUR.rglob('*') if x.is_file() and x.name!='contract-receipt.json'),key=lambda x:x.relative_to(CUR).as_posix().encode())
 assert [x['path'] for x in receipt['files']]==[x.relative_to(CUR).as_posix() for x in paths];lines=[]
 for item,path in zip(receipt['files'],paths):
  digest=sha(path);assert item['bytes']==path.stat().st_size and item['sha256']==digest;lines.append(f"{item['path']}|{digest}\n")
 assert receipt['treeSha256']==hashlib.sha256(''.join(lines).encode()).hexdigest();print('PASS desktop automation v1.6 observationIds=3 v1.5Immutable=True sourceParity=True');return 0
if __name__=='__main__':sys.exit(main())
