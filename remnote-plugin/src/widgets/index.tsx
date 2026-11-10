import {declareIndexPlugin, WidgetLocation, type ReactRNPlugin} from '@remnote/plugin-sdk';
import {startRuntime,WRITE_SETTING} from '../runtime';
import {MERMAID_POWERUP} from '../api/mermaid';
let stop:(()=>void)|undefined;
async function activate(plugin:ReactRNPlugin) {
 await plugin.settings.registerBooleanSetting({id:WRITE_SETTING,title:'允许剪藏导入',description:'允许本机 KnowledgeClipper 导入卡片、恢复图片及迁移已核验的导入身份。',defaultValue:true});
 await plugin.app.registerWidget('status',WidgetLocation.RightSidebar,{});
 await plugin.app.registerPowerup({name:'剪藏 Mermaid',code:MERMAID_POWERUP,description:'保留 Mermaid 源码并显示矢量图',options:{slots:[]}});
 await plugin.app.registerWidget('mermaid',WidgetLocation.UnderRemEditor,{dimensions:{height:'auto',width:'100%'},powerupFilter:MERMAID_POWERUP});
 stop?.();stop=await startRuntime(plugin);
}
async function deactivate(plugin:ReactRNPlugin){stop?.();stop=undefined;await plugin.app.unregisterWidget('status',WidgetLocation.RightSidebar);}
declareIndexPlugin(activate,deactivate);
