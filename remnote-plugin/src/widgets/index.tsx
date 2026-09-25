import {declareIndexPlugin, WidgetLocation, type ReactRNPlugin} from '@remnote/plugin-sdk';
import {startRuntime,WRITE_SETTING} from '../runtime';
let stop:(()=>void)|undefined;
async function activate(plugin:ReactRNPlugin) {
 await plugin.settings.registerBooleanSetting({id:WRITE_SETTING,title:'允许剪藏导入',description:'允许本机 KnowledgeClipper 导入卡片、恢复图片及迁移已核验的导入身份。',defaultValue:true});
 await plugin.app.registerWidget('status',WidgetLocation.RightSidebar,{});
 stop?.();stop=startRuntime(plugin);
}
async function deactivate(plugin:ReactRNPlugin){stop?.();stop=undefined;await plugin.app.unregisterWidget('status',WidgetLocation.RightSidebar);}
declareIndexPlugin(activate,deactivate);
