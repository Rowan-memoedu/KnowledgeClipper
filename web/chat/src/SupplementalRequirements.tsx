import {useId} from 'react';

export const requirementOptions = [
  {id:'mermaid',label:'生成 Mermaid 图',instruction:'在完成原有任务并保留完整正文的同时，提炼关键结构、关系或步骤，在当前动作允许更新的目标字段中补充 Mermaid 图，使用 ```mermaid 代码块；图示须与正文一致，不虚构关系，不用图代替原有内容。使用可解析的 Mermaid 语法：节点 ID 仅用英文字母和数字，所有节点显示文字和边文字均用双引号包围，显示文字中的双引号写为 &quot;；含括号、方括号、花括号、下标或公式的文字也必须放在引号内，不能直接作为语法结构。流程图涉及判断时，每条出边写出明确条件，不能用含义不完整的“是/否”代替；并列情形须互斥且完备，等号、零值和边界情形须保留，不能把某一子情形当作其余全部情形。'},
  {id:'clean',label:'去除无关信息',instruction:'清除与知识内容无关的水印、题号、页码、页眉页脚、排版标记、乱码等信息；保留有效题干、条件、公式、必要符号、解答和逻辑层级，不删去有效内容或改变事实。'},
] as const;
export type RequirementOption = typeof requirementOptions[number]['id'];

export function composeRequirements(text:string,selected:readonly RequirementOption[]):string{
  const instructions=requirementOptions.filter(option=>selected.includes(option.id)).map(option=>option.instruction);
  return instructions.length?[text,...instructions,'继续遵守当前动作的目标字段与输出格式约定。'].filter(Boolean).join('\n\n'):text;
}

export function SupplementalRequirements({text,onTextChange,selected,onSelectionChange,disabled=false}:{
  text:string;onTextChange:(text:string)=>void;selected:RequirementOption[];
  onSelectionChange:(selected:RequirementOption[])=>void;disabled?:boolean;
}){
  const id=useId();
  return <div className="supplemental-requirements">
    <label htmlFor={id}>补充要求</label>
    <div className="requirements-field" data-state={disabled?'loading':'default'}>
      <textarea id={id} aria-label="补充要求" rows={2} value={text} disabled={disabled}
        onChange={event=>onTextChange(event.target.value)} placeholder="仅在主动整合、书面化或图转文字时发送"/>
      <div className="requirement-options" role="group" aria-label="补充要求选项">
        {requirementOptions.map(option=><button key={option.id} type="button" data-option={option.id}
          aria-pressed={selected.includes(option.id)} disabled={disabled} title={option.instruction}
          onClick={()=>onSelectionChange(selected.includes(option.id)?selected.filter(id=>id!==option.id):[...selected,option.id])}>
          <span aria-hidden="true" className="requirement-check">{selected.includes(option.id)?'✓':'+'}</span>{option.label}
        </button>)}
      </div>
    </div>
  </div>;
}
