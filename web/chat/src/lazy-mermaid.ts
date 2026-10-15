import type {DiagramPlugin, MermaidInstance, MermaidPluginOptions, MermaidConfig} from '@streamdown/mermaid';

// Keep Streamdown's renderer and its exact options, but load the diagram engine
// only when a Mermaid block is actually rendered. Plain history needs no engine.
export function createLazyMermaidPlugin(options:MermaidPluginOptions):DiagramPlugin {
  let loaded:Promise<DiagramPlugin>|undefined;
  const load=()=>loaded??=import('@streamdown/mermaid').then(module=>module.createMermaidPlugin(options));
  let configuration:MermaidConfig|undefined;
  const instance:MermaidInstance={
    initialize(config){configuration=config;},
    async render(id,source){return (await load()).getMermaid(configuration).render(id,source);},
  };
  return {name:'mermaid',type:'diagram',language:'mermaid',getMermaid(config){
    if(config)instance.initialize(config);
    return instance;
  }};
}
