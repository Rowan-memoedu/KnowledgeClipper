export type CopyMath={tex:string;markdown:string;block:boolean};
export type CopyMessage={id:string;parts:{kind:string;text?:string;copyMath?:CopyMath[];image?:{sha256:string}}[]};

// The browser locates the selected range. Formula content comes from the host's
// original Markdown AST, never from KaTeX's visible or accessibility text.
export function copySelection(range:Range,messages:CopyMessage[],root:HTMLElement):string {
  const pieces:string[]=[];
  for(const body of root.querySelectorAll<HTMLElement>('.message-body[data-message-id]')){
    if(!range.intersectsNode(body))continue;
    const source=messages.find(m=>m.id===body.dataset.messageId);if(!source)throw new Error('原始消息尚未就绪，请稍后复制。');
    const tokens=source.parts.flatMap(p=>p.copyMath||[]);
    const nodes=[...body.querySelectorAll<HTMLElement>('.katex')];
    const formulas=new Map<Element,CopyMath>();
    for(let i=0;i<nodes.length;i++){
      const tex=nodes[i].querySelector('annotation[encoding="application/x-tex"]')?.textContent?.trim().replace(/\r\n/g,'\n');
      if(!tokens[i]||tokens[i].tex.trim().replace(/\r\n/g,'\n')!==tex){
        if(range.intersectsNode(nodes[i]))throw new Error('选中公式尚未与原始 LaTeX 对齐，请稍后复制或使用“复制整条”。');
      }else formulas.set(nodes[i],tokens[i]);
    }
    const walk=(node:Node):string=>{
      if(!range.intersectsNode(node))return '';
      if(node.nodeType===Node.TEXT_NODE){const text=node.textContent||'';return text.slice(range.startContainer===node?range.startOffset:0,range.endContainer===node?range.endOffset:text.length);}
      if(!(node instanceof HTMLElement))return [...node.childNodes].map(walk).join('');
      if(node.classList.contains('katex'))return formulas.get(node)?.markdown||'';
      if(node.matches('button,[aria-hidden="true"]'))return '';
      if(node.tagName==='IMG'){const sha=node.getAttribute('src')?.match(/\/([a-f0-9]{64})\.png$/)?.[1];return sha?`![图片](asset:${sha})`:'';}
      if(node.tagName==='BR')return '\n';
      const text=[...node.childNodes].map(walk).join('');if(!text)return '';
      if(node.tagName==='PRE')return '\n\n```\n'+text.replace(/^`|`$/g,'')+'\n```\n\n';
      if(node.tagName==='CODE')return '`'+text+'`';
      if(node.tagName==='STRONG')return '**'+text+'**';
      if(node.tagName==='EM')return '*'+text+'*';
      if(node.tagName==='LI')return '- '+text.trim()+'\n';
      if(/^H[1-6]$/.test(node.tagName))return '\n\n'+'#'.repeat(Number(node.tagName[1]))+' '+text.trim()+'\n\n';
      if(node.tagName==='P'||node.tagName==='BLOCKQUOTE'||node.classList.contains('katex-display'))return '\n\n'+text+'\n\n';
      return text;
    };
    const text=walk(body).trim();if(text)pieces.push(text);
  }
  return pieces.join('\n\n');
}

export function pasteImages(event:ReactClipboardEvent<HTMLTextAreaElement>,target:object,send:(command:string,payload:object)=>void):void {
  const files=[...event.clipboardData.files].filter(file=>file.type.startsWith('image/'));
  if(!files.length)return;
  event.preventDefault();
  const field=event.currentTarget;
  const placement={start:field.selectionStart,end:field.selectionEnd,expectedText:field.value,clipboardText:event.clipboardData.getData('text/plain')};
  Promise.all(files.map(file=>new Promise<string>((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(String(reader.result).split(',')[1]);reader.onerror=()=>reject(reader.error);reader.readAsDataURL(file);})))
    .then(images=>send('pasteImageData',{...target,...placement,images}))
    .catch(()=>send('copySelectionError',{message:'粘贴图片读取失败，原输入已保留。'}));
}
import type {ClipboardEvent as ReactClipboardEvent} from 'react';
