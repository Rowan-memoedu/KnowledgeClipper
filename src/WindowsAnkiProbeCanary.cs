using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;
using KnowledgeClipper.Desktop.Core;
using KnowledgeClipper.Desktop.CoreGateway.Tests;

namespace KnowledgeClipper
{
    internal static partial class WindowsUiCanary
    {
        private static int AnkiProbeUiOnly(string root)
        {
            if(Directory.Exists(root))throw new Exception("immutable-evidence-exists");Directory.CreateDirectory(root);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            const string baseline="D:\\Artifacts\\KnowledgeClipper\\desktop-read-to-anki\\desktop-final-resize-speech-20260908T172000Z";
            var config=Config(Path.Combine(root,"data"));var fake=new AnkiProbeFixture();var gateway=new NamedPipeCoreGateway(fake);
            var local=new ReadToAnkiOnlyApplicationSupportPorts();
            var app=new DesktopCompositionRoot(gateway,gateway,gateway,gateway,gateway,local,local,local);
            var deps=LegacyDesktopAdapter.CreateSettingsDependencies(config,UserSettings.Defaults(),null,null,null,null,app,baseline);
            Assert(ReferenceEquals(deps.Application,app)&&ReferenceEquals(app.ModuleActions,gateway),"settings shares production composition/gateway object");
            using var form=new SettingsForm(deps);form.Show();Pump(30);
            Find<ListBox>(form,SettingsAutomationCatalog.Navigation).SelectedIndex=5;Pump(20);
            Find<TextBox>(form,SettingsAutomationCatalog.ExportAnkiProfile).Text="test-profile";
            Find<ComboBox>(form,SettingsAutomationCatalog.ExportAnkiDeckPreset).SelectedItem="custom";
            var routes=Find<TextBox>(form,SettingsAutomationCatalog.ExportAnkiDeckRoutes);
            routes.Text="{\"language-writing\":\"fixture-deck\",\"secondary\":\"other-deck\"}";
            var route=Find<ComboBox>(form,SettingsForm.AnkiProbeRouteId);
            var button=Find<SemanticActionButton>(form,SettingsAutomationCatalog.ExportAnkiProbe);
            var result=Find<TextBox>(form,SettingsAutomationCatalog.ExportAnkiProbeStatus);
            void Complete(){WaitUntil(()=>button.Enabled&&!result.AccessibleDescription.Contains("checking"),3000,"probe terminal/busy released");Pump(20);}
            Assert(route.SelectedIndex==-1&&fake.Submits==0,"multiple routes require explicit selection; settings opening/editing never probes");
            button.PerformClick();Pump(20);Complete();
            Assert(result.Text.Contains("需要选择检查目标")&&fake.Submits==0,"no guessed target or transport on missing selection");
            route.SelectedItem="language-writing";
            fake.QueryGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            button.PerformClick();Pump(20);WaitUntil(()=>fake.Queries==1,2000,"typed observer reached from real settings click");
            button.ScheduleCommand();button.ScheduleCommand();Pump(20);
            Assert(fake.Submits==1&&!button.Enabled,"concurrent semantic invocations suppressed while busy");
            fake.QueryGate.SetResult(true);Complete();fake.QueryGate=null;
            Assert(result.Text.Contains("当前目标可用")&&result.Text.Contains("optional-a、optional-b")&&fake.Route=="language-writing","real UI maps typed ready plus multiple non-ready optional routes");
            using(var configJson=JsonDocument.Parse(fake.Configuration))
                Assert(!configJson.RootElement.GetProperty("provision_managed").GetBoolean()&&configJson.RootElement.GetProperty("expected_profile").GetString()=="test-profile","production configuration builder publishes draft without provisioning");
            button.PerformClick();Pump(20);Complete();Assert(fake.Submits==2,"second click executes once after busy recovery");
            foreach(var scenario in new[]{"failed","unknown","cancelled","empty-target","duplicate-target","missing-readiness","missing-evidence","route-mismatch","operation-mismatch","evidence-empty","timeout"})
            {
                fake.Scenario=scenario;button.PerformClick();Pump(20);Complete();
                using var projection=JsonDocument.Parse(result.AccessibleDescription);
                Assert(!projection.RootElement.GetProperty("ready").GetBoolean()&&button.Enabled,"UI non-ready terminal and repeatable button: "+scenario);
            }
            fake.Scenario="timeout";button.PerformClick();Pump(20);form.CancelAnkiProbe();Complete();
            Console.WriteLine("ANKI_CANCEL_OBSERVED " + result.AccessibleDescription);
            Assert(result.AccessibleDescription.Contains("Cancelled")&&!result.AccessibleDescription.Contains("checking"),"cancel clears checking/previous evidence");
            fake.Scenario="ready";fake.PublishFailure="cancelled-during-publish";button.PerformClick();Pump(20);Complete();
            Assert(result.AccessibleDescription.Contains("CancelledOrTimedOut")&&!result.AccessibleDescription.Contains("checking"),"publish cancellation has explicit nonready terminal");fake.PublishFailure=null;
            // The old provider task deliberately ignores cancellation. Only the observer
            // deadline/cancellation boundary may isolate it from a later UI invocation.
            fake.QueryGate=new(TaskCreationOptions.RunContinuationsAsynchronously);fake.IgnoreQueryCancellation=true;
            var lateGate=fake.QueryGate;var oldQueries=fake.Queries;
            button.PerformClick();Pump(20);WaitUntil(()=>fake.Queries>oldQueries,2000,"noncooperative old query started");
            var oldOperation="probe-"+fake.Submits;
            form.CancelAnkiProbe();Complete();
            Assert(!result.AccessibleDescription.Contains("checking")&&button.Enabled,"noncooperative query cancellation releases UI");
            fake.QueryGate=null;fake.IgnoreQueryCancellation=false;route.SelectedItem="secondary";
            button.PerformClick();Pump(20);Complete();
            var newState=result.AccessibleDescription;var newText=result.Text;var readCount=fake.Reads;
            Assert(newState.Contains("secondary")&&result.Text.Contains("当前目标可用"),"new probe publishes its distinct target readiness after cancellation");
            lateGate.SetResult(true);WaitUntil(()=>fake.LastCompletedQuery==oldOperation,2000,"old query actually completed late");Pump(20);
            Assert(result.AccessibleDescription==newState&&result.Text==newText&&fake.Reads==readCount&&button.Enabled,
                "late old success cannot overwrite new UI readiness/evidence or read another artifact");
            routes.Text="invalid-json";button.PerformClick();Pump(20);Complete();
            Assert(result.AccessibleDescription.Contains("Failed")&&!result.AccessibleDescription.Contains("checking"),"validation failure clears previous ready/evidence and releases busy");
            Assert(!Find<Button>(form,SettingsAutomationCatalog.ExportAnkiInitialize).Enabled,"managed initialization remains disabled");
            File.WriteAllText(Path.Combine(root,"receipt.json"),JsonSerializer.Serialize(new{status="PASS",assertions,scenario="real SettingsForm semantic click -> shared application command -> NamedPipeCoreGateway -> typed SDK observer fixture",fake.Submits,fake.Publishes,fake.Reads,externalCalls=0,workerRuns=0,mutation=0}));
            form.Close();Console.WriteLine("ANKI_PROBE_WINDOWS_UI PASS assertions="+assertions);return 0;
        }
    }
}
