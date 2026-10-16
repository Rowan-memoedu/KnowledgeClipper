using System.Text.Json;
using KnowledgeClipper.Workflow;

internal static class IntegrationLive
{
    internal static async Task Run(string repo, string output, string provider)
    {
        var data = Path.Combine(output, "integration-live", provider); Directory.CreateDirectory(data);
        var store = new DraftStore(data); var idFile = Path.Combine(data, "draft-id.txt"); ClipDraft draft;
        if (File.Exists(idFile)) draft = store.Find(File.ReadAllText(idFile))!;
        else
        {
            var material = new CaptureMaterial("公开整合多卡验收", null, "generated_allowed", null, "public", 0, DateTimeOffset.UtcNow);
            var root = new ChatSession { Messages = [
                new() { Id = "rectangle-question", Role = "user", Parts = [new("text", "学习目标甲：矩形长7厘米、宽4厘米，求面积并说明单位。")] },
                new() { Id = "rectangle-answer", Role = "assistant", Parts = [new("text", "矩形面积 $S=ab=7\\times4=28$ 平方厘米；长度单位平方后得到面积单位。")] }
            ], Auxiliary = new() { Provider = provider } };
            var fork = ChatWorkspace.Fork(root, "rectangle-answer");
            fork.Session.Messages.AddRange([
                new() { Id = "prime-question", Role = "user", Parts = [new("text", "学习目标乙：定义素数，并说明2为什么是唯一的偶素数。")] },
                new() { Id = "prime-answer", Role = "assistant", Parts = [new("text", "素数是大于1、只有1和自身两个正因数的整数。2是素数；其他正偶数若大于2都能被2整除，所以2是唯一的偶素数。")] }
            ]);
            ChatWorkspace.New(root);
            draft = new() { Original = material, Material = material,
                Settings = WorkflowJson.Read<LanguageSettings>(@"D:\AppData\KnowledgeClipper\language-settings.json"),
                Configuration = ModeConfigurationLoader.Load(Path.Combine(repo, "modes")), ModeId = "question", Chat = root };
            store.Save(draft); File.WriteAllText(idFile, draft.Id);
        }
        var chat = draft.Chat!; var config = new ChatConfigurationLoader(Path.Combine(repo, "modes", "chat"), Path.Combine(data, "modes"));
        using var http = new HttpClient();
        var auxiliary = chat.Auxiliary!.Current;
        var coordinator = new ChatCoordinator(draft, store, config, new(Path.Combine(repo, "helpers", "codex_chat.py"), data), http,
            () => ContentCredentials.Resolve(), data, auxiliary);
        var models = await coordinator.ModelsAsync(provider); WorkflowJson.Save(Path.Combine(data, "models.json"), models);
        if (string.IsNullOrWhiteSpace(auxiliary.Model))
        {
            auxiliary.Model = (provider == "api" ? models.FirstOrDefault(m => m.Id == draft.Settings.ImageModel) ?? models.First(m => m.Id.Contains("flash", StringComparison.OrdinalIgnoreCase))
                : models.First(m => m.Id == ChatTypeDefaults.Model)).Id;
            auxiliary.Effort = provider == "codex" ? "low" : null; store.Save(draft);
        }
        var requirements = new[] { "", "请生成两张卡片，分别整合两个独立的学习目标。", "请生成3张卡片，分别讲矩形面积、素数的定义、2为什么是唯一的偶素数。",
            "只整合当前手写的矩形问题，参考对话补足答案，不扩展其他主题。" };
        var receipts = new List<object>();
        for (var index = 0; index < requirements.Length; index++)
        {
            var stageFile = Path.Combine(data, $"stage-{index}.json"); string proposalId;
            if (File.Exists(stageFile)) proposalId = JsonDocument.Parse(File.ReadAllText(stageFile)).RootElement.GetProperty("proposalId").GetString()!;
            else
            {
                var card = new CardComposition { TypeId = "fast-answer" }; chat.Compositions.Add(card);
                if (index == 1)
                {
                    ChatComposition.Select(chat, card, "rectangle-question", "front", true);
                    ChatComposition.Select(chat, card, "prime-answer", "back", true);
                    ChatComposition.Edit(card, card.Front + "\n手工补充：问题要能独立理解。", card.Back, "fast-answer");
                }
                if (index == 3) ChatComposition.Edit(card, "矩形长7厘米、宽4厘米，求面积并解释面积单位。", "", "fast-answer");
                var proposal = ChatComposition.Begin(chat, card, "integrate", requirements[index], ["fast-answer"], contextSession: ChatWorkspace.Active(chat));
                proposalId = proposal.Id; store.Save(draft);
                WorkflowJson.Save(stageFile, new { proposalId, cardId = card.Id, requested = index == 1 ? (int?)2 : index == 2 ? 3 : null });
            }
            var current = chat.Proposals.Single(p => p.Id == proposalId);
            if (current.Status != "adopted")
            {
                var attempted = auxiliary.Prompts.SingleOrDefault(p => p.Input == current.InputSnapshot);
                if (attempted == null)
                {
                    if (auxiliary.Binding.NeedsReconciliation) throw new InvalidOperationException("Earlier auxiliary result is uncertain; retain its original identity.");
                    auxiliary.TypeId = "fast-answer"; auxiliary.IncludeExamples = false;
                    Console.WriteLine($"LIVE {provider} integration stage {index}, model {auxiliary.Model}");
                    try { await coordinator.SendAsync(models, "integrate", current.InputSnapshot, ChatComposition.ProposalImages(current, chat)); }
                    catch { current.Status = "incomplete"; store.Save(draft); throw; }
                    attempted = auxiliary.Prompts.Single(p => p.Input == current.InputSnapshot);
                }
                if (auxiliary.Binding.NeedsReconciliation && provider == "codex") await coordinator.RecoverAsync();
                if (attempted.Stage != "completed") throw new InvalidOperationException("Live integration is not complete; no blind resend.");
                var raw = string.Join("\n", auxiliary.Messages.Where(m => m.Role == "assistant" && m.NativeTurnId == attempted.TurnId).Select(m => m.Text));
                ChatComposition.Parse(current, raw, chat, ["fast-answer"]); store.Save(draft);
                if (current.Status != "preview") throw new InvalidDataException(current.Error);
                if (index == 0 && current.Cards.Count < 2) throw new InvalidDataException("Independent learning goals were collapsed into one card.");
                ChatComposition.Adopt(chat, current); store.Save(draft);
            }
            var validation = new ComposeProposal { Action = current.Action, CompositionId = current.CompositionId, InputSnapshot = current.InputSnapshot };
            ChatComposition.Parse(validation, current.RawOutput, chat, ["fast-answer"]);
            if (validation.Status != "preview") throw new InvalidDataException(validation.Error);
            if (current.Cards.Select(c => c.Front).Distinct().Count() != current.Cards.Count || current.Cards.Any(c => string.IsNullOrWhiteSpace(c.Back)))
                throw new InvalidDataException("Live cards contain duplicates or empty answers.");
            if (index == 3 && (current.Cards.Count != 1 || current.Cards.Any(c => (c.Front + c.Back).Contains("素数"))))
                throw new InvalidDataException("Focused manual material was expanded into unrelated conversation goals.");
            var snapshot = auxiliary.Prompts.Single(p => p.Input == current.InputSnapshot);
            receipts.Add(new { stage = index, proposalId, count = current.Cards.Count, current.Status, snapshot.Model,
                snapshot.TurnId, auxiliary.Binding.ThreadId, cards = current.Cards.Select(c => new { c.Id, c.Front, c.Back, c.TypeId }) });
            Console.WriteLine($"PASS LIVE {provider} integration stage {index}: {current.Cards.Count} actual cards");
        }
        WorkflowJson.Save(Path.Combine(data, "acceptance.json"), new { status = "PASS", provider, draft.Id, stages = receipts,
            mainMessageCount = ChatWorkspace.Messages(chat).Count(), modelTurns = auxiliary.Prompts.Count, auxiliary.Binding.ThreadId,
            activeConversationRemainsBlank = ChatWorkspace.Active(chat).Messages.Count == 0 });
    }
}
