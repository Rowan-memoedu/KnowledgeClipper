import React, { createContext, memo, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AssistantRuntimeProvider, ThreadPrimitive, MessagePrimitive, useExternalStoreRuntime, useAuiState, type ThreadMessageLike } from '@assistant-ui/react';
import { StreamdownTextPrimitive } from '@assistant-ui/react-streamdown';
import { createMathPlugin } from '@streamdown/math';
import { cjk } from '@streamdown/cjk';
import {createLazyMermaidPlugin} from './lazy-mermaid';
import { Streamdown } from 'streamdown';
import 'katex/dist/katex.min.css';
import './style.css';
import {copySelection,pasteImages,type CopyMath} from './clipboard';
import {SupplementalRequirements,composeRequirements,type RequirementOption} from './SupplementalRequirements';
import {ActionButton,send,receiveCommandAck,usePendingActions,setCommandConversation} from './commands';

type Part = {kind: string; text?: string; displayText?:string; copyMath?:CopyMath[]; image?: {sha256:string}};
type Message = {id:string;role:'user'|'assistant';parts:Part[];status:string;typeId:string;action?:string;createdAt:string;canFork?:boolean};
type Card = {id:string;typeId:string;front:string;back:string;annotation?:string;annotationEnabled?:boolean;frontAnnotation?:string;frontAnnotationEnabled?:boolean;revision:number;frontSources:string[];backSources:string[];manuallyEdited:boolean;sourcesChanged:boolean;exportDraftId?:string;canUndo?:boolean;canRedo?:boolean};
type Proposal = {id:string;action:string;targetSide?:'front'|'back';status:string;rawOutput:string;error?:string;cards:Card[]};
type Model = {id:string;name:string;efforts:string[];inputModalities:string[];defaultEffort?:string};
type Auxiliary = {provider:string;model:string;effort?:string;models:Model[];loading:boolean;busy:boolean;error?:string;needsReconciliation:boolean};
type State = {draftId:string;conversationId?:string;tabs?:{id:string;title:string;busy:boolean;forking?:boolean;needsReconciliation:boolean}[];
  sourceMessages?:{id:string;title:string;conversation:string}[];busy:boolean;error?:string;status:string;provider:string;model:string;effort?:string;typeId:string;input:string;
  includeExamples:boolean;useQuestionTypeDefaults?:boolean;messages:Message[];total:number;hasSent?:boolean;models:Model[];types:{id:string;label:string;modelHint?:string}[];
  attachments:{sha256:string}[];cards:Card[];proposals:Proposal[];exportStatus:Record<string,string>;needsReconciliation:boolean;
  conversationBusy?:boolean;auxiliary?:Auxiliary;cardPreview?:{cardId:string;revision:number;front:string;back:string}};
declare global { interface Window { chrome?: {webview:{postMessage:(v:unknown)=>void;addEventListener:(n:string,f:(e:{data:unknown})=>void)=>void;removeEventListener:(n:string,f:(e:{data:unknown})=>void)=>void}} } }
const reportUiError = (error:unknown) => send('uiError',{message:error instanceof Error?error.message:String(error),stack:error instanceof Error?error.stack:''});
window.addEventListener('error',event=>reportUiError(event.error??event.message));
window.addEventListener('unhandledrejection',event=>reportUiError(event.reason));
class ChatErrorBoundary extends React.Component<React.PropsWithChildren, {failed:boolean}> {
  state={failed:false};
  static getDerivedStateFromError(){return {failed:true};}
  componentDidCatch(error:Error){reportUiError(error);}
  render(){return this.state.failed?<main className="conversation" role="alert"><h1>问答界面暂时无法显示</h1><p>对话和输入保留在本机。重新加载后可继续查看；不会重新发送问题。</p><ActionButton onClick={()=>send('reloadView')}>重新加载界面</ActionButton></main>:this.props.children;}
}
const imageUrl = (sha:string) => `https://clipper-media.local/${sha}.png`;
const math = createMathPlugin({singleDollarTextMath:true});
const mermaid=createLazyMermaidPlugin({config:{securityLevel:'strict',suppressErrorRendering:true}});
const markdownControls={code:false,table:false,image:false,mermaid:{download:false,copy:false,fullscreen:false,panZoom:false}};
const Context = createContext<{state:State;card?:Card}>({state:null!});
const empty:State = {draftId:'',busy:false,status:'加载问答…',provider:'api',model:'',typeId:'fast-answer',input:'',includeExamples:false,
 messages:[],total:0,models:[],types:[],attachments:[],cards:[],proposals:[],exportStatus:{},needsReconciliation:false};

