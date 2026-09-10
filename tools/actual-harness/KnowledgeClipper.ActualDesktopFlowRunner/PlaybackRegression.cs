using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

// Test-only UI orchestration. The SDK is exclusively an observer in this file.
internal static class PlaybackRegression
{
    internal const string StopId=PlaybackMenuSelector.StopName;
    internal const string StatusId="knowledge-clipper.desktop.status";
    internal const string LanguageId="knowledge-clipper.desktop.settings.question.language-card-default";
    internal const string ClientSha="1C25A618536D30A0F5CE31C5A0FE1050C821C7E2790B5F70AC04AEAC226C842F";
    internal const string KernelSha="935988224E7E36116909B4CF137E5A2BC3DA586AA217201773F5AF6268971678";
    internal const string ProductRoot=@"D:\Artifacts\KnowledgeClipper\desktop-read-to-anki\desktop-final-resize-speech-20260908T172000Z";
    internal const string ProductManifestSha="7087B3084EB7B66D6CB0208B01E57BC16EA2406C0A73F3F9361764A57FD4832C";
    internal static readonly SequenceSource Source=new("gradually","Regular practice can "," improve your confidence.");
    internal static object Plan=>new{mode="--actual --playback-regression",source=Source,languageMode="both",ui=new{StopId,StatusId,LanguageId},
        expected=new{synth=1,playback=3,content=1,exporter=1,notes=2,terminals=new[]{"Stopped","IdleReplayable","Stopped"}},
        capture="existing desktop-test-capture.v1 test input port",sdkCommands=0,automaticRetries=0,physicalKeyboard=false,
        product=new{root=ProductRoot,manifestSha256=ProductManifestSha},statusChannel=PlaybackStatusReader.Channel,
        tooShortPolicy="record actual playback state separately from menu missing/disabled and UI status empty; never change input, rate or speed",liveExecuted=false};
    static void Require(bool condition,string code)=>QuestionRegression.Require(condition,code);
    static string Hash(string path)=>QuestionRegression.FileHash(path);

