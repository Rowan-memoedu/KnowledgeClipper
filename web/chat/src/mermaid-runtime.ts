import mermaid from 'mermaid';

// Shared offline Anki runtime. Source remains in the card; generated SVG is only a view.
const version='11.16.1';
let serial=0;
mermaid.initialize({startOnLoad:false,securityLevel:'strict',suppressErrorRendering:true,theme:'default'});
async function run(root:ParentNode=document){
  for(const element of root.querySelectorAll<HTMLElement>('.kc-mermaid')){
    const source=element.querySelector<HTMLElement>('.kc-mermaid-source');
    if(!source||element.dataset.mermaidState==='rendering'||element.dataset.mermaidState==='complete')continue;
    element.dataset.mermaidState='rendering';
    const target=element.querySelector<HTMLElement>('.kc-mermaid-diagram')!;
    try{
      const result=await mermaid.render('kcmermaid'+Date.now()+'_'+(++serial),source.textContent||'');
      target.innerHTML=result.svg;target.setAttribute('role','img');target.setAttribute('aria-label','Mermaid 图');
      target.querySelectorAll('a').forEach(a=>{a.removeAttribute('href');a.removeAttribute('xlink:href');});
      const svg=target.querySelector('svg');if(svg){svg.style.maxWidth='100%';svg.style.height='auto';}
      element.dataset.mermaidState='complete';
      element.dispatchEvent(new CustomEvent('knowledgeclipper:mermaid',{bubbles:true,detail:{version,state:'complete'}}));
    }catch(error){
      element.dataset.mermaidState='error';target.textContent='Mermaid 语法错误；展开源码可检查。';
      element.dispatchEvent(new CustomEvent('knowledgeclipper:mermaid',{bubbles:true,detail:{version,state:'error',error:String(error)}}));
    }
  }
}
const global=window as unknown as {KnowledgeClipperMermaid?:{version:string;run:typeof run}};
if(!global.KnowledgeClipperMermaid)global.KnowledgeClipperMermaid={version,run};
void run();
