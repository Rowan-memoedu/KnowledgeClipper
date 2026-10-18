"""Read naturally rendered tables in the installed Anki reviewer; public isolated profile only."""
import hashlib,json,os,pathlib,time,traceback
from aqt import mw,gui_hooks
from aqt.qt import QTimer,QApplication
import anki
ROOT=pathlib.Path(os.environ['KC_TABLE_NATIVE_OUTPUT']).resolve()
assert os.environ.get('KNOWLEDGECLIPPER_ISOLATED_UI_TEST')=='1'
assert pathlib.Path(mw.pm.base).resolve()==(ROOT/'profile').resolve()
FIXTURE=json.loads((ROOT/'fixture.json').read_text(encoding='utf-8-sig'))
RESULT={'passed':False,'installedAnkiVersion':anki.version,'faces':{},'ratingCalls':0};START=time.time()
def write():(ROOT/'results.json').write_text(json.dumps(RESULT,ensure_ascii=False,indent=2),encoding='utf-8')
def fail():
 RESULT['error']=traceback.format_exc();write();QTimer.singleShot(50,QApplication.instance().quit)
EXPRESSION="""(()=>({body:document.body.innerHTML,separator:!!document.querySelector('hr#answer'),
 tables:[...document.querySelectorAll('table')].map(t=>({rows:t.rows.length,columns:t.rows[0].cells.length,
 text:t.innerText,width:t.getBoundingClientRect().width,height:t.getBoundingClientRect().height,
 cells:[...t.rows].flatMap(r=>[...r.cells].map(c=>({text:c.innerText,rect:c.getBoundingClientRect().toJSON(),
 math:[...c.querySelectorAll('mjx-container')].map(m=>{const rect=m.getBoundingClientRect();let right=rect.right;
 for(let p=m.parentElement;p&&p!==c;p=p.parentElement)if(getComputedStyle(p).overflowX!=='visible')right=Math.min(right,p.getBoundingClientRect().right);
 return {rect:rect.toJSON(),visibleRight:right,error:!!m.querySelector('[data-mjx-error]')};})})))
 }))}))()"""
def inspect(face):
 def received(value):
  try:
   RESULT['lastDom']=value;write();tables=value.get('tables',[]) if value else []
   if len(tables)!=(2 if face=='question' else 4) or sum(len(c['math']) for t in tables for c in t['cells'])<(2 if face=='question' else 4):
    assert time.time()-START<80,value;QTimer.singleShot(250,lambda:inspect(face));return
   assert all(t['rows']==4 and t['columns'] in [2,3] and t['width']>100 and t['height']>40 for t in tables),value
   for table in tables:
    for cell in table['cells']:
     for math in cell['math']:
      assert not math['error'],math
      assert math['visibleRight']<=cell['rect']['right']+1,'Formula overlaps the next cell'
   if face=='answer':assert value['separator']
   def painted():
    try:
     mw.reviewer.web.grab().save(str(ROOT/f'{face}.png'));RESULT['faces'][face]=value;write()
     if face=='question':mw.reviewer._showAnswer();QTimer.singleShot(350,lambda:inspect('answer'))
     else:
      assert mw.col.db.scalar('select count(*) from revlog')==0
      RESULT.update(passed=True,revlogCount=0);write();QTimer.singleShot(100,QApplication.instance().quit)
    except Exception:fail()
   QTimer.singleShot(300,painted)
  except Exception:fail()
 mw.reviewer.web.page().runJavaScript(EXPRESSION,received)
def opened():
 gui_hooks.profile_did_open.remove(opened)
 def begin():
  try:
   assert mw.col.note_count()==0
   model=mw.col.models.new('KnowledgeClipper public table fixture')
   for name in FIXTURE['fieldNames']:mw.col.models.add_field(model,mw.col.models.new_field(name))
   for name,faces in FIXTURE['templates'].items():
    template=mw.col.models.new_template(name);template['qfmt']=faces['Front'];template['afmt']=faces['Back'];mw.col.models.add_template(model,template)
   model['css']=FIXTURE['styling']['css'];mw.col.models.add(model)
   deck=mw.col.decks.id('KnowledgeClipper table isolated acceptance');note=mw.col.new_note(model)
   for name,value in FIXTURE['fields'].items():note[name]=value
   mw.col.add_note(note,deck);mw.col.decks.select(deck);mw.reset()
   RESULT['fieldHashes']={name:hashlib.sha256(note[name].encode()).hexdigest() for name in FIXTURE['fieldNames']};write()
   def wait_for_reset():
    def ready(value):
     if value:mw.moveToState('review');QTimer.singleShot(400,lambda:inspect('question'))
     else:QTimer.singleShot(100,wait_for_reset)
    mw.web.page().runJavaScript("document.querySelector('.deck.current')?.id==="+json.dumps(str(deck)),ready)
   QTimer.singleShot(100,wait_for_reset)
  except Exception:fail()
 QTimer.singleShot(800,begin)
def watchdog():
 if not RESULT['passed']:RESULT['error']='Native reviewer did not finish within 90 seconds';write();QApplication.instance().quit()
write();gui_hooks.profile_did_open.append(opened);QTimer.singleShot(90000,watchdog)