    internal static int Run(string[] args)
    {
        string Arg(string key){var index=Array.IndexOf(args,key);if(index<0||index+1>=args.Length)throw new ArgumentException("argument-required:"+key);return args[index+1];}
        Require(!args.Contains("--question-regression")&&!args.Contains("--sequence-matrix"),"playback.mode-mix-forbidden");
        var candidate=Path.GetFullPath(Arg("--candidate"));var evidence=Path.GetFullPath(Arg("--evidence-root"));var data=Path.GetFullPath(Arg("--data-root"));
        Require(!Directory.Exists(evidence)&&!Directory.Exists(Path.Combine(data,"KnowledgeClipper","core-v3")),"playback.fresh-roots-required");
        var deck=Arg("--deck");var endpoint=new Uri(Arg("--anki-endpoint"));
        Require(deck==QuestionRegression.CanaryDeck&&endpoint.IsLoopback&&endpoint.Scheme=="http","playback.approved-isolated-target-required");
        var fixture=Path.GetFullPath(Arg("--capture-fixture"));ValidateFixture(File.ReadAllBytes(fixture));
        var manifestHash=Hash(Path.Combine(candidate,"candidate-file-manifest.v1.json"));
        Require(string.Equals(candidate,ProductRoot,StringComparison.OrdinalIgnoreCase)&&manifestHash==ProductManifestSha,"playback.fixed-product-binding-drift");
        using(var authorization=JsonDocument.Parse(File.ReadAllBytes(Arg("--authorization"))))
            Require(authorization.RootElement.GetProperty("mayRunOneActual").GetBoolean()&&string.Equals(authorization.RootElement.GetProperty("candidateManifestSha256").GetString(),manifestHash,StringComparison.OrdinalIgnoreCase),"quality-authorization-invalid");
        Require(Hash(typeof(CoreV3Client).Assembly.Location)==ClientSha,"playback.runner-sdk-drift");
        foreach(var binding in new[]{("KnowledgeClipper.Core.V3.Client.dll",ClientSha),("KnowledgeClipper.Core.V3.Kernel.dll",KernelSha)}){
            var matches=Directory.GetFiles(candidate,binding.Item1,SearchOption.AllDirectories);Require(matches.Length>0&&matches.All(p=>Hash(p)==binding.Item2),"playback.candidate-sdk-runtime-drift");}
        Directory.CreateDirectory(evidence);
        void Save(string name,object value){using var stream=new FileStream(Path.Combine(evidence,name),FileMode.CreateNew);JsonSerializer.Serialize(stream,value,new JsonSerializerOptions{WriteIndented=true});}
        void Append(object value)=>File.AppendAllText(Path.Combine(evidence,"events.ndjson"),JsonSerializer.Serialize(value)+Environment.NewLine,new UTF8Encoding(false));
        Save("pending.json",new{Plan,candidate,manifestHash,fixture,fixtureSha256=Hash(fixture),createdAt=DateTimeOffset.UtcNow});
        try
        {
            var heartbeat=new HeartbeatJournal(Append);var binding=DesktopAutomationBinding.Load(candidate);
            using var surface=new OwnedCandidateProcessPort(heartbeat);using var anki=new AnkiConnectReadOnlyClient(endpoint.ToString());
            var sdk=new CoreV3Client(Arg("--pipe"));
            var executable=Directory.GetFiles(candidate,"KnowledgeClipper.exe",SearchOption.AllDirectories).Single();
            surface.StartOwned(executable,"--open-settings",data,fixture);surface.AttachExact(executable,binding.FloatingRoot);
            surface.SelectItem(binding.Require("settings.page.select:export"),"导出");
            surface.SelectItem(binding.Require("settings.export.anki.deck-preset.select:custom"),"custom");
            var routes=JsonSerializer.Serialize(new Dictionary<string,string>{{"canary",deck}});
            surface.SetValue(binding.Require("settings.export.anki.deck-routes.edit"),routes);surface.AssertValue(binding.Require("settings.export.anki.deck-routes.edit"),routes);
            surface.SelectItem(binding.Require("settings.page.select:export"),"提问与制卡");surface.SelectItem(LanguageId,"both");
            surface.Invoke(binding.Require("settings.save"));Save("saved-destination.json",surface.VerifySavedDestination(data,deck));
            surface.Invoke(binding.Require("mode.context.set"));
            var baseline=anki.CaptureBaseline(deck);Save("baseline.json",new{deck,noteIds=baseline});
            var checkpoint=sdk.BeginReadToAnkiDiscoveryAsync("playback-actual-"+Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
            surface.Invoke(binding.Require("read.capture"));
            var initial=sdk.WaitForAcceptedReadToAnkiAsync(checkpoint).GetAwaiter().GetResult();
            var first=WaitActive(sdk,initial.OperationId,initial.CommandId,heartbeat);
            var rounds=new List<ReadPlaybackControlSnapshot>();
            rounds.Add(StopViaUi(surface,sdk,first,heartbeat,Append));Save("round-0-stopped.json",new{terminal=rounds[^1],actualUi=WaitUiTerminal(surface,rounds[^1],false,heartbeat)});
            var draft=sdk.WaitReadAloudCardsActionAsync(initial).GetAwaiter().GetResult();
            Require(draft.Pipeline==ReadAloudCardsPipelineState.AwaitingCardConfirmation&&draft.CardDraft?.Cards.Count==2&&draft.FrozenLanguageCardChoice=="both","playback.exact-both-draft-required-no-resolution-fallback");
            Require(draft.ContextEvidence is {ContextRequested:true,ContextApplied:true} context&&
                string.Equals(context.SelectedTextSha256,Source.SelectedSha256,StringComparison.OrdinalIgnoreCase)&&string.Equals(context.SpokenTextSha256,Source.CompleteSha256,StringComparison.OrdinalIgnoreCase),"playback.original-context-drift");
            var cards=draft.CardDraft!;var faces=cards.Cards.Select((x,i)=>new PreviewCard(i,x.Front,x.Back,x.Template)).ToArray();
            Require(faces.Select(x=>x.Template).SequenceEqual(new[]{"language_context","language_writing"}),"playback.both-template-mismatch");
            var preview=surface.ReadPreviewCards(binding.Require("flashcard-preview.cards.observe"),binding.Require("flashcard-preview.front.observe"),binding.Require("flashcard-preview.back.observe"),new(initial.OperationId,cards.DraftSha256,cards.Revision),faces);
            var beforeExport=PlaybackLedger.Verify(data,initial.OperationId,rounds,0);
            Require(anki.CaptureBaseline(deck).SetEquals(baseline),"playback.export-before-confirm");
            Save("draft.json",new{Source,draft.OperationId,cards,actualUiPreview=preview,ledger=beforeExport,draft.SettingsEvidence,draft.ContextEvidence});
            var confirmCheckpoint=sdk.BeginReadToAnkiActionDiscoveryAsync(initial.OperationId,"playback-confirm").GetAwaiter().GetResult();
            surface.Invoke(binding.Require("flashcard-preview.confirm"));
            var confirmation=sdk.WaitForAcceptedReadToAnkiActionAsync(confirmCheckpoint).GetAwaiter().GetResult();
            var confirmed=sdk.WaitReadAloudCardsActionAsync(confirmation).GetAwaiter().GetResult();
            Require(confirmed.Pipeline==ReadAloudCardsPipelineState.Completed,"playback.confirm-not-completed-no-retry");
            var readback=VerifyReadback(anki,deck,baseline,confirmed,preview);Save("confirmed.json",new{confirmation,readback,actualUi=WaitUiTerminal(surface,rounds[0],true,heartbeat)});
            for(var ordinal=1;ordinal<=2;ordinal++)
            {
                WaitReplayButton(surface,initial.OperationId,heartbeat);
                var replayCheckpoint=sdk.BeginReadToAnkiActionDiscoveryAsync(initial.OperationId,"playback-replay-"+ordinal).GetAwaiter().GetResult();
                surface.Invoke(StatusId);
                var replay=sdk.WaitForAcceptedReadToAnkiActionAsync(replayCheckpoint).GetAwaiter().GetResult();
                Require(replay.CommandId!=initial.CommandId&&replay.AcceptedSequence>confirmation.AcceptedSequence,"playback.replay-new-action-required");
                var active=WaitActive(sdk,initial.OperationId,replay.CommandId,heartbeat);
                Require(rounds.All(x=>x.Target!.AttemptId!=active.Target!.AttemptId),"playback.attempt-reused");
                var terminal=ordinal==2?StopViaUi(surface,sdk,active,heartbeat,Append):WaitPlaybackTerminal(sdk,active.Target!,ReadPlaybackControlState.IdleReplayable,heartbeat);
                rounds.Add(terminal);
                var result=sdk.WaitReadAloudCardsActionAsync(replay).GetAwaiter().GetResult();
                Require(result.TerminalCommandId==replay.CommandId&&result.TerminalSequence>=replay.AcceptedSequence&&result.Pipeline==ReadAloudCardsPipelineState.Completed,"playback.old-terminal-rejected");
                Require(result.CardDraft?.DraftSha256==cards.DraftSha256&&result.CardDraft.Cards.Count==2&&result.SynthesisArtifact?.Equals(draft.SynthesisArtifact)==true,"playback.replay-mutated-draft-or-audio");
                var unchanged=VerifyReadback(anki,deck,baseline,result,preview);
                Require(unchanged.NoteIds.Order().SequenceEqual(readback.NoteIds.Order()),"playback.replay-created-notes");
                var ledger=PlaybackLedger.Verify(data,initial.OperationId,rounds,1);
                Require(ledger.AudioSha256==beforeExport.AudioSha256,"playback.replay-audio-hash-drift");
                Save($"round-{ordinal}.json",new{replay,terminal,readback=unchanged,ledger,actualUi=WaitUiTerminal(surface,terminal,true,heartbeat)});
            }
            var finalLedger=PlaybackLedger.Verify(data,initial.OperationId,rounds,1);
            Save("actual-run.receipt.json",new{status="passed",scope="single-Read-production-Desktop-UI-stop-replay",candidate,manifestHash,runnerSha256=Hash(typeof(PlaybackRegression).Assembly.Location),
                sdkSha256=ClientSha,kernelSha256=KernelSha,Source,initial.OperationId,rounds,actualUiCards=preview,readback,finalLedger,
                finalActualUi=WaitUiTerminal(surface,rounds[^1],true,heartbeat),physicalSpeakerPlayback=true,physicalKeyboard=false,automaticRetries=0,ocr=0,remnote=0});
            File.WriteAllText(Path.Combine(evidence,"READABLE.md"),$"# 单任务停止/重播\n\n原文：{Source.Prefix}{Source.SelectedText}{Source.Suffix}\n\n目标：{Source.SelectedText}；模式 both。\n\n"+
                string.Join("\n\n",preview.Select((x,i)=>$"卡 {i+1}\nFront: {x.Front}\nBack: {x.Back}"))+"\n\nNotes: "+string.Join(", ",readback.NoteIds)+"\n\n初播 Stopped → Replay IdleReplayable → Replay Stopped；1 synth / 3 playback / 1 Content / 1 exporter / 2 notes。\n",new UTF8Encoding(false));
            return 0;
        }
        catch(Exception ex){Save("actual-run.receipt.json",new{status="failed-closed",candidate,manifestHash,error=ex.Message,automaticRetry=false,sideEffects="consult-preserved-worker-results-and-readback"});return 20;}
    }

    static ReadPlaybackControlSnapshot StopViaUi(OwnedCandidateProcessPort surface,CoreV3Client sdk,ReadPlaybackControlSnapshot active,HeartbeatJournal heartbeat,Action<object> log)
    {
        var target=active.Target!;surface.OpenPlaybackContextMenu();var watch=Stopwatch.StartNew();string lastAvailability="menu-unobserved";
        while(watch.Elapsed<TimeSpan.FromSeconds(10))
        {
            var found=surface.FindOwnedPlaybackMenu(StopId);var availability=PlaybackMenuSelector.Availability(found.Observation);
            if(availability!=lastAvailability){log(new{state="stop-menu-observation",availability,menu=found.Observation});lastAvailability=availability;}
            var now=sdk.GetReadPlaybackControlAsync(target.OperationId).GetAwaiter().GetResult();RequireSameTarget(now,target);
            Require(now.State is ReadPlaybackControlState.Playing,"playback.stop-not-invoked:"+availability+":observed-state="+now.State+":no-retry");
            Require(availability!="menu-invoke-pattern-missing","playback.stop-menu-invoke-pattern-missing");
            if(found.Element is {} menu&&availability=="menu-ready")
            {
                PlaybackStatusObservation? actualUi=null;
                try{actualUi=surface.ReadOwnedPlaybackStatus();RequireUiTarget(actualUi.Status,target,requireCanStop:true);}
                catch(Exception ex)when(ex is InvalidOperationException or JsonException or KeyNotFoundException){log(new{state="stop-ui-status-rejected",availability,actualUi,error=ex.Message});throw;}
                Require(menu.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern),"playback.stop-menu-invoke-pattern-missing");
                Require(menu.Current.IsEnabled,"playback.stop-menu-disabled-before-invoke");
                log(new{state="stop-ui-once",target,actualUi,controlName=StopId,selection=found.Observation});((InvokePattern)pattern).Invoke();
                return WaitPlaybackTerminal(sdk,target,ReadPlaybackControlState.Stopped,heartbeat);
            }
            heartbeat.Beat("playback.stop-menu-enabled.wait");Thread.Sleep(20);
        }
        throw new InvalidOperationException("playback.stop-not-invoked:"+lastAvailability+":timeout:no-retry");
    }
    static object WaitUiTerminal(OwnedCandidateProcessPort surface,ReadPlaybackControlSnapshot expected,bool requireCompleted,HeartbeatJournal heartbeat)
    {
        var clock=Stopwatch.StartNew();JsonElement last=default;string text="";var observations=new List<PlaybackStatusObservation>();
        while(clock.Elapsed<TimeSpan.FromSeconds(15))
        {
            // Reacquire the live control for each read; no backend-filled UI fields.
            var actualUi=surface.ReadOwnedPlaybackStatus();
            if(observations.Count==0||observations[^1].RawLegacyDescription!=actualUi.RawLegacyDescription||observations[^1].NativeText!=actualUi.NativeText||observations[^1].Enabled!=actualUi.Enabled)observations.Add(actualUi);
            last=actualUi.Status;text=actualUi.NativeText;var enabled=actualUi.Enabled;
            if(IsUiTerminalReady(last,text,enabled,expected,requireCompleted))return new{final=actualUi,observations};
            heartbeat.Beat("playback.actual-ui-terminal.wait");Thread.Sleep(50);
        }
        throw new InvalidOperationException("playback.actual-ui-terminal-not-proven:"+JsonSerializer.Serialize(observations));
    }
    internal static void RequireUiTerminal(JsonElement ui,string text,bool enabled,ReadPlaybackControlSnapshot expected,bool requireCompleted)
    {
        RequireUiTarget(ui,expected.Target!,false);
        Require(ui.GetProperty("Playback").GetString()==expected.State.ToString()&&!ui.GetProperty("CanStop").GetBoolean(),"playback.ui-terminal-mismatch");
        if(requireCompleted)Require(ui.GetProperty("Pipeline").GetString()=="Completed"&&ui.GetProperty("CanReplay").GetBoolean()&&enabled&&text.Contains("重播",StringComparison.Ordinal),"playback.ui-not-completed-replayable");
    }
    internal static bool IsUiTerminalReady(JsonElement ui,string text,bool enabled,ReadPlaybackControlSnapshot expected,bool requireCompleted)
    {
        try{RequireUiTerminal(ui,text,enabled,expected,requireCompleted);return true;}
        catch(InvalidOperationException ex)when(ex.Message is "playback.ui-target-drift" or "playback.ui-terminal-mismatch" or "playback.ui-not-completed-replayable"){return false;}
    }
    internal static void RequireUiTarget(JsonElement status,ReadPlaybackTarget target,bool requireCanStop)
    {
        Require(status.GetProperty("OperationId").GetString()==target.OperationId&&status.GetProperty("ActionCommandId").GetString()==target.ActionCommandId&&status.GetProperty("AttemptId").GetString()==target.AttemptId&&(!requireCanStop||status.GetProperty("CanStop").GetBoolean()),"playback.ui-target-drift");
    }
    static void WaitReplayButton(OwnedCandidateProcessPort surface,string operation,HeartbeatJournal heartbeat)
    {
        var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromSeconds(15)){
            var actualUi=surface.ReadOwnedPlaybackStatus();var ui=actualUi.Status;
            if(actualUi.Enabled&&actualUi.NativeText.Contains("重播",StringComparison.Ordinal)&&ui.GetProperty("OperationId").GetString()==operation&&ui.GetProperty("CanReplay").GetBoolean())return;
            heartbeat.Beat("playback.replay-button.wait");Thread.Sleep(100);}
        throw new InvalidOperationException("playback.real-replay-control-not-ready");
    }
    static ReadPlaybackControlSnapshot WaitActive(CoreV3Client sdk,string operation,string command,HeartbeatJournal heartbeat)
    {
        var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromMinutes(3)){
            var value=sdk.GetReadPlaybackControlAsync(operation).GetAwaiter().GetResult();
            if(value.Target?.ActionCommandId==command){if(value.CanStop&&value.State==ReadPlaybackControlState.Playing)return value;
                if(value.State is ReadPlaybackControlState.IdleReplayable or ReadPlaybackControlState.Stopped or ReadPlaybackControlState.Unknown)throw new InvalidOperationException("playback.too-short-or-failed-before-active-observation");}
            heartbeat.Beat("playback.active-round.wait");Thread.Sleep(25);}
        throw new InvalidOperationException("playback.active-round-timeout");
    }
    static ReadPlaybackControlSnapshot WaitPlaybackTerminal(CoreV3Client sdk,ReadPlaybackTarget target,ReadPlaybackControlState expected,HeartbeatJournal heartbeat)
    {
        var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromMinutes(2)){
            var snapshot=sdk.GetReadPlaybackControlAsync(target.OperationId).GetAwaiter().GetResult();RequireSameTarget(snapshot,target);
            if(snapshot.State is ReadPlaybackControlState.Stopped or ReadPlaybackControlState.IdleReplayable or ReadPlaybackControlState.Unknown){RequireTerminal(snapshot,target,expected);return snapshot;}
            heartbeat.Beat("playback.same-round-terminal.wait");Thread.Sleep(25);}
        throw new InvalidOperationException("playback.terminal-timeout-accepted-is-not-stopped");
    }
    internal static void RequireSameTarget(ReadPlaybackControlSnapshot value,ReadPlaybackTarget target)=>Require(value.Target==target,"playback.observed-round-changed");
    internal static void RequireTerminal(ReadPlaybackControlSnapshot value,ReadPlaybackTarget target,ReadPlaybackControlState expected)
    {RequireSameTarget(value,target);Require(value.State==expected&&expected is ReadPlaybackControlState.Stopped or ReadPlaybackControlState.IdleReplayable,"playback.unexpected-terminal-no-retry");}
    static AnkiReadback VerifyReadback(AnkiConnectReadOnlyClient anki,string deck,IReadOnlySet<long> baseline,ReadAloudCardsProjectionSnapshot projection,IReadOnlyList<PreviewCard> preview)
    {
        var readback=anki.ReadNew(deck,baseline);var typed=projection.AnkiSemanticReadback?.Cards;
        Require(typed?.Count==2&&readback.NoteIds.Count==2&&readback.CardCount==2&&readback.Cards?.Count==2,"playback.two-note-readback-required");
        for(var i=0;i<2;i++){var t=typed![i];var p=preview[i];Require(t.Front==p.Front&&t.Back==p.Back&&t.DeckName==deck&&readback.Cards!.Any(x=>x.NoteId==t.NoteId&&x.CardIds.Order().SequenceEqual(t.CardIds.Order())&&x.Front==p.Front&&x.Back==p.Back&&x.Deck==deck),"playback.exact-readback-mismatch");}
        return readback;
    }
    internal static void ValidateFixture(byte[] bytes)
    {
        using var json=JsonDocument.Parse(bytes);var value=json.RootElement;
        Require(value.EnumerateObject().Select(x=>x.Name).Order().SequenceEqual(new[]{"prefix","schemaVersion","selectedText","suffix"})&&value.GetProperty("schemaVersion").GetString()=="desktop-test-capture.v1"&&value.GetProperty("selectedText").GetString()==Source.SelectedText&&value.GetProperty("prefix").GetString()==Source.Prefix&&value.GetProperty("suffix").GetString()==Source.Suffix,"playback.exact-source-fixture-required");
    }
    internal static int SelfTest(string[]? args=null)
    {
        var cases=0;void Reject(Action action){try{action();throw new Exception("negative accepted");}catch(InvalidOperationException){cases++;}}
        ValidateFixture(JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="desktop-test-capture.v1",selectedText=Source.SelectedText,prefix=Source.Prefix,suffix=Source.Suffix}));cases++;
        Reject(()=>ValidateFixture(JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="desktop-test-capture.v1",selectedText="gradually changed",prefix=Source.Prefix,suffix=Source.Suffix})));
        var target=new ReadPlaybackTarget("operation",new('a',64),"command-1","attempt-1");
        var snapshot=new ReadPlaybackControlSnapshot("operation",ReadPlaybackControlState.Playing,true,target,1);RequireSameTarget(snapshot,target);cases++;
        Reject(()=>RequireSameTarget(snapshot,target with{AttemptId="attempt-2"}));Reject(()=>RequireSameTarget(snapshot,target with{ActionCommandId="command-2"}));
        RequireUiTarget(JsonSerializer.SerializeToElement(new{OperationId="operation",ActionCommandId="command-1",AttemptId="attempt-1",CanStop=true}),target,true);cases++;
        Reject(()=>RequireUiTarget(JsonSerializer.SerializeToElement(new{OperationId="operation",ActionCommandId="command-1",AttemptId="attempt-2",CanStop=true}),target,true));
        PlaybackLedger.RequireCounts(1,3,1,1,3,1);cases++;
        Reject(()=>PlaybackLedger.RequireCounts(2,3,1,1,3,1));Reject(()=>PlaybackLedger.RequireCounts(1,3,2,1,3,1));Reject(()=>PlaybackLedger.RequireCounts(1,3,1,2,3,1));
        Reject(()=>RequireTerminal(snapshot with{State=ReadPlaybackControlState.StopRequested},target,ReadPlaybackControlState.Stopped));
        Reject(()=>RequireTerminal(snapshot with{State=ReadPlaybackControlState.IdleReplayable},target,ReadPlaybackControlState.Stopped));
        RequireTerminal(snapshot with{State=ReadPlaybackControlState.Stopped},target,ReadPlaybackControlState.Stopped);cases++;
        var stopped=snapshot with{State=ReadPlaybackControlState.Stopped,CanStop=false};
        JsonElement Ui(string action="command-1",string playback="Stopped",bool replay=true)=>JsonSerializer.SerializeToElement(new{OperationId="operation",ActionCommandId=action,AttemptId="attempt-1",CanStop=false,Playback=playback,Pipeline="Completed",CanReplay=replay});
        RequireUiTerminal(Ui(),"已停止 · 重播",true,stopped,true);cases++;
        Reject(()=>RequireUiTerminal(Ui(playback:"StopRequested"),"已请求停止",true,stopped,true));
        Reject(()=>RequireUiTerminal(Ui(action:"old-command"),"已停止 · 重播",true,stopped,true));
        Reject(()=>RequireUiTerminal(Ui(replay:false),"忙碌",false,stopped,true));
        var pending=PlaybackStatusReader.ParseLegacyDescription(true,JsonSerializer.Serialize(new{OperationId="operation",ActionCommandId=(string?)null,AttemptId=(string?)null,Playback="Stopped",Pipeline="Completed",CanStop=false,CanReplay=false}));
        // SDK is already terminal; the actual UI is pending, then binds its real target.
        Require(new[]{IsUiTerminalReady(pending,"忙碌",false,stopped,true),IsUiTerminalReady(Ui(),"已停止 · 重播",true,stopped,true)}.SequenceEqual(new[]{false,true}),"playback.ui-pending-refresh-regression");cases++;
        Reject(()=>RequireUiTarget(pending,target,true));
        var recordedCases=0;
        if(args is not null&&args.Contains("--menu-observation")){
            string Input(string key){var i=Array.IndexOf(args,key);if(i<0||i+1>=args.Length)throw new ArgumentException(key);return args[i+1];}
            using var menuDoc=JsonDocument.Parse(File.ReadAllBytes(Input("--menu-observation")));
            var menuRoot=menuDoc.RootElement;var pid=menuRoot.GetProperty("targetPid").GetInt32();
            var observations=new List<PlaybackMenuObservation>();var rawCount=0;
            foreach(var window in menuRoot.GetProperty("windows").EnumerateArray()){
                var values=window.GetProperty("values").EnumerateArray().ToArray();var root=values[0];var index=0;
                foreach(var item in values){
                    index++;var name=item.GetProperty("name").GetString()??"";if(name==StopId)rawCount++;
                    observations.Add(new(window.GetProperty("hwnd").GetRawText()+":"+index,item.GetProperty("processId").GetInt32(),window.GetProperty("owner").GetInt32(),
                        !root.GetProperty("isOffscreen").GetBoolean(),root.GetProperty("controlType").GetString()=="ControlType.Menu",
                        item.GetProperty("controlType").GetString()=="ControlType.MenuItem",!item.GetProperty("isOffscreen").GetBoolean(),name,
                        item.GetProperty("isEnabled").GetBoolean(),item.GetProperty("invokePattern").GetBoolean()));
                }
            }
            Require(rawCount==2,"recorded-menu-mirror-fixture-drift");recordedCases++;
            Require(PlaybackMenuSelector.Availability(PlaybackMenuSelector.Select(observations,pid,StopId))=="menu-ready","recorded-menu-not-selected");recordedCases++;
            using var statusDoc=JsonDocument.Parse(File.ReadAllBytes(Input("--status-observation")));var status=statusDoc.RootElement;
            var parsed=PlaybackStatusReader.ParseLegacyDescription(status.GetProperty("legacyPatternAvailable").GetBoolean(),status.GetProperty("legacyDescription").GetString());
            Require(status.GetProperty("uiaHelpText").GetString()==""&&parsed.GetProperty("OperationId").GetString()=="offline-op"&&
                parsed.GetProperty("ActionCommandId").GetString()=="offline-action"&&parsed.GetProperty("AttemptId").GetString()=="offline-attempt"&&parsed.GetProperty("CanStop").GetBoolean(),"recorded-status-channel-drift");recordedCases++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{status="PASS",playbackCases=cases,menuSelectorCases=PlaybackMenuSelector.SelfTest(),statusReaderCases=PlaybackStatusReader.SelfTest(),recordedProbeCases=recordedCases,previewBarrierCases=PreviewReadBarrier.SelfTest(),externalCalls=0,physicalSpeakerPlayback=false,uiEventsExecuted=false}));return 0;
    }
}
