using System.Collections;
using System.Diagnostics;
using System.Reflection;
using KnowledgeClipper.Workflow;
using Microsoft.Web.WebView2.WinForms;

internal static partial class Program
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly List<string> checks=[],errors=[];
    static string output="",data="";
    static DraftStore store=null!;
    static object flow=null!;
    static Assembly desktop=null!;
    static ClipDraft Draft(Form form)=>(ClipDraft)form.GetType().GetProperty("Draft",Flags)!.GetValue(form)!;
    static object? Call(object target,string method,params object?[] args)=>target.GetType().GetMethod(method,Flags)!.Invoke(target,args);
    static Form[] Chats()=>((IEnumerable)flow.GetType().GetField("chatForms",Flags)!.GetValue(flow)!).Cast<Form>().Where(f=>!f.IsDisposed).ToArray();
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
    static async Task Ready(Form form)
    {
        var web=(WebView2)form.GetType().GetField("web",Flags)!.GetValue(form)!;
        var timer=Stopwatch.StartNew();
        while(web.CoreWebView2==null||await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.composer')!==null")!="true"){
            if(timer.Elapsed.TotalSeconds>40)throw new TimeoutException("Chat page did not load");await Task.Delay(30);
        }
    }
    [STAThread] static int Main(string[] args)=>args[0]=="--cover"?CoverWindow(args[1],args[2]):WindowLayeringMain(args);
}
