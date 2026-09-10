using System.Text.Json;
using System.Windows.Automation;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed class UiaPreviewSource(Func<string,AutomationElement> find,string listId,string frontId,string backId,Action<int>? afterFrontRead=null):IRenderedPreviewSource
{
    public RenderedPreviewIdentity? ReadIdentity()
    {
        try
        {
            var help=find(listId).Current.HelpText;
            if(string.IsNullOrWhiteSpace(help))return null;
            using var doc=JsonDocument.Parse(help);var root=doc.RootElement;
            if(root.GetProperty("schemaVersion").GetString()!="knowledge-clipper-rendered-preview.v1")throw new InvalidOperationException("preview.rendered-identity-schema-invalid");
            var ready=root.GetProperty("ready").GetBoolean();
            if(root.GetProperty("operationId").ValueKind==JsonValueKind.Null&&!ready)return null;
            var identity=new RenderedPreviewIdentity(root.GetProperty("operationId").GetString()!,root.GetProperty("draftSha256").GetString()!,root.GetProperty("revision").GetString()!,root.GetProperty("renderGeneration").GetInt64(),root.GetProperty("cardCount").GetInt32(),ready);
            if(string.IsNullOrWhiteSpace(identity.OperationId)||identity.DraftSha256.Length!=64||string.IsNullOrWhiteSpace(identity.Revision)||identity.RenderGeneration<0||identity.CardCount<0)throw new InvalidOperationException("preview.rendered-identity-invalid");
            return identity;
        }
        catch(ElementNotAvailableException){return null;}
    }
    public int ReadCount()
    {
        try{return find(listId).FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Count;}
        catch(ElementNotAvailableException ex){throw new PreviewStaleElementException(ex);}
    }
    public PreviewCard ReadCard(int ordinal)
    {
        try
        {
            var before=ReadIdentity();
            var list=find(listId);
            var items=list.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem));
            if(ordinal>=items.Count)throw new PreviewStaleElementException();
            if(!items[ordinal].TryGetCurrentPattern(SelectionItemPattern.Pattern,out var selected))
            {
                var after=ReadIdentity();
                if(after is null||before is null||after.RenderGeneration!=before.RenderGeneration)throw new PreviewStaleElementException();
                throw new InvalidOperationException("uia.preview-selection-pattern-missing");
            }
            ((SelectionItemPattern)selected).Select();
            var front=ReadValue(frontId);afterFrontRead?.Invoke(ordinal);var back=ReadValue(backId);
            return new(ordinal,front,back);
        }
        catch(ElementNotAvailableException ex){throw new PreviewStaleElementException(ex);}
    }
    string ReadValue(string id)
    {
        var element=find(id);
        if(!element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern))throw new InvalidOperationException("uia.preview-value-pattern-missing:"+id);
        return ((ValuePattern)pattern).Current.Value;
    }
}
