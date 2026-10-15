"""Observe naturally rendered Mermaid in the installed Anki reviewer, in a public isolated profile only."""
import hashlib, json, os, pathlib, time, traceback
from aqt import mw, gui_hooks
from aqt.qt import QTimer, QApplication
import anki

ROOT=pathlib.Path(os.environ['KC_MERMAID_NATIVE_OUTPUT']).resolve()
assert os.environ.get('KNOWLEDGECLIPPER_ISOLATED_UI_TEST')=='1'
assert pathlib.Path(mw.pm.base).resolve()==(ROOT/'profile').resolve(), 'Never run against a user profile'
ROOT.mkdir(parents=True,exist_ok=True)
FIXTURE=json.loads((ROOT/'fixture.json').read_text(encoding='utf-8-sig'))
RESULT={'passed':False,'installedAnkiVersion':anki.version,'faces':{},'ratingCalls':0}
START=time.time()

def write():
    (ROOT/'results.json').write_text(json.dumps(RESULT,ensure_ascii=False,indent=2),encoding='utf-8')

def fail():
    RESULT['error']=traceback.format_exc();write();QTimer.singleShot(50,QApplication.instance().quit)

EXPRESSION="""(() => ({
  url:location.href,userAgent:navigator.userAgent,body:document.body.innerHTML,
  diagrams:Array.from(document.querySelectorAll('.kc-mermaid')).map(e=>({
    state:e.dataset.mermaidState,source:e.querySelector('.kc-mermaid-source')?.textContent,
    svg:e.querySelector('svg')?.outerHTML,
    width:e.querySelector('svg')?.getBoundingClientRect().width||0,
    height:e.querySelector('svg')?.getBoundingClientRect().height||0,
    canvas:getComputedStyle(e.querySelector('.kc-mermaid-diagram')).backgroundColor,
    text:e.querySelector('.kc-mermaid-diagram')?.textContent,
    nodeCount:e.querySelectorAll('svg .node').length
  })),separator:!!document.querySelector('hr#answer')
}))()"""

def inspect(face):
    RESULT['lastInspection']={'face':face,'elapsed':time.time()-START,'state':mw.state,'reviewerState':mw.reviewer.state,'domDone':mw.reviewer.web._domDone};write()
    def received(value):
        try:
            RESULT['lastDom']=value;write()
            count=1 if face=='question' else 2
            diagrams=value.get('diagrams',[]) if value else []
            if len(diagrams)!=count or any(d.get('state')!='complete' or d['height']<20 for d in diagrams):
                assert time.time()-START<80, value
                QTimer.singleShot(250,lambda:inspect(face));return
            assert '输入' in diagrams[0]['text'] and '输出' in diagrams[0]['text'], value
            assert diagrams[0]['nodeCount']==3, value
            assert all(d['canvas']=='rgb(255, 255, 255)' for d in diagrams),value
            if face=='answer':
                assert value['separator'] and '学生' in diagrams[1]['text'] and '回答' in diagrams[1]['text'],value
            for index,d in enumerate(diagrams):
                d['sourceSha256']=hashlib.sha256(d['source'].encode()).hexdigest()
                (ROOT/f'{face}-{index}.svg').write_text(d['svg'],encoding='utf-8')
            def painted():
                try:
                    mw.reviewer.web.grab().save(str(ROOT/f'{face}.png'))
                    RESULT['faces'][face]=value;write()
                    if face=='question':
                        mw.reviewer._showAnswer();QTimer.singleShot(300,lambda:inspect('answer'))
                    else:
                        assert mw.col.db.scalar('select count(*) from revlog')==0
                        RESULT['passed']=True;RESULT['revlogCount']=0;write()
                        QTimer.singleShot(100,QApplication.instance().quit)
                except Exception:fail()
            QTimer.singleShot(350,painted)
        except Exception:fail()
    mw.reviewer.web.page().runJavaScript(EXPRESSION,received)

def opened():
    gui_hooks.profile_did_open.remove(opened)
    def begin():
        try:
            assert mw.col.note_count()==0,'Fixture profile is not empty'
            model=mw.col.models.new('KnowledgeClipper Mermaid public fixture')
            for name in FIXTURE['fieldNames']:
                mw.col.models.add_field(model,mw.col.models.new_field(name))
            for name,faces in FIXTURE['templates'].items():
                template=mw.col.models.new_template(name)
                template['qfmt']=faces['Front'];template['afmt']=faces['Back']
                mw.col.models.add_template(model,template)
            model['css']=FIXTURE['styling']['css'];mw.col.models.add(model)
            deck=mw.col.decks.id('KnowledgeClipper Mermaid isolated acceptance')
            note=mw.col.new_note(model)
            for name,value in FIXTURE['fields'].items():note[name]=value
            mw.col.add_note(note,deck)
            runtime=(ROOT/'runtime.js').read_bytes()
            assert hashlib.sha256(runtime).hexdigest()==FIXTURE['runtimeSha256']
            assert mw.col.media.write_data(FIXTURE['runtimeFile'],runtime)==FIXTURE['runtimeFile']
            RESULT['fieldHashes']={name:hashlib.sha256(note[name].encode()).hexdigest() for name in FIXTURE['fieldNames']}
            RESULT['runtimeSha256']=hashlib.sha256(runtime).hexdigest()
            RESULT['noteId']=note.id;RESULT['cardIds']=[c.id for c in note.cards()]
            write();mw.col.decks.select(deck);mw.reset()
            RESULT['renderedCardQuestion']=note.cards()[0].question();write()
            def wait_for_reset():
                def ready(value):
                    if value:
                        mw.moveToState('review');QTimer.singleShot(400,lambda:inspect('question'))
                    else:
                        QTimer.singleShot(100,wait_for_reset)
                mw.web.page().runJavaScript("document.querySelector('.deck.current')?.id==="+json.dumps(str(deck)),ready)
            QTimer.singleShot(100,wait_for_reset)
        except Exception:fail()
    QTimer.singleShot(800,begin)

def watchdog():
    if not RESULT['passed']:
        RESULT['error']='Native reviewer did not finish within 90 seconds';write();QApplication.instance().quit()
write();gui_hooks.profile_did_open.append(opened);QTimer.singleShot(90000,watchdog)
