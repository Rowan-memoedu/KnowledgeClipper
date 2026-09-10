using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record SequenceSource(string SelectedText,string Prefix,string Suffix)
{
    internal string SelectedSha256=>Hash(SelectedText);
    internal string CompleteSha256=>Hash(Prefix+SelectedText+Suffix);
    internal static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
internal sealed record FlowStep(string CaptureAction,string LanguageMode,SequenceSource? Source=null)
{
    internal int CardCount=>LanguageMode=="both"?2:1;
    internal bool IncludeSpeech=>CaptureAction=="read.capture";
    internal void Validate()
    {
        if(CaptureAction is not ("read.capture" or "clip.capture")||LanguageMode is not ("meaning-reading" or "writing" or "both"))
            throw new InvalidOperationException("sequence.invalid-action-or-language-mode");
    }
}
internal static class SequencePlan
{
    internal static IReadOnlyList<FlowStep> Load(string? path)
    {
        if(path is null)throw new InvalidOperationException("sequence.explicit-capture-queue-required");
        return Parse(File.ReadAllBytes(path));
    }
    internal static IReadOnlyList<FlowStep> Parse(byte[] bytes)
    {
        if(bytes.Length>64000)throw new InvalidOperationException("sequence.capture-queue-too-large");
        using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"inputs","schemaVersion"})||
           root.GetProperty("schemaVersion").GetString()!="desktop-test-capture-queue.v1"||root.GetProperty("inputs").GetArrayLength()!=4)
            throw new InvalidOperationException("sequence.exact-four-input-queue-required");
        var sources=new List<SequenceSource>();
        foreach(var input in root.GetProperty("inputs").EnumerateArray())
        {
            if(!input.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"prefix","selectedText","suffix"}))throw new InvalidOperationException("sequence.capture-queue-fields-invalid");
            var selected=input.GetProperty("selectedText").GetString();var prefix=input.GetProperty("prefix").GetString();var suffix=input.GetProperty("suffix").GetString();
            if(string.IsNullOrWhiteSpace(selected)||selected.Length>5000||prefix is null||suffix is null||prefix.Length>10000||suffix.Length>10000)throw new InvalidOperationException("sequence.capture-queue-text-invalid");
            sources.Add(new(selected,prefix,suffix));
        }
        return new[]{new FlowStep("read.capture","meaning-reading",sources[0]),new FlowStep("read.capture","writing",sources[1]),new FlowStep("clip.capture","both",sources[2]),new FlowStep("read.capture","both",sources[3])};
    }
}