// Runtime identity and host ownership are stable; there is no cloud history adapter.
const convert = (m:Message):ThreadMessageLike => ({id:m.id,role:m.role,createdAt:new Date(m.createdAt),
  content:m.parts.filter(p=>p.kind==='text'||p.kind==='image').map(p=>p.kind==='image'
    ? {type:'image' as const,image:imageUrl(p.image!.sha256)} : {type:'text' as const,text:p.displayText??p.text??''})});
const PreviewMarkdown = ({text}:{text:string}) => <Streamdown mode="static" plugins={{math,cjk,mermaid}} controls={markdownControls}
  remarkRehypeOptions={{allowDangerousHtml:false}} urlTransform={url=>/^https:\/\/clipper-media\.local\/[a-f0-9]{64}\.png$/.test(url)?url:''}
  components={{a:({children}:any)=><span>{children}</span>,img:({src,alt}:any)=>src?.startsWith('https://clipper-media.local/')?<img src={src} alt={alt||'卡片图片'}/>:null}}>{text}</Streamdown>;
const Markdown = () => <StreamdownTextPrimitive plugins={{math,cjk,mermaid}} controls={markdownControls}
  components={{a:({children}:any)=><span>{children}</span>,img:()=>null}}
  remarkRehypeOptions={{allowDangerousHtml:false}} />;
const MessageView = memo(function MessageView(){
  const {state,card}=useContext(Context); const id=useAuiState(s=>s.message.id);
  const source=state.messages.find(m=>m.id===id);
  // ExternalStoreRuntime creates an optimistic assistant item before the host
  // receives a native response. It has no persisted source or card actions.
  if(!source)return <MessagePrimitive.Root className="message assistant" aria-busy={state.busy}>
    <div className="message-meta"><strong>回答</strong><span className="pending">正在准备回答…</span></div>
  </MessagePrimitive.Root>;
  return <MessagePrimitive.Root className={`message ${source.role}`}>
    <div className="message-meta"><strong>{source.role==='user'?'我':'回答'}</strong>
      <span>{state.types.find(t=>t.id===source.typeId)?.label}{source.action?' · 辅助操作':''}</span>
      {source.status!=='completed'&&<span className="pending">{source.status==='incomplete'&&state.busy?'生成中':source.status==='interrupted'?'已停止 · 部分内容':source.status==='unknown'?'待核对':'未完成'}</span>}
    </div>
    <div className="message-body" data-message-id={source.id}><MessagePrimitive.Parts components={{Text:Markdown,Image:({image}:any)=><img src={image} loading="lazy" alt="对话图片"/>}}/></div>
    {!source.action&&card&&<div className="message-actions">{(['front','back'] as const).map(side=>{
      const selected=(side==='front'?card.frontSources:card.backSources).includes(id);
      return <ActionButton key={side} aria-pressed={selected} disabled={!!card.exportDraftId}
       onClick={()=>send('select',{cardId:card.id,messageId:id,side,selected:!selected})}>{selected?'已选入':'加入'}{side==='front'?'正面':'背面'}</ActionButton>;
    })}<ActionButton onClick={()=>send('roundCard',{messageId:id})}>按轮制卡</ActionButton><ActionButton onClick={()=>send('copyMessage',{messageId:id})}>复制整条</ActionButton>
      <ActionButton className="fork-action" disabled={state.busy||source.canFork===false}
        title={source.canFork===false?'请从本轮最后一条已完成回答分叉':source.role==='user'?'在新 Tab 修改并重新发送这个问题':'保留此前对话，在新 Tab 从这里继续'}
        onClick={()=>send('fork',{messageId:id})}>分叉</ActionButton></div>}
    {source.action&&<div className="message-actions"><ActionButton onClick={()=>send('copyMessage',{messageId:id})}>复制整条</ActionButton></div>}
  </MessagePrimitive.Root>;
});

