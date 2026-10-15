using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] static extern int WindowStyle(IntPtr window,int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle,int index,System.Text.StringBuilder value,int bytes,out int needed);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenDesktop(string name,uint flags,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktopHandle);
    static string DesktopName()
    {
        var name=new System.Text.StringBuilder(256);
        if(!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()),2,name,512,out _))throw new Exception("Cannot inspect test desktop");
        return name.ToString();
    }
    static int WindowLayeringMain(string[] args)
    {
        if(Environment.GetEnvironmentVariable("KNOWLEDGECLIPPER_ISOLATED_UI_TEST")!="1")throw new Exception("Use isolated desktop runner");
        output=args[0];data=Path.Combine(output,"fixture");Directory.CreateDirectory(output);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var context=new ApplicationContext();using var start=new System.Windows.Forms.Timer{Interval=10};var result=0;
        start.Tick+=async(_,_)=>{start.Stop();try{await WindowLayering(args[1]);}catch(Exception ex){result=1;Console.Error.WriteLine(ex);File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
            finally{foreach(var form in Application.OpenForms.Cast<Form>().ToArray())form.Dispose();WorkflowJson.Save(Path.Combine(output,"results.json"),new{passed=result==0,checks,errors,data,desktop=desktop?.Location});context.ExitThread();}};
        start.Start();Application.Run(context);return result;
    }
    static int CoverWindow(string handlePath,string desktopName)
    {
        if(!desktopName.StartsWith("KnowledgeClipperUiTests_",StringComparison.Ordinal))throw new Exception("Cover app requires the isolated test desktop");
        if(DesktopName()!=desktopName){
            var target=OpenDesktop(desktopName,0,false,0x01ff);
            if(target==IntPtr.Zero||!SetThreadDesktop(target)||DesktopName()!=desktopName)throw new Exception("Cover app cannot enter the isolated test desktop: "+Marshal.GetLastWin32Error());
        }
        Application.EnableVisualStyles();
        using var cover=new Form{Text="公开窗口覆盖验收",StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(120,120,700,500)};
        cover.Shown+=(_,_)=>{
            File.WriteAllText(handlePath+".pending",cover.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
            File.Move(handlePath+".pending",handlePath);
        };
        Application.Run(cover);return 0;
    }
    static bool Above(IntPtr upper,IntPtr lower)
    {
        for(var current=GetWindow(lower,3);current!=IntPtr.Zero;current=GetWindow(current,3))if(current==upper)return true;
        return false;
    }
    static async Task WindowLayering(string repo)
    {
        Directory.CreateDirectory(data);store=new(data);desktop=Assembly.Load("KnowledgeClipper");
        // Use the shipped assets, helpers and modes without touching the production data root.
        var release=Path.GetDirectoryName(desktop.Location)!;
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY",release);
        var probe=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);probe.Start();
        var port=((System.Net.IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using var server=new System.Net.HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();
        var calls=new List<string>();
        _=Task.Run(async()=>{while(server.IsListening)try{
            var request=await server.GetContextAsync();lock(calls)calls.Add(request.Request.HttpMethod+" "+request.Request.Url!.AbsolutePath);
            request.Response.ContentType="application/json";
            await request.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"public-fixture\"}]}"));request.Response.Close();
        }catch(System.Net.HttpListenerException){break;}catch(ObjectDisposedException){break;}});
        var settings=new LanguageSettings{ApiBaseUrl=$"http://127.0.0.1:{port}",Model="public-fixture",GlobalSelection=false,
            ClipboardImageDotEnabled=false,LanguageAutoRead=false,TypedInputAutoRead=false,OriginalExcerptHotkey="",ReplayLastHotkey=""};
        WorkflowJson.Save(Path.Combine(data,"language-settings.json"),settings);
        WorkflowJson.Save(Path.Combine(data,"chat-preferences.json"),new ChatPreferences("api","public-fixture","","fast-answer"));
        var configType=desktop.GetType("KnowledgeClipper.AppConfig",true)!;
        var config=configType.GetMethod("Read")!.Invoke(null,[Path.Combine(repo,"appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config,data);
        var controllerType=desktop.GetType("KnowledgeClipper.Desktop.Core.DesktopController",true)!;
        var controller=controllerType.GetConstructors(Flags).Single(c=>c.GetParameters().Length==5).Invoke(new object?[5]);
        using var main=(Form)desktop.GetType("KnowledgeClipper.BoundClipperForm",true)!.GetConstructor(Flags,null,[configType,controllerType,typeof(bool)],null)!.Invoke([config,controller,false]);
        Call(main,"EnableLanguageWorkflow");main.Show();main.Location=new Point(20,20);await Task.Delay(100);
        flow=main.GetType().GetField("languageFlow",Flags)!.GetValue(main)!;
        Check(main.TopMost&&(WindowStyle(main.Handle,-20)&8)!=0,"实际浮条保持置顶，验收使用原正式入口");
        var coverPath=Path.Combine(output,"cover-handle.txt");
        var child=new ProcessStartInfo(@"D:\Applications\RemNoteQuickClipperRuntime\dotnet-sdk-8.0.424\dotnet.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        child.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);child.ArgumentList.Add("--cover");child.ArgumentList.Add(coverPath);child.ArgumentList.Add(DesktopName());
        using var cover=Process.Start(child)!;
        var childError=cover.StandardError.ReadToEndAsync();
        try{
            var clock=Stopwatch.StartNew();while(!File.Exists(coverPath)){if(cover.HasExited)throw new Exception("Cover app failed: "+await childError);if(clock.Elapsed.TotalSeconds>15)throw new TimeoutException("Cover app did not start");await Task.Delay(30);}
            var other=new IntPtr(long.Parse(File.ReadAllText(coverPath),CultureInfo.InvariantCulture));
            async Task Verify(Form editor,string label)
            {
                editor.Bounds=new Rectangle(120,120,700,500);await Task.Delay(100);
                // Only the fixture is briefly promoted/demoted: on an inactive test desktop
                // there is no taskbar foreground grant. Its final state is an ordinary window.
                SetWindowPos(other,new IntPtr(-1),120,120,700,500,0x50);
                var positioned=SetWindowPos(other,new IntPtr(-2),120,120,700,500,0x50);var nativeError=Marshal.GetLastWin32Error();await Task.Delay(100);
                var observation=new{label,managedTopMost=editor.TopMost,nativeTopMost=(WindowStyle(editor.Handle,-20)&8)!=0,
                    owner=GetWindow(editor.Handle,4).ToInt64(),otherProcess=cover.Id,desktopName=DesktopName(),positioned,nativeError,
                    coverTopMost=(WindowStyle(other,-20)&8)!=0,coveredByOtherApp=Above(other,editor.Handle)};
                WorkflowJson.Save(Path.Combine(output,label+"-layering.json"),observation);
                Check(!observation.nativeTopMost&&!observation.managedTopMost&&observation.owner==0,label+"不继承浮条置顶或所有者关系");
                Check(observation.coveredByOtherApp&&!observation.coverTopMost,label+"可被独立普通应用覆盖");
                Check(editor.ShowInTaskbar&&editor.MinimizeBox,label+"保留任务栏入口和最小化按钮");
                editor.WindowState=FormWindowState.Minimized;await Task.Delay(80);
                Check(editor.WindowState==FormWindowState.Minimized,label+"可最小化");
                editor.WindowState=FormWindowState.Normal;editor.Activate();await Task.Delay(80);
                SetWindowPos(other,new IntPtr(-1),120,120,700,500,0x50);
                SetWindowPos(other,new IntPtr(-2),120,120,700,500,0x50);await Task.Delay(100);
                Check((WindowStyle(editor.Handle,-20)&8)==0&&Above(other,editor.Handle),label+"恢复后仍可被其他应用覆盖");
            }
            await (Task)Call(flow,"StartQuestionInputAsync","公开窗口验收",Array.Empty<byte[]>(),false)!;
            var chat=Chats().Single();await Ready(chat);var id=Draft(chat).Id;await Verify(chat,"聊天制卡");
            chat.Close();Call(flow,"Resume",id);chat=Chats().Single();await Ready(chat);await Verify(chat,"聊天重开");chat.Close();
            var material=new CaptureMaterial("公开语言预览",null,"generated_allowed",null,"公开测试",0,DateTimeOffset.UtcNow);
            var draft=new ClipDraft{Original=material,Material=material,Settings=settings,Configuration=ModeConfigurationLoader.Load(Path.Combine(release,"modes")),
                ModeId="translation",Content=new("translation",Basic:new("公开正面","公开背面"))};
            var language=(Form)Call(flow,"OpenDraft",draft)!;await Verify(language,"语言预览");language.Close();
            var excerpt=main.GetType().GetField("excerptFlow",Flags)!.GetValue(main)!;
            var session=ExcerptCards.Create(material,settings,new ExcerptStore(data));Call(excerpt,"Open",session);
            var excerptForm=Application.OpenForms.Cast<Form>().Single(f=>f.GetType().Name=="ExcerptDraftForm");
            await Verify(excerptForm,"摘录制卡");excerptForm.Close();
            Check(main.TopMost&&(WindowStyle(main.Handle,-20)&8)!=0,"关闭制卡窗口后浮条仍保持置顶");
            lock(calls)Check(calls.All(c=>c.StartsWith("GET ")&&c.EndsWith("/models")),"窗口操作仅查询本地模型目录，没有模型生成或远端导出");
        }finally{if(!cover.HasExited){PostMessage(new IntPtr(File.Exists(coverPath)?long.Parse(File.ReadAllText(coverPath),CultureInfo.InvariantCulture):0),0x10,IntPtr.Zero,IntPtr.Zero);await cover.WaitForExitAsync();}}
    }
}
