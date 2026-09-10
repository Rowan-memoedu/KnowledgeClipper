using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using System.Drawing;

internal static class Program
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly desktop = null!;
    static int assertions;
    static object New(string name, params object?[] args) => Activator.CreateInstance(desktop.GetType(name, true)!, Instance, null, args, null)!;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
    static void Pump() { for(int i=0;i<10;i++){Application.DoEvents();Thread.Sleep(5);} }
    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [STAThread]
    static int Main(string[] args)
    {
        var root=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);
        // Match the executable's application base so runtime-owned contract lookup
        // reads the sealed candidate, never the runner or source checkout.
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",root+Path.DirectorySeparatorChar);
        if(Directory.Exists(output)) throw new Exception("append-only-smoke-output-exists");
        Directory.CreateDirectory(output);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            var path=Path.Combine(root,name.Name+".dll");
            return File.Exists(path)?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;
        };
        desktop=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root,"KnowledgeClipper.dll"));
        Check(Sha(desktop.Location)=="825A2B8716111907E83BECD949BFD858CE1C636AF9F51531D876785CF21E9120", "actual sealed Desktop DLL loaded");
        var config=New("KnowledgeClipper.AppConfig");
        var data=Path.Combine(output,"isolated-data");Directory.CreateDirectory(data);
        config.GetType().GetProperty("WorkDirectory")!.SetValue(config,data);
        var capture=New("KnowledgeClipper.Desktop.Core.FakeCaptureProvider",new object?[]{null});
        var screen=New("KnowledgeClipper.Desktop.Core.FakeScreenshotProvider",new object?[]{null});
        var orchestrator=New("KnowledgeClipper.Desktop.Core.FakeDesktopOrchestratorPort");
        var controller=New("KnowledgeClipper.Desktop.Core.DesktopController",capture,screen,orchestrator,
            New("KnowledgeClipper.Desktop.Core.FakeClock",DateTime.UtcNow),New("KnowledgeClipper.Desktop.Core.FakeIdProvider"));
        Application.EnableVisualStyles();
        using var form=(Form)New("KnowledgeClipper.BoundClipperForm",config,controller,false);
        form.Show();Pump();
        Control Find(string id)=>form.Controls.Find(id,true).Single();
        var context=(Button)Find("knowledge-clipper.desktop.mode.context");
        var original=(Button)Find("knowledge-clipper.desktop.mode.original-image");
        var input=(TextBox)Find("knowledge-clipper.desktop.question.inline-input");
        Check(context.Visible && original.Visible && input.Visible,"mode keys and separate input remain visible");
        var previous=context.Text;context.PerformClick();Pump();
        Check(context.Text!=previous,"context toggle is bound");context.PerformClick();Pump();
        Check(context.Text==previous,"context toggle is reversible");
        previous=original.Text;original.PerformClick();Pump();
        Check(original.Text!=previous,"original toggle is bound");original.PerformClick();Pump();
        Check(original.Text==previous,"original toggle is reversible");
        form.GetType().GetMethod("RestoreDefaultSize",Instance)!.Invoke(form,null);Pump();
        var font=context.Font.Size;
        form.ClientSize=new Size(688,210);Pump();
        Check(form.ClientSize==new Size(688,420) && Math.Abs(context.Font.Size/font-2)<0.02,"whole-window zoom survives integrated payload");
        input.Text="isolated smoke input";
        typeof(Control).GetMethod("OnKeyDown",Instance)!.Invoke(input,new object[]{new KeyEventArgs(Keys.Escape)});Pump();
        Check(input.Text=="","Escape clears inline input");
        var questions=(System.Collections.ICollection)orchestrator.GetType().GetField("Questions",Instance)!.GetValue(orchestrator)!;
        var bundles=(System.Collections.ICollection)orchestrator.GetType().GetField("SubmittedBundles",Instance)!.GetValue(orchestrator)!;
        Check(questions.Count==0 && bundles.Count==0,"cancel emits no question or capture command");
        using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,form.ClientRectangle);bmp.Save(Path.Combine(output,"integrated-smoke.png"));}
        File.WriteAllText(Path.Combine(output,"receipt.json"),JsonSerializer.Serialize(new {
            status="PASS-INJECTED-SEALED-DESKTOP-SMOKE",assertions,candidate=root,desktopDllSha256=Sha(desktop.Location),
            productionDllLoaded=true,process="external reflection runner",capture="Fake",orchestrator="Fake",
            realKeyboardMouse=0,externalCalls=0,speaker=0,userHandTestEntrypoint=false
        },new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
}
