using System.Security.Cryptography;
using System.Text.Json;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record CompletedFlowStep(string OperationId,bool IncludeSpeech,int CardCount);
internal static class SequenceWorkerLedger
{
    internal static object Verify(string dataRoot,IReadOnlyList<CompletedFlowStep> completed)
    {
        var jobs=Path.Combine(Path.GetFullPath(dataRoot),"KnowledgeClipper","core-v3","worker-process-v3","jobs");
        var all=Directory.GetFiles(jobs,"request.json",SearchOption.AllDirectories);
        if(all.Length>256)throw new InvalidOperationException("sequence.worker-ledger-limit");
        var rows=new List<object>();
        foreach(var step in completed)
        {
            var stages=new List<string>();var evidence=new List<object>();
            foreach(var requestPath in all)
            {
                using var request=JsonDocument.Parse(File.ReadAllBytes(requestPath));var req=request.RootElement;
                if(req.GetProperty("operationId").GetString()!=step.OperationId)continue;
                var resultPath=Path.Combine(Path.GetDirectoryName(requestPath)!,"result.json");
                using var result=JsonDocument.Parse(File.ReadAllBytes(resultPath));var res=result.RootElement;
                foreach(var field in new[]{"taskId","runId","stageId","attemptId","requestId","operationId"})
                    if(req.GetProperty(field).GetString()!=res.GetProperty(field).GetString())throw new InvalidOperationException("sequence.worker-ledger-identity-drift");
                if(res.GetProperty("status").GetString()!="completed")throw new InvalidOperationException("sequence.worker-ledger-not-completed");
                var stage=req.GetProperty("stageId").GetString()!;stages.Add(stage);
                evidence.Add(new{stage,requestPath,requestSha256=Hash(requestPath),resultPath,resultSha256=Hash(resultPath)});
            }
            var synth=stages.Count(s=>s=="speech-synthesize");var play=stages.Count(s=>s=="speech-playback");var export=stages.Count(s=>s=="anki-export");
            RequireCounts(step.IncludeSpeech,synth,play,export);
            rows.Add(new{step.OperationId,synthesisInvocations=synth,playbackInvocations=play,exporterInvocations=export,logicalCards=step.CardCount,evidence});
        }
        return new{status="PASS",operations=rows,exporterInvocations=completed.Count,logicalCards=completed.Sum(s=>s.CardCount),synthesisInvocations=completed.Count(s=>s.IncludeSpeech),playbackInvocations=completed.Count(s=>s.IncludeSpeech),lowLevelMutationCountNotInferred=true,readOnly=true};
    }
    internal static void RequireCounts(bool speech,int synth,int play,int export)
    {
        if(synth!=(speech?1:0)||play!=(speech?1:0)||export!=1)throw new InvalidOperationException("sequence.worker-ledger-invocation-count-mismatch");
    }
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
