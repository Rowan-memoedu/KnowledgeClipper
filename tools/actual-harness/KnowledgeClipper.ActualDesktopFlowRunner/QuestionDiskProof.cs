using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record QuestionTurnProof(string SessionId,string ConfigIdentity,string ConfigPayloadSha256,string InputSha256,object[] Evidence);
internal static class QuestionDiskProof
{
    static string Root(string data)=>Path.Combine(Path.GetFullPath(data),"KnowledgeClipper","core-v3");
    static string Token(string value){QuestionRegression.Require(Regex.IsMatch(value,"^[A-Za-z0-9._-]{1,128}$"),"question.proof-token-invalid");return value;}
    static string Text(JsonElement value,string key)=>value.GetProperty(key).GetString()??throw new InvalidOperationException("question.proof-string-required:"+key);
    internal static QuestionTurnProof Read(string data,string operation,string expectedInput,string expectedDeck)
    {
        var root=Root(data);var events=Events(root,operation);
        var terminal=events.Last(x=>Text(x,"eventType")=="operation.completed");
        QuestionRegression.Require(Text(terminal,"streamId")==operation,"question.proof-operation-drift");
        var command=Text(terminal,"correlationId");
        QuestionRegression.Require(events.Count(x=>Text(x,"eventType")=="command.accepted"&&Text(x,"correlationId")==command)==1,"question.proof-own-accepted-missing");
        var result=terminal.GetProperty("data").GetProperty("result");
        QuestionRegression.Require(Text(result,"pipeline")=="awaiting_card_confirmation"&&result.GetProperty("externalWriteCount").GetInt32()==0,"question.proof-nonready-or-effect");
        var session=Text(result,"sessionId");var sessionEvents=Events(root,"question-session-"+Token(session));
        var answered=sessionEvents.Single(x=>Text(x,"eventType")=="question.answer-recorded"&&Text(x,"correlationId")==command);
        var input=answered.GetProperty("artifacts")[0];var inputProof=Artifact(root,input);
        QuestionRegression.Require(Encoding.UTF8.GetString(inputProof.Bytes)==expectedInput,"question.proof-input-mismatch");
        var config=terminal.GetProperty("artifacts").EnumerateArray().Single(x=>Text(x,"kind") is "anki-export-config" or "anki-configuration");
        var configProof=Artifact(root,config);
        using var parsed=JsonDocument.Parse(configProof.Bytes);
        var routes=parsed.RootElement.GetProperty("deck_routes").EnumerateObject().ToArray();
        QuestionRegression.Require(routes.Length==1&&routes[0].Name=="canary"&&routes[0].Value.GetString()==expectedDeck,"question.proof-frozen-deck-mismatch");
        var configIdentity=Text(config,"artifactId")+":"+Text(config.GetProperty("manifestDigest"),"value");
        return new(session,configIdentity,QuestionRegression.Hash(configProof.Bytes),QuestionRegression.Hash(inputProof.Bytes),[inputProof.Evidence,configProof.Evidence,new{operation,command,terminalSequence=terminal.GetProperty("sequence").GetInt64(),session}]);
    }
    static JsonElement[] Events(string root,string stream)
    {
        var directory=Path.Combine(root,"events-v3","streams",Token(stream));
        return Directory.GetFiles(directory,"*.events.ndjson").Order(StringComparer.Ordinal).SelectMany(path=>File.ReadAllLines(path).Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>{using var doc=JsonDocument.Parse(line);return doc.RootElement.Clone();})).ToArray();
    }
    static (byte[] Bytes,object Evidence) Artifact(string root,JsonElement reference)
    {
        var artifactRoot=Path.Combine(root,"artifact-store-v3","artifacts",Token(Text(reference,"artifactId")));
        var manifest=Path.Combine(artifactRoot,"artifact.json");var raw=File.ReadAllBytes(manifest);
        QuestionRegression.Require(string.Equals(QuestionRegression.Hash(raw),Text(reference.GetProperty("manifestDigest"),"value"),StringComparison.OrdinalIgnoreCase),"question.proof-manifest-hash-drift");
        using var doc=JsonDocument.Parse(raw);var envelope=doc.RootElement;
        foreach(var field in new[]{"artifactId","kind","contract","storeId"})QuestionRegression.Require(Text(envelope,field)==Text(reference,field),"question.proof-artifact-identity-drift");
        var primary=envelope.GetProperty("representations").EnumerateArray().Single(x=>Text(x,"id")==Text(envelope,"primaryRepresentation"));
        var path=Path.GetFullPath(Path.Combine(artifactRoot,Text(primary,"path")));
        QuestionRegression.Require(path.StartsWith(artifactRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"question.proof-payload-path-escape");
        var bytes=File.ReadAllBytes(path);
        QuestionRegression.Require(bytes.LongLength==primary.GetProperty("bytes").GetInt64()&&string.Equals(QuestionRegression.Hash(bytes),Text(primary.GetProperty("digest"),"value"),StringComparison.OrdinalIgnoreCase),"question.proof-payload-hash-drift");
        return(bytes,new{manifest,manifestSha256=QuestionRegression.Hash(raw),path,payloadSha256=QuestionRegression.Hash(bytes),bytes=bytes.Length});
    }
    internal static object Workers(string data,IReadOnlyList<string> operations,int expectedExport)
    {
        var root=Root(data);var jobs=Path.Combine(root,"worker-process-v3","jobs");
        var requests=Directory.GetFiles(jobs,"request.json",SearchOption.AllDirectories);
        QuestionRegression.Require(requests.Length<=32,"question.worker-ledger-limit");
        var evidence=new List<object>();var exports=0;var content=0;
        foreach(var requestPath in requests)
        {
            using var req=JsonDocument.Parse(File.ReadAllBytes(requestPath));var request=req.RootElement;
            var op=Text(request,"operationId");var stage=Text(request,"stageId");
            QuestionRegression.Require(operations.Contains(op),"question.unexpected-operation-worker");
            QuestionRegression.Require(stage is "content.understand-and-card" or "anki-export","question.unexpected-worker-kind");
            if(stage=="anki-export")exports++;else content++;
            var resultPath=Path.Combine(Path.GetDirectoryName(requestPath)!,"result.json");
            using var res=JsonDocument.Parse(File.ReadAllBytes(resultPath));var result=res.RootElement;
            foreach(var field in new[]{"taskId","runId","stageId","attemptId","requestId","operationId"})QuestionRegression.Require(Text(request,field)==Text(result,field),"question.worker-result-identity-drift");
            QuestionRegression.Require(Text(result,"status")=="completed","question.worker-noncompleted-no-retry");
            if(stage=="anki-export")QuestionRegression.Require(op==operations[^1],"question.exported-nonfinal-operation");
            evidence.Add(new{op,stage,requestPath,requestSha256=QuestionRegression.FileHash(requestPath),resultPath,resultSha256=QuestionRegression.FileHash(resultPath)});
        }
        QuestionRegression.Require(exports==expectedExport&&content==operations.Count*2,"question.worker-invocation-count-mismatch");
        var streams=Directory.GetDirectories(Path.Combine(root,"events-v3","streams"));
        var questionOps=streams.SelectMany(path=>Directory.GetFiles(path,"*.events.ndjson")).SelectMany(File.ReadLines)
            .Select(line=>{using var d=JsonDocument.Parse(line);return d.RootElement.Clone();})
            .Where(x=>Text(x,"eventType")=="command.accepted"&&Text(x,"operation").StartsWith("question.",StringComparison.Ordinal)).ToArray();
        QuestionRegression.Require(questionOps.Length==operations.Count+expectedExport&&questionOps.All(x=>operations.Contains(Text(x,"streamId"))),"question.extra-ui-command-detected");
        return new{status="PASS",contentInvocations=content,exporterInvocations=exports,speech=0,ocr=0,remnote=0,requests=evidence,lowLevelMutationCountNotInferred=true};
    }

    internal static int SelfTest(string evidenceRoot)
    {
        QuestionRegression.Require(evidenceRoot.StartsWith("D:\\Artifacts\\",StringComparison.OrdinalIgnoreCase)&&!Directory.Exists(evidenceRoot),"question.test-root-must-be-new-D-artifact");
        Directory.CreateDirectory(evidenceRoot);var results=new List<object>();
        foreach(var test in new[]{"valid","input-drift","payload-drift","manifest-drift","wrong-deck","unexpected-export","foreign-operation"})
        {
            var data=Path.Combine(evidenceRoot,test);var root=Root(data);
            void Write(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var file=new FileStream(path,FileMode.CreateNew);JsonSerializer.Serialize(file,value);}
            object Ref(string id,string kind,byte[] bytes)
            {
                var dir=Path.Combine(root,"artifact-store-v3","artifacts",id);Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir,"content.txt"),bytes);
                var manifest=Path.Combine(dir,"artifact.json");Write(manifest,new{artifactId=id,kind,contract="1.0.0",storeId="kernel-v3",primaryRepresentation="primary",representations=new[]{new{id="primary",path="content.txt",bytes=bytes.Length,digest=new{algorithm="sha256",value=QuestionRegression.Hash(bytes)}}}});
                return new{artifactId=id,kind,contract="1.0.0",storeId="kernel-v3",manifestDigest=new{algorithm="sha256",value=QuestionRegression.FileHash(manifest)}};
            }
            var input=Ref("input","question-text",Encoding.UTF8.GetBytes("synthetic input"));
            var config=Ref("config","anki-export-config",JsonSerializer.SerializeToUtf8Bytes(new{deck_routes=new Dictionary<string,string>{{"canary",test=="wrong-deck"?"not-authorized":QuestionRegression.CanaryDeck}}}));
            var stream=Path.Combine(root,"events-v3","streams","op");Directory.CreateDirectory(stream);
            var ev=new object[]{new{streamId="op",eventType="command.accepted",correlationId="command",operation="question.initial",sequence=1},new{streamId="op",eventType="operation.completed",correlationId="command",operation="question.initial",sequence=2,data=new{result=new{pipeline="awaiting_card_confirmation",externalWriteCount=0,sessionId="session"}},artifacts=new[]{config}}};
            File.WriteAllLines(Path.Combine(stream,"0001.events.ndjson"),ev.Select(x=>JsonSerializer.Serialize(x)),new UTF8Encoding(false));
            var sessionStream=Path.Combine(root,"events-v3","streams","question-session-session");Directory.CreateDirectory(sessionStream);
            File.WriteAllText(Path.Combine(sessionStream,"0001.events.ndjson"),JsonSerializer.Serialize(new{streamId="question-session-session",eventType="question.answer-recorded",correlationId="command",operation="question.initial",artifacts=new[]{input}}),new UTF8Encoding(false));
            for(var i=0;i<2;i++)
            {
                var attempt=Path.Combine(root,"worker-process-v3","jobs","job","runs","run","stages","content","attempts","attempt"+i);
                var identity=new{taskId="task",runId="run",stageId=test=="unexpected-export"&&i==0?"anki-export":"content.understand-and-card",attemptId="attempt"+i,requestId="request"+i,operationId=test=="foreign-operation"?"foreign":"op",status="completed"};
                Write(Path.Combine(attempt,"request.json"),identity);Write(Path.Combine(attempt,"result.json"),identity);
            }
            if(test=="payload-drift")File.AppendAllText(Path.Combine(root,"artifact-store-v3","artifacts","config","content.txt")," ");
            if(test=="manifest-drift")File.AppendAllText(Path.Combine(root,"artifact-store-v3","artifacts","config","artifact.json")," ");
            var expected=test switch{"input-drift"=>"question.proof-input-mismatch","payload-drift"=>"question.proof-payload-hash-drift","manifest-drift"=>"question.proof-manifest-hash-drift","wrong-deck"=>"question.proof-frozen-deck-mismatch","unexpected-export"=>"question.worker-invocation-count-mismatch","foreign-operation"=>"question.unexpected-operation-worker",_=>"PASS"};
            var observed="PASS";
            try{_=Read(data,"op",test=="input-drift"?"different":"synthetic input",QuestionRegression.CanaryDeck);_=Workers(data,["op"],0);}
            catch(InvalidOperationException ex){observed=ex.Message;}
            QuestionRegression.Require(observed==expected,"question.disk-test-unexpected:"+test+":"+observed);
            results.Add(new{test,status="PASS",expected,observed});
        }
        var receipt=new{status="PASS",diskProofCases=results.Count,results,externalCalls=0,workerInvocations=0,liveExecuted=false};
        using(var output=new FileStream(Path.Combine(evidenceRoot,"receipt.json"),FileMode.CreateNew))JsonSerializer.Serialize(output,receipt,new JsonSerializerOptions{WriteIndented=true});
        Console.WriteLine(JsonSerializer.Serialize(receipt));return 0;
    }
}