function CardEditor({card,state}:{card:Card;state:State}){
  const historyPending=usePendingActions().history.has(card.id);
  const [front,setFront]=useState(card.front),[back,setBack]=useState(card.back),[type,setType]=useState(card.typeId);
  const [requirement,setRequirement]=useState('');
  const [requirementSelection,setRequirementSelection]=useState<RequirementOption[]>([]);
  const requirements=composeRequirements(requirement,requirementSelection);
  const [annotation,setAnnotation]=useState(card.annotation||'');
  const [frontAnnotation,setFrontAnnotation]=useState(card.frontAnnotation||'');
  const previewElement=useRef<HTMLElement>(null);
  const preview=state.cardPreview?.cardId===card.id&&state.cardPreview.revision===card.revision?state.cardPreview:undefined;
  useEffect(()=>{if(preview)previewElement.current?.scrollIntoView({block:'nearest'});},[preview?.cardId,preview?.revision]);
  useEffect(()=>{setFront(card.front);setBack(card.back);setType(card.typeId);},[card.id,card.front,card.back,card.typeId]);
  useEffect(()=>{setAnnotation(card.annotation||'');},[card.id,card.annotation]);
  useEffect(()=>{setFrontAnnotation(card.frontAnnotation||'');},[card.id,card.frontAnnotation]);
  const editFace=(side:'front'|'back',text:string)=>{if(side==='front')setFront(text);else setBack(text);send('editCard',{cardId:card.id,[side]:text});};
  const frozen=!!card.exportDraftId;
  const auxiliary=state.auxiliary;
  const auxiliaryModel=auxiliary?.models.find(m=>m.id===auxiliary.model);
  const auxiliaryUnavailable=state.busy||frozen||!auxiliaryModel||!!auxiliary?.loading||!!auxiliary?.needsReconciliation;
  const sources=(side:'front'|'back')=><ol className="sources">{(side==='front'?card.frontSources:card.backSources).map((id,i,all)=><li key={id}>
    <span>{state.sourceMessages?.find(m=>m.id===id)?.conversation&&<small className="source-tab">{state.sourceMessages.find(m=>m.id===id)!.conversation} · </small>}
      {state.sourceMessages?.find(m=>m.id===id)?.title||state.messages.find(m=>m.id===id)?.parts.find(p=>p.kind==='text')?.text?.slice(0,32)||'消息 / 图片'}</span>
    <ActionButton aria-label="上移" disabled={frozen||i===0} onClick={()=>send('move',{cardId:card.id,side,index:i,offset:-1})}>↑</ActionButton>
    <ActionButton aria-label="下移" disabled={frozen||i===all.length-1} onClick={()=>send('move',{cardId:card.id,side,index:i,offset:1})}>↓</ActionButton>
    <ActionButton aria-label="移除" disabled={frozen} onClick={()=>send('select',{cardId:card.id,messageId:id,side,selected:false})}>移除</ActionButton>
  </li>)}</ol>;
  return <div className="card-editor" onKeyDownCapture={e=>{
    if(!(e.ctrlKey||e.metaKey)||e.altKey||e.nativeEvent.isComposing)return;
    const key=e.key.toLowerCase();const redo=key==='y'&&!e.shiftKey||key==='z'&&e.shiftKey;
    if(key!=='z'&&!redo)return;
    e.preventDefault();e.stopPropagation();
    if(!frozen&&!historyPending&&(redo?card.canRedo:card.canUndo))send(redo?'redo':'undo',{cardId:card.id});
  }}>
    <label>主要提问类型<select value={type} disabled={frozen} onChange={e=>{setType(e.target.value);send('editCard',{cardId:card.id,typeId:e.target.value});}}>{state.types.map(t=><option key={t.id} value={t.id}>{t.label}</option>)}</select></label>
    {card.sourcesChanged&&<p className="notice">来源列表已调整，手工正文未删改。可继续编辑正文，或用“全部书面化”整理当前卡片。</p>}
    {(['front','back'] as const).map(side=><section key={side} className="face"><div className="face-title"><h2>{side==='front'?'正面 · 问题':'背面 · 解答'}</h2>
      <div className="face-actions">
        <ActionButton disabled={auxiliaryUnavailable||!(front.trim()||back.trim()||state.messages.length)} aria-busy={auxiliary?.busy&&state.proposals.some(p=>p.action===(side==='front'?'formalize':'formalize-answer')&&p.status==='pending')||false} onClick={()=>send('compose',{cardId:card.id,action:side==='front'?'formalize':'formalize-answer',requirements})}>{side==='front'?'生成书面化问题':'生成书面化答案'}</ActionButton>
        <ActionButton disabled={frozen} aria-expanded={side==='front'?card.frontAnnotationEnabled||!!card.frontAnnotation:card.annotationEnabled||!!card.annotation} onClick={()=>send('editCard',{cardId:card.id,[side==='front'?'frontAnnotationEnabled':'annotationEnabled']:true})}>添加批注</ActionButton>
        <ActionButton disabled={frozen} onClick={()=>send('cardImage',{cardId:card.id,side})}>补充图片</ActionButton>
        <ActionButton disabled={auxiliaryUnavailable||!/(asset:|https:\/\/clipper-media\.local\/)/.test(side==='front'?front+'\n'+frontAnnotation:back+'\n'+annotation)} onClick={()=>send('compose',{cardId:card.id,action:'transcribe',side,requirements})}>图转文字</ActionButton></div></div>{sources(side)}
      <textarea aria-label={side==='front'?'卡片正面':'卡片背面'} disabled={frozen} value={side==='front'?front:back}
        onPaste={e=>pasteImages(e,{cardId:card.id,side},send)}
        onChange={e=>editFace(side,e.target.value)} placeholder="可直接编辑 Markdown、公式与图片引用"/>
      {(side==='front'?(card.frontAnnotationEnabled||!!card.frontAnnotation):(card.annotationEnabled||!!card.annotation))&&<div className="annotation"><label>批注<textarea aria-label={side==='front'?'卡片正面批注':'卡片批注'} disabled={frozen} value={side==='front'?frontAnnotation:annotation}
        onPaste={e=>pasteImages(e,{cardId:card.id,side:side==='front'?'frontAnnotation':'annotation'},send)}
        onChange={e=>{if(side==='front')setFrontAnnotation(e.target.value);else setAnnotation(e.target.value);send('editCard',{cardId:card.id,[side==='front'?'frontAnnotation':'annotation']:e.target.value});}} placeholder="可输入批注、Markdown、公式与图片"/></label>
        <div className="annotation-tools"><ActionButton disabled={auxiliaryUnavailable||!(side==='front'?frontAnnotation:annotation).trim()} onClick={()=>send('compose',{cardId:card.id,action:'formalize-annotation',side,requirements})}>书面化</ActionButton></div></div>}
    </section>)}
    <div className="tools card-tools"><ActionButton disabled={auxiliaryUnavailable||!(front.trim()||back.trim()||frontAnnotation.trim()||annotation.trim()||state.messages.length)} aria-busy={auxiliary?.busy&&state.proposals.some(p=>p.action==='formalize-all'&&p.status==='pending')||false} onClick={()=>send('compose',{cardId:card.id,action:'formalize-all',requirements})}>全部书面化</ActionButton>
      <ActionButton disabled={!card.canUndo||historyPending} title="Ctrl+Z" onClick={()=>send('undo',{cardId:card.id})}>撤销上次编辑</ActionButton>
      <ActionButton disabled={!card.canRedo||historyPending} title="Ctrl+Y" onClick={()=>send('redo',{cardId:card.id})}>重做上次编辑</ActionButton>
      {frozen&&<ActionButton onClick={()=>send('copyCard',{cardId:card.id})}>编辑为新草稿</ActionButton>}
      <ActionButton className="preview-action" onClick={()=>send('preview',{cardId:card.id})}>预览卡片</ActionButton></div>
    <SupplementalRequirements text={requirement} onTextChange={setRequirement} selected={requirementSelection} onSelectionChange={setRequirementSelection} disabled={state.busy}/>
    <div className="auxiliary-panel">
      <div className="tools auxiliary-actions"><ActionButton disabled={auxiliaryUnavailable} onClick={()=>send('compose',{cardId:card.id,action:'integrate',requirements})}>整合所选对话</ActionButton></div>
      <div className="auxiliary-settings" aria-label="辅助操作设置" aria-busy={auxiliary?.loading||auxiliary?.busy||false}
        data-state={auxiliary?.error?'error':auxiliary?.loading?'loading':auxiliaryModel?'success':'default'}>
        <label>服务<select aria-label="辅助服务" value={auxiliary?.provider||'api'} disabled={state.busy} onChange={e=>send('auxiliarySettings',{provider:e.target.value})}>
          <option value="api">API</option><option value="codex">Codex</option></select></label>
        <label>模型<select aria-label="辅助模型" title={auxiliaryModel?.name} value={auxiliary?.model||''} disabled={state.busy||auxiliary?.loading} onChange={e=>send('auxiliarySettings',{model:e.target.value})}>
          <option value="">{auxiliary?.loading?'加载中…':'选择模型'}</option>{auxiliary?.models.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}</select></label>
        <label>推理<select aria-label="辅助推理" value={auxiliary?.effort||''} disabled={state.busy||!auxiliaryModel?.efforts.length} onChange={e=>send('auxiliarySettings',{effort:e.target.value})}>
          <option value="">{auxiliaryModel?.defaultEffort?`默认 (${auxiliaryModel.defaultEffort})`:'默认'}</option>{auxiliaryModel?.efforts.map(e=><option key={e}>{e}</option>)}</select></label>
      </div>
      {auxiliary?.error&&<p className="error" role="alert">{auxiliary.error}</p>}
      {!auxiliary?.loading&&!auxiliaryModel&&<ActionButton className="auxiliary-retry" disabled={state.busy} onClick={()=>send('auxiliaryModels')}>重新加载辅助模型</ActionButton>}
      {auxiliary?.needsReconciliation&&auxiliary.provider==='codex'&&<ActionButton disabled={state.busy} onClick={()=>send('recoverAuxiliary')}>核对原辅助回合</ActionButton>}
    </div>
    {preview&&<section ref={previewElement} className="card-preview" aria-label="卡片预览"><h2>卡片预览</h2><h3>正面</h3><div className="message-body"><PreviewMarkdown text={preview.front}/></div><h3>背面</h3><div className="message-body"><PreviewMarkdown text={preview.back}/></div></section>}
    <div className="export"><span role="status">{state.exportStatus[card.id]||'尚未导出'}</span>
      <ActionButton className="primary" disabled={state.busy} onClick={()=>send('export',{cardId:card.id,destination:'RemNote'})}>确认并导入 RemNote</ActionButton>
      <ActionButton disabled={state.busy} onClick={()=>send('export',{cardId:card.id,destination:'Anki'})}>确认并导入 Anki</ActionButton></div>
  </div>;
}

