using System.Net;

namespace KnowledgeClipper.Workflow;

public static class MermaidCards
{
    public const string Version = "11.16.1";
    public const string RuntimeFile = "_knowledgeclipper_mermaid_11_16_1.js";
    public static bool Contains(AnkiNoteDraft note) => note.Fields.Values.Any(value => value.Contains("class=\"kc-mermaid\"", StringComparison.Ordinal));
    // The hidden source is exact, editable card content; SVG is generated in the reviewer.
    public static string Html(string source) => "<div class=\"kc-mermaid\"><div class=\"kc-mermaid-diagram\" aria-live=\"polite\" style=\"background:#fff;color:#333;padding:8px;border-radius:4px\"></div>" +
        "<details class=\"kc-mermaid-code\"><summary>Mermaid 源码</summary><pre class=\"kc-mermaid-source\" style=\"white-space:pre;overflow:auto\">" +
        WebUtility.HtmlEncode(source) + "</pre></details></div>";
    public const string Bootstrap = "<!-- knowledgeclipper-mermaid.v1 --><script>(function(){if(!document.querySelector('.kc-mermaid'))return;" +
        "if(window.KnowledgeClipperMermaid){window.KnowledgeClipperMermaid.run();return;}" +
        "var s=document.createElement('script');s.src='" + RuntimeFile + "';s.onerror=function(){document.querySelectorAll('.kc-mermaid-diagram').forEach(function(e){e.textContent='Mermaid 渲染组件缺失；源码已保留。';});};document.head.appendChild(s);})();</script>";
}
