using System.Text;

namespace KnowledgeClipper.Workflow;

// Deterministic native LaTeX grid cells. No model, HTML importer or bitmap conversion.
internal static class TableMath
{
    public static string Cell(IEnumerable<RemNoteRichPart> parts, bool header)
    {
        var lines = new List<StringBuilder> { new() };var width = 0d;
        foreach (var part in parts)
        {
            if (part.Kind == "latex") { lines[^1].Append('{').Append(part.Text).Append('}');width += 8;continue; }
            if (part.Kind != "text") throw new InvalidDataException("表格中存在无法保留的内容。");
            var text = new StringBuilder();
            void Flush(){if(text.Length==0)return;lines[^1].Append(header ? @"\textbf{" : @"\text{").Append(text).Append('}');text.Clear();}
            foreach (var rune in (part.Text ?? "").EnumerateRunes())
            {
                if(rune.Value=='\r')continue;
                var size=rune.Value>255?1d:.55;
                if(rune.Value=='\n'||width+size>16){Flush();lines.Add(new());width=0;if(rune.Value=='\n')continue;}
                text.Append(rune.Value switch {
                    '\\'=>@"\textbackslash{}",'{'=>@"\{",'}'=>@"\}",'$'=>@"\$",'&'=>@"\&",'#'=>@"\#",'%'=>@"\%",'_'=>@"\_",'^'=>@"\textasciicircum{}",'~'=>@"\textasciitilde{}",_=>rune.ToString()});
                width+=size;
            }
            Flush();
        }
        if(lines.All(line=>line.Length==0))return @"\text{ }";
        return lines.Count==1?lines[0].ToString():@"\begin{gathered}"+string.Join(@"\\",lines)+@"\end{gathered}";
    }
}