function ConversationRuntime({state,children}:{state:State;children:React.ReactNode}){
  const onNew=useCallback(async()=>send('send'),[]),onCancel=useCallback(async()=>send('stop'),[]);
  const runtime=useExternalStoreRuntime({messages:state.messages,convertMessage:convert,isRunning:state.conversationBusy??state.busy,onNew,onCancel});
  return <AssistantRuntimeProvider runtime={runtime}>{children}</AssistantRuntimeProvider>;
}
function App(){
  const [hostState,setState]=useState<State>(empty),[input,setInput]=useState(''),[active,setActive]=useState('');
  const pending=usePendingActions();
  const state=useMemo(()=>({...hostState,busy:hostState.busy||pending.busy,status:pending.busy&&!hostState.busy?'正在处理…':hostState.status}),[hostState,pending.busy]);
  const latestState=useRef(state);latestState.current=state;
  const focusTab=useRef(false);
  useEffect(()=>{if(focusTab.current){document.getElementById(`conversation-${state.conversationId||'main'}`)?.focus({preventScroll:true});focusTab.current=false;}},[state.conversationId]);
  useEffect(()=>{
    const copy=(event:ClipboardEvent)=>{
      const selection=window.getSelection();const root=document.querySelector<HTMLElement>('.viewport');
      if(!selection?.rangeCount||selection.isCollapsed||!root)return;
      const range=selection.getRangeAt(0);
      if(![...root.querySelectorAll('.message-body[data-message-id]')].some(body=>range.intersectsNode(body)))return;
      event.preventDefault();
      try{const text=copySelection(range,latestState.current.messages,root);event.clipboardData?.setData('text/plain',text);event.clipboardData?.setData('text/markdown',text);}
      catch(error){send('copySelectionError',{message:error instanceof Error?error.message:String(error)});}
    };
    document.addEventListener('copy',copy);return()=>document.removeEventListener('copy',copy);
  },[]);
  useEffect(()=>{const receive=(e:{data:unknown})=>{
    receiveCommandAck(e.data);
    const next=e.data as State;if(!next.draftId)return;
    setCommandConversation(next.conversationId||'main');
    setState(previous=>{
      // Only changed items acquire a new object; this keeps long Markdown messages mounted.
      const byId=new Map(previous.messages.map(m=>[m.id,m]));
      next.messages=next.messages.map(m=>{const old=byId.get(m.id);return old&&JSON.stringify(old)===JSON.stringify(m)?old:m;});
      if(previous.draftId!==next.draftId||previous.conversationId!==next.conversationId||previous.input!==next.input)setInput(next.input);
      return next;
    });
  };window.chrome?.webview.addEventListener('message',receive);send('ready');
    return()=>window.chrome?.webview.removeEventListener('message',receive);},[]);
  const card=state.cards.find(c=>c.id===active)||state.cards.at(-1);
  const currentModel=state.models.find(m=>m.id===state.model);
  const canSend=!state.busy&&!!currentModel&&!state.needsReconciliation&&!!(input.trim()||state.attachments.length);
  const context=useMemo(()=>({state,card}),[state,card]);
  return <Context.Provider value={context}>
    <header><div><h1>问答与制卡</h1><p>从一段对话，留下一个清楚的问题。</p></div><ActionButton onClick={()=>send('new')}>新建对话</ActionButton></header>
    <div className="workspace"><ConversationRuntime key={state.conversationId||state.draftId} state={state}><main className="conversation">
      <div className="conversation-tabs"><div role="tablist" aria-label="对话分叉">{(state.tabs||[{id:'main',title:'原对话',busy:false,needsReconciliation:false}]).map(tab=>
        <ActionButton role="tab" key={tab.id} id={`conversation-${tab.id}`} aria-selected={tab.id===(state.conversationId||'main')}
          data-state={tab.needsReconciliation?'error':tab.busy?'loading':tab.id===(state.conversationId||'main')?'success':'default'}
          aria-controls="conversation-panel" tabIndex={tab.id===(state.conversationId||'main')?0:-1} title={tab.title}
          onKeyDown={event=>{const tabs=state.tabs||[];const index=tabs.findIndex(t=>t.id===tab.id);const next=event.key==='ArrowRight'?(index+1)%tabs.length:event.key==='ArrowLeft'?(index+tabs.length-1)%tabs.length:event.key==='Home'?0:event.key==='End'?tabs.length-1:-1;
            if(next>=0){event.preventDefault();focusTab.current=true;send('switchConversation',{id:tabs[next].id});}}}
          onClick={()=>send('switchConversation',{id:tab.id})}>{tab.title}{tab.busy?(tab.forking?' · 分叉中':' · 回复中'):tab.needsReconciliation?' · 待核对':''}</ActionButton>)}</div>
        <ActionButton className="new-conversation-tab" aria-label="新建对话 Tab" title="新建空白对话" onClick={()=>send('new')}>＋</ActionButton></div>
      <div id="conversation-panel" className="conversation-panel" role="tabpanel" aria-labelledby={`conversation-${state.conversationId||'main'}`}>
      <div className="selectors"><label>服务<select value={state.provider} disabled={state.busy||state.hasSent} onChange={e=>send('settings',{provider:e.target.value})}>
        <option value="api">API</option><option value="codex">Codex · ChatGPT 登录</option></select></label>
        <label>模型<select value={state.model} disabled={state.busy||(state.useQuestionTypeDefaults&&state.provider==='codex')} onChange={e=>send('settings',{model:e.target.value})}><option value="">选择模型</option>{state.models.map(m=><option key={m.id} value={m.id}>{m.name}</option>)}</select></label>
        <label>推理<select value={state.effort||''} disabled={state.busy||!currentModel?.efforts.length||(state.useQuestionTypeDefaults&&state.provider==='codex')} onChange={e=>send('settings',{effort:e.target.value})}><option value="">默认</option>{currentModel?.efforts.map(e=><option key={e}>{e}</option>)}</select></label>
        <label>问题类型<select value={state.typeId} disabled={state.busy} onChange={e=>send('settings',{typeId:e.target.value})}>{state.types.map(t=><option key={t.id} value={t.id}>{t.label}</option>)}</select></label>
      </div>
      <div className="rule-tools"><ActionButton onClick={()=>send('models')}>刷新模型</ActionButton><ActionButton onClick={()=>send('openRules',{kind:'type'})}>编辑类型 MD</ActionButton><ActionButton onClick={()=>send('openRules',{kind:'profile'})}>编辑个人背景</ActionButton><ActionButton onClick={()=>send('openRules',{kind:'registry'})}>类型设置</ActionButton><ActionButton onClick={()=>send('snapshot')}>本次生效规则</ActionButton>
        <label className="check"><input type="checkbox" checked={state.includeExamples} disabled={state.busy} onChange={e=>send('settings',{includeExamples:e.target.checked})}/>选用当前类型示例</label></div>
      <ThreadPrimitive.Root className="thread"><ThreadPrimitive.Viewport className="viewport">
        {state.total>state.messages.length&&<ActionButton className="earlier" onClick={()=>send('older')}>加载更早的消息（还有 {state.total-state.messages.length} 条）</ActionButton>}
        {state.total===0&&<div className="empty"><h2>从文字或图片开始</h2><p>选择服务、模型和问题类型。回答后，可把多条消息选入卡片的正面或背面。</p></div>}
        <ThreadPrimitive.Messages components={{UserMessage:MessageView,AssistantMessage:MessageView}}/>
      </ThreadPrimitive.Viewport></ThreadPrimitive.Root>
      <div className="composer"><div className="attachments">{state.attachments.map(a=><figure key={a.sha256}><img src={imageUrl(a.sha256)} alt="待发送图片"/><ActionButton onClick={()=>send('removeImage',{sha256:a.sha256})}>移除</ActionButton></figure>)}</div>
        <textarea aria-label="问题" value={input} onPaste={e=>pasteImages(e,{},send)} onChange={e=>{setInput(e.target.value);send('input',{text:e.target.value});}} disabled={state.busy} placeholder="输入问题，也可以只发图片" onKeyDown={e=>{if(e.ctrlKey&&e.key==='Enter'){e.preventDefault();if(canSend)send('send',{input});}}}/>
        <div className="composer-actions"><ActionButton disabled={state.busy} onClick={()=>send('attach')}>添加图片</ActionButton><ActionButton disabled={state.busy} onClick={()=>send('pasteImage')}>粘贴图片</ActionButton><span role="status">{state.status}</span>
          {state.needsReconciliation&&!state.busy&&<ActionButton onClick={()=>send('recover')}>核对原回合</ActionButton>}
          {hostState.busy?<ActionButton onClick={()=>send('stop')}>停止</ActionButton>:<ActionButton className="primary" disabled={!canSend} onClick={()=>send('send',{input})}>发送</ActionButton>}
        </div>{state.error&&<p className="error" role="alert">{state.error}</p>}
      </div>
      </div></main></ConversationRuntime><aside className="cards"><div className="cards-heading"><h2>卡片编辑</h2><ActionButton onClick={()=>send('addCard')}>新卡片</ActionButton></div>
      <nav aria-label="卡片">{state.cards.map((c,i)=><ActionButton key={c.id} aria-pressed={card?.id===c.id} onClick={()=>setActive(c.id)}>卡片 {i+1}{c.exportDraftId?' · 已确认':''}</ActionButton>)}</nav>
      {card&&<CardEditor key={card.id} card={card} state={state}/>}
    </aside></div>
  </Context.Provider>;
}
createRoot(document.getElementById('root')!).render(<ChatErrorBoundary><App/></ChatErrorBoundary>);
