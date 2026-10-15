using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeClipper.Workflow;

internal static class CorpusChecks
{
    internal static void Run(string source,string output)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(source));var reports=new List<object>();var media=new Dictionary<string,ImageAsset>();
        string Material(string value) => Regex.Replace(value,@"!\[([^\]]*)\]\((file:///[^)]+)\)",match=>{
            var path=new Uri(match.Groups[2].Value).LocalPath;var bytes=File.ReadAllBytes(path);var sha=ImageAsset.Hash(bytes);
            media[sha]=new(path,sha,"question",1);media[sha].ReadVerified();return "!["+match.Groups[1].Value+"](asset:"+sha+")";
        });
        string Outline(JsonElement nodes,int level=0) {
            var text=new StringBuilder();foreach(var node in nodes.EnumerateArray()) {
                var lines=Material(node.GetProperty("text").GetString()!).Replace("\r\n","\n").Split('\n');
                text.Append(' ',level*2).Append("- ").AppendLine(lines[0]);
                foreach(var line in lines.Skip(1))text.Append(' ',level*2+2).AppendLine(line);
                if(node.TryGetProperty("children",out var children))text.Append(Outline(children,level+1));
            }return text.ToString();
        }
        foreach(var card in document.RootElement.GetProperty("cards").EnumerateArray()){
            var front=Material(card.GetProperty("front").GetString()!);var back=Outline(card.GetProperty("back"));
            var rendered=ChatMarkdown.Render(front,back,media.Values.ToArray());
            var parts=rendered.Card.Front.Concat(rendered.Card.Back.SelectMany(r=>r)).ToArray();
            // This reviewed corpus uses dollar math without code or currency. Its independent
            // source scan checks the AST converter without putting source-specific rules in production.
            var expected=Regex.Matches(front+"\n"+back,@"\$\$(.*?)\$\$|\$([^$]*?)\$",RegexOptions.Singleline)
                .Select(m=>new{Text=m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value,Block=m.Groups[1].Success}).ToArray();
            var actual=parts.Where(p=>p.Kind=="latex").Select(p=>new{Text=p.Text!.Trim(),Block=p.Block==true}).ToArray();
            if(!expected.Select(p=>new{Text=p.Text.Trim(),p.Block}).SequenceEqual(actual))throw new Exception("Corpus formula source/order changed: "+card.GetProperty("id").GetString());
            var sourceArrows=(front+back).Count(c=>c is '→' or '←' or '↔');
            if(parts.Sum(p=>(p.Text??"").Count(c=>c is '→' or '←' or '↔'))!=sourceArrows)throw new Exception("Corpus literal arrow lost");
            if(rendered.Card.BackParents==null||rendered.Card.Back.Length==0)throw new Exception("Corpus hierarchy lost");
            reports.Add(new{id=card.GetProperty("id").GetString(),rows=rendered.Card.Back.Length,inline=parts.Count(p=>p.Kind=="latex"&&p.Block!=true),display=parts.Count(p=>p.Kind=="latex"&&p.Block==true),images=parts.Count(p=>p.Kind=="image"),literalArrows=sourceArrows});
        }
        WorkflowJson.Save(Path.Combine(output,"corpus-compatibility.json"),new{sourceSha256=ImageAsset.Hash(File.ReadAllBytes(source)),cards=reports,uniqueImages=media.Count,noExternalWrites=true});
        Console.WriteLine($"PASS {reports.Count} existing cards converted locally, {media.Count} original media verified; zero external writes");
    }
}
