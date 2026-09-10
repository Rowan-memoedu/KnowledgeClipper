using System.Diagnostics;

namespace KnowledgeClipper.ActualDesktopFlowRunner;

internal sealed record RenderedPreviewIdentity(string OperationId,string DraftSha256,string Revision,long RenderGeneration=0,int CardCount=-1,bool Ready=true)
{
    internal bool SameDraft(RenderedPreviewIdentity other)=>OperationId==other.OperationId&&DraftSha256==other.DraftSha256&&Revision==other.Revision;
}
internal sealed class PreviewStaleElementException(Exception? inner=null) : Exception("preview.stale-ui-reference",inner);
internal sealed class PreviewRenderPendingException : Exception;
internal interface IRenderedPreviewSource
{
    RenderedPreviewIdentity? ReadIdentity();
    int ReadCount();
    PreviewCard ReadCard(int ordinal);
}
internal static class PreviewReadBarrier
{
    internal static IReadOnlyList<PreviewCard> Read(IRenderedPreviewSource source,RenderedPreviewIdentity expected,IReadOnlyList<PreviewCard> expectedCards,TimeSpan timeout,Action<string> heartbeat)
    {
        if(string.IsNullOrWhiteSpace(expected.OperationId)||expected.DraftSha256.Length!=64||string.IsNullOrWhiteSpace(expected.Revision)||expectedCards.Count==0)
            throw new InvalidOperationException("preview.expected-identity-invalid");
        var clock=Stopwatch.StartNew();var matched=false;var retries=0;long generation=0;
        while(clock.Elapsed<timeout)
        {
            heartbeat("preview.identity.poll");
            var observed=source.ReadIdentity();
            if(observed is null||!observed.SameDraft(expected)||!observed.Ready)
            {
                if(matched&&observed is not null&&!observed.SameDraft(expected))throw new InvalidOperationException("preview.rendered-draft-changed-during-read");
                Thread.Sleep(25);continue;
            }
            matched=true;generation=observed.RenderGeneration;
            try
            {
                RequireIdentity();
                var count=source.ReadCount();RequireIdentity();
                if(count!=expectedCards.Count)throw new InvalidOperationException("preview.rendered-card-count-mismatch");
                var cards=new List<PreviewCard>();
                for(var i=0;i<count;i++)
                {
                    RequireIdentity();
                    var card=source.ReadCard(i);
                    RequireIdentity();
                    if(card.Ordinal!=i||card.Front!=expectedCards[i].Front||card.Back!=expectedCards[i].Back)
                        throw new InvalidOperationException("preview.content-does-not-match-typed-draft");
                    cards.Add(card);
                }
                RequireIdentity();return cards;
            }
            catch(Exception ex)when(ex is PreviewStaleElementException or PreviewRenderPendingException)
            {
                var current=source.ReadIdentity();
                if(current is not null&&!current.SameDraft(expected))throw new InvalidOperationException("preview.rendered-draft-changed-during-read",ex);
                if(++retries>3)throw new InvalidOperationException("preview.render-reread-limit",ex);
                heartbeat("preview.render-reread");
            }
        }
        throw new InvalidOperationException("preview.rendered-identity-timeout");

        void RequireIdentity()
        {
            var value=source.ReadIdentity();
            if(value is null)throw new PreviewRenderPendingException();
            if(!value.SameDraft(expected))throw new InvalidOperationException("preview.rendered-draft-changed-during-read");
            if(!value.Ready||value.RenderGeneration!=generation)throw new PreviewRenderPendingException();
            if(value.CardCount>=0&&value.CardCount!=expectedCards.Count)throw new InvalidOperationException("preview.rendered-card-count-mismatch");
        }
    }

    internal static int SelfTest()
    {
        var expected=new RenderedPreviewIdentity("new-op",new('a',64),new('b',64));
        var old=expected with{OperationId="old-op"};var cards=new[]{new PreviewCard(0,"front-0","back-0"),new PreviewCard(1,"front-1","back-1")};
        var count=0;
        var delayed=new Fixture(expected,cards){Old=old,DelayedReads=2};
        if(Read(delayed,expected,cards,TimeSpan.FromSeconds(1),_=>{}).Count!=2||delayed.SelectedBeforeReady)throw new Exception("preview-delayed-identity-gate-failed");count++;
        var stable=new Fixture(expected,cards);
        if(Read(stable,expected,cards,TimeSpan.FromSeconds(1),_=>{}).Count!=2)throw new Exception("preview-stable-refresh-failed");count++;
        var stale=new Fixture(expected,cards){StaleOnce=true};
        if(Read(stale,expected,cards,TimeSpan.FromSeconds(1),_=>{}).Count!=2||stale.ReadAttempts!=3)throw new Exception("preview-stale-bounded-reread-failed");count++;
        Reject(new Fixture(expected,cards){ChangeDuringRead=true},"preview.rendered-draft-changed-during-read");
        Reject(new Fixture(expected,cards){Old=old,DelayedReads=int.MaxValue},"preview.rendered-identity-timeout");
        Reject(new Fixture(expected,cards){WrongBody=true},"preview.content-does-not-match-typed-draft");
        Reject(new Fixture(expected,cards){WrongCount=true},"preview.rendered-card-count-mismatch");
        Reject(new Fixture(expected,cards){MissingPattern=true},"uia.preview-selection-pattern-missing");
        Reject(new Fixture(expected,cards){AlwaysStale=true},"preview.render-reread-limit");
        return count;
        void Reject(Fixture fixture,string code){try{Read(fixture,expected,cards,TimeSpan.FromMilliseconds(100),_=>{});throw new Exception("preview-negative-accepted");}catch(InvalidOperationException ex)when(ex.Message==code){count++;}}
    }
    sealed class Fixture(RenderedPreviewIdentity identity,IReadOnlyList<PreviewCard> cards):IRenderedPreviewSource
    {
        internal RenderedPreviewIdentity? Old;internal int DelayedReads;internal bool StaleOnce,AlwaysStale,ChangeDuringRead,WrongBody,WrongCount,MissingPattern,SelectedBeforeReady;internal int ReadAttempts;
        public RenderedPreviewIdentity? ReadIdentity()=>DelayedReads-->0?Old:identity;
        public int ReadCount()=>WrongCount?cards.Count+1:cards.Count;
        public PreviewCard ReadCard(int ordinal)
        {
            if(DelayedReads>0)SelectedBeforeReady=true;ReadAttempts++;
            if(ChangeDuringRead)identity=identity with{Revision="changed"};
            if(AlwaysStale||StaleOnce&&ReadAttempts==1)throw new PreviewStaleElementException();
            if(MissingPattern)throw new InvalidOperationException("uia.preview-selection-pattern-missing");
            return WrongBody?cards[ordinal] with{Front="wrong"}:cards[ordinal];
        }
    }
}
