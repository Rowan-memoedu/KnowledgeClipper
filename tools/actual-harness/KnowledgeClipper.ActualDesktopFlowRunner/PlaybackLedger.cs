using System.Text.Json;
using KnowledgeClipper.Core.V3.Client;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record PlaybackLedgerSnapshot(int Synthesis,int Playback,int Content,int Exporter,string AudioSha256,IReadOnlyList<object> Evidence);
internal static class PlaybackLedger
{
    static void Require(bool value,string message)=>QuestionRegression.Require(value,message);
    static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString()!;
    static string Hash(string path)=>QuestionRegression.FileHash(path);
    static string Inside(string root,string relative)
    {
        Require(!Path.IsPathRooted(relative)&&!relative.Replace('\\','/').Split('/').Contains(".."),"playback.ledger-path-escape");
        var path=Path.GetFullPath(Path.Combine(root,relative));Require(path.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"playback.ledger-path-escape");return path;
    }
    internal static PlaybackLedgerSnapshot Verify(string dataRoot,string operation,IReadOnlyList<ReadPlaybackControlSnapshot> rounds,int expectedExport)
    {
        var jobs=Path.Combine(Path.GetFullPath(dataRoot),"KnowledgeClipper","core-v3","worker-process-v3","jobs");
        var paths=Directory.GetFiles(jobs,"request.json",SearchOption.AllDirectories);Require(paths.Length<=16,"playback.unexpected-job-count");
        var synth=0;var play=0;var content=0;var exporter=0;string? audioHash=null;string? audioIdentity=null;
        var playInputs=new List<string>();var evidence=new List<object>();var matchedRounds=new HashSet<string>();
        foreach(var requestPath in paths)
        {
            using var requestDocument=JsonDocument.Parse(File.ReadAllBytes(requestPath));var request=requestDocument.RootElement;
            Require(Text(request,"operationId")==operation,"playback.foreign-operation-worker");
            var resultPath=Path.Combine(Path.GetDirectoryName(requestPath)!,"result.json");
            using var resultDocument=JsonDocument.Parse(File.ReadAllBytes(resultPath));var result=resultDocument.RootElement;
            foreach(var field in new[]{"taskId","runId","stageId","attemptId","requestId","operationId"})Require(Text(request,field)==Text(result,field),"playback.worker-result-identity-drift");
            var job=Inside(jobs,Text(request,"taskId"));var stage=Text(request,"stageId");var status=Text(result,"status");
            if(stage=="speech-playback")
            {
                play++;var round=rounds.Single(x=>x.Target!.AttemptId==Text(request,"attemptId"));Require(matchedRounds.Add(round.Target!.AttemptId),"playback.duplicate-attempt");
                Require(Text(request,"operation")=="speech.playback","playback.wrong-worker-operation");
                var expected=round.State==ReadPlaybackControlState.Stopped?"stopped":"idle_replayable";
                Require(Text(result.GetProperty("payload"),"playbackState")==expected,"playback.worker-terminal-does-not-match-observer");
                if(expected=="stopped"){
                    Require(status=="cancelled"&&Text(result.GetProperty("error"),"code")=="playback.user_stop","playback.stop-not-proven-by-result");
                    var signalPath=Inside(job,Text(request.GetProperty("cancelSignal"),"path"));using var signal=JsonDocument.Parse(File.ReadAllBytes(signalPath));
                    Require(Text(signal.RootElement,"attemptId")==Text(request,"attemptId")&&Text(signal.RootElement,"operationId")==operation&&Text(signal.RootElement,"reason")=="user_stop","playback.cancel-signal-drift");
                    evidence.Add(new{signalPath,sha256=Hash(signalPath)});
                }else Require(status=="completed"&&result.GetProperty("error").ValueKind==JsonValueKind.Null,"playback.natural-terminal-not-proven");
                var audio=request.GetProperty("inputRefs").EnumerateArray().Single(x=>Text(x,"kind")=="audio.speech");
                playInputs.Add(Identity(audio));
            }
            else
            {
                Require(status=="completed","playback.business-worker-not-completed");
                if(stage=="speech-synthesize"){
                    synth++;Require(Text(request.GetProperty("payload").GetProperty("playbackPolicy"),"replay")=="from_start","playback.from-start-policy-drift");
                    var audio=result.GetProperty("outputs").EnumerateArray().Single(x=>Text(x,"kind")=="audio.speech");audioIdentity=Identity(audio);
                    var manifestPath=Inside(job,Text(audio,"manifestPath"));Require(Hash(manifestPath).Equals(Text(audio,"expectedSha256"),StringComparison.OrdinalIgnoreCase),"playback.audio-manifest-drift");
                    using var manifest=JsonDocument.Parse(File.ReadAllBytes(manifestPath));var file=manifest.RootElement.GetProperty("files").EnumerateArray().Single(x=>Text(x,"mediaType").StartsWith("audio/",StringComparison.Ordinal));
                    var payloadPath=Inside(job,Text(file,"path"));audioHash=Hash(payloadPath);
                    Require(new FileInfo(payloadPath).Length==file.GetProperty("bytes").GetInt64()&&audioHash.Equals(Text(file,"sha256"),StringComparison.OrdinalIgnoreCase),"playback.audio-payload-drift");
                    evidence.Add(new{manifestPath,manifestSha256=Hash(manifestPath),payloadPath,audioSha256=audioHash,bytes=new FileInfo(payloadPath).Length});
                } else if(stage=="content.understand-and-card")content++;
                else if(stage=="anki-export")exporter++;
                else throw new InvalidOperationException("playback.unexpected-stage:"+stage);
            }
            var execution=Path.Combine(Path.GetDirectoryName(requestPath)!,"worker-execution.evidence.v1.json");
            Require(File.Exists(execution),"playback.production-worker-execution-proof-missing");
            evidence.Add(new{stage,requestPath,requestSha256=Hash(requestPath),resultPath,resultSha256=Hash(resultPath),execution,executionSha256=Hash(execution)});
        }
        RequireCounts(synth,play,content,exporter,rounds.Count,expectedExport);
        Require(audioHash is not null&&audioIdentity is not null&&playInputs.All(x=>x==audioIdentity),"playback.replay-did-not-reuse-same-audio");
        return new(synth,play,content,exporter,audioHash!,evidence);
    }
    static string Identity(JsonElement value)=>Text(value,"artifactRef")+":"+Text(value,"manifestPath")+":"+Text(value,"expectedSha256").ToUpperInvariant();
    internal static void RequireCounts(int synth,int play,int content,int exporter,int expectedPlay,int expectedExport)
        =>Require(synth==1&&play==expectedPlay&&content==1&&exporter==expectedExport,"playback.worker-count-mismatch");
}
