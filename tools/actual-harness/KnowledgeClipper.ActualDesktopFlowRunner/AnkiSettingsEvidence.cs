using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record SavedAnkiDestinationEvidence(string Path,string Sha256,string Preset,string RouteKey,string DeckIdentity,string DeckSha256);

internal static class AnkiSettingsEvidence
{
    internal static SavedAnkiDestinationEvidence Validate(byte[] bytes,string path,string expectedDeck)
    {
        using var document=JsonDocument.Parse(bytes);
        var root=document.RootElement;
        if(!root.TryGetProperty("schemaVersion",out var schema)||schema.GetString()!="knowledge-clipper-desktop-module-settings.v1"||
           !root.TryGetProperty("ankiDeckPreset",out var preset)||preset.GetString()!="custom"||
           !root.TryGetProperty("ankiDeckRoutes",out var routes)||routes.ValueKind!=JsonValueKind.Object||
           !routes.TryGetProperty("canary",out var deck)||deck.ValueKind!=JsonValueKind.String||deck.GetString()!=expectedDeck)
            throw new InvalidOperationException("settings.saved-anki-destination-mismatch-before-capture");
        return new(path,Convert.ToHexString(SHA256.HashData(bytes)),"custom","canary",expectedDeck,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedDeck))));
    }

    internal static SavedAnkiDestinationEvidence Wait(string dataRoot,string expectedDeck,Action heartbeat)
    {
        var path=System.IO.Path.Combine(System.IO.Path.GetFullPath(dataRoot),"KnowledgeClipper","desktop-module-settings.v1.json");
        var deadline=DateTime.UtcNow.AddSeconds(10);
        do
        {
            heartbeat();
            try { return Validate(File.ReadAllBytes(path),path,expectedDeck); }
            catch(Exception ex)when(ex is IOException or JsonException || ex is InvalidOperationException invalid&&invalid.Message=="settings.saved-anki-destination-mismatch-before-capture") { }
            Thread.Sleep(100);
        }while(DateTime.UtcNow<deadline);
        throw new InvalidOperationException("settings.saved-anki-destination-mismatch-before-capture");
    }

    internal static int SelfTest()
    {
        var passed=0;
        foreach(var deck in new[]{"零碎知识剪藏::Canary::Independent-A","零碎知识剪藏::Canary::Independent-B"})
        {
            byte[] Fixture(string preset,string key,string value)=>JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion="knowledge-clipper-desktop-module-settings.v1",ankiDeckPreset=preset,ankiDeckRoutes=new Dictionary<string,string>{{key,value}}});
            var evidence=Validate(Fixture("custom","canary",deck),"fixture",deck);
            if(evidence.DeckIdentity!=deck||evidence.Sha256.Length!=64||evidence.DeckSha256.Length!=64)throw new Exception("settings-evidence-positive-failed");
            passed++;
            foreach(var bytes in new[]{Fixture("knowledge-clipper-default","canary",deck),Fixture("custom","default",deck),Fixture("custom","canary",deck+"-other")})
            {
                try { Validate(bytes,"fixture",deck);throw new Exception("settings-evidence-negative-accepted"); }
                catch(InvalidOperationException ex)when(ex.Message=="settings.saved-anki-destination-mismatch-before-capture"){passed++;}
            }
        }
        return passed;
    }
}
