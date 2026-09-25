import React, {useEffect,useState} from 'react';
import {renderWidget,usePlugin} from '@remnote/plugin-sdk';
import {STATUS_KEY} from '../runtime';
function Status(){const plugin=usePlugin();const [status,setStatus]=useState('正在读取连接状态');useEffect(()=>{let active=true;const read=()=>{void plugin.storage.getSession<string>(STATUS_KEY).then(value=>{if(active)setStatus(value??'等待插件启动');}).catch(()=>{if(active)setStatus('无法读取连接状态');});};read();const timer=setInterval(read,3000);return()=>{active=false;clearInterval(timer);};},[plugin]);return <main style={{padding:20,fontFamily:'system-ui'}}><h2>KnowledgeClipper 剪藏</h2><p>{status}</p><p>剪藏、语言卡片及题目图片由桌面剪藏工具导入。</p><p>本插件独立运行，不需要 MCP Automation Bridge。</p></main>;}
renderWidget(Status);
