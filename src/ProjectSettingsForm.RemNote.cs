using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KnowledgeClipper.Core.V3.Client;
using KnowledgeClipper.Desktop.Adapters.V3;
using KnowledgeClipper.Desktop.Application.V3;

namespace KnowledgeClipper
{
    internal sealed partial class SettingsForm
    {
        private readonly SettingsCallbackScope remNoteScope = new();
        private readonly Dictionary<string, VerifiedRemNoteBinding> remNoteBindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RemNoteDestinationSettings> remNoteOriginalSettings = new(StringComparer.Ordinal);
        private readonly HashSet<string> remNotePendingProfiles = new(StringComparer.Ordinal);
        private bool remNoteApplyingSelector;
        private bool remNoteBusy;

        private void SetRemNoteSelector(string title)
        {
            remNoteApplyingSelector = true;
            try { remnoteRootTitle.Text = title; }
            finally { remNoteApplyingSelector = false; }
        }

        private void RemNoteSelectorChanged()
        {
            InvalidateRemNoteObservation();
            if (remNoteApplyingSelector || settingsDraft == null) return;
            var id = workflowDocument.ActiveProfileId;
            if (!remNotePendingProfiles.Contains(id) || CurrentRemNoteBinding()?.RootTitle == remnoteRootTitle.Text.Trim()) return;
            remNotePendingProfiles.Remove(id);
            remNoteBindings.Remove(id);
            remNoteOriginalSettings.TryGetValue(id, out var original);
            var profiles = new Dictionary<string, WorkflowSettingsProfile>(workflowDocument.Profiles, StringComparer.Ordinal);
            profiles[id] = profiles[id] with { Remnote = original };
            workflowDocument = workflowDocument with { Profiles = profiles };
            remnoteRootId.Text = original?.Target?.RootRemId ?? "";
            if (remnoteBoundTargetStatus != null) remnoteBoundTargetStatus.Text = "新标题未核实 · 未保存发现已撤回；原已保存绑定保留";
        }

        private VerifiedRemNoteBinding CurrentRemNoteBinding()
        {
            if (settingsDraft == null || !remNoteBindings.TryGetValue(workflowDocument.ActiveProfileId, out var binding)) return null;
            var target = workflowDocument.Profiles[workflowDocument.ActiveProfileId].Remnote?.Target;
            return target?.RootRemId == binding.RootRemId && target.RootTitle == binding.RootTitle ? binding : null;
        }

        internal void InvalidateRemNoteObservation()
        {
            remNoteScope.Invalidate();
            remNoteBusy = false;
            if (IsDisposed || Disposing) return;
            if (remnoteProbe != null) remnoteProbe.Enabled = typedApplication != null;
            if (remnoteDiscoverTarget != null) remnoteDiscoverTarget.Enabled = typedApplication != null;
            if (remnoteProbeStatus != null)
            {
                remnoteProbeStatus.Text = "未核实当前选择 · 历史绑定不代表当前连接就绪";
                remnoteProbeStatus.AccessibleDescription = "ready=false; observation=invalidated";
                remnoteProbeStatus.ForeColor = Muted;
            }
        }

        private SettingsCallbackScope.Lease BeginRemNoteObservation()
        {
            var lease = remNoteScope.Begin(workflowDocument.ActiveProfileId, remnoteRootTitle.Text.Trim(), CurrentRemNoteBinding());
            remNoteBusy = true;
            remnoteDiscoverTarget.Enabled = remnoteProbe.Enabled = false;
            return lease;
        }
        private bool CurrentRemNoteObservation(SettingsCallbackScope.Lease lease) => !IsDisposed && !Disposing &&
            remNoteScope.IsCurrent(lease, workflowDocument.ActiveProfileId, remnoteRootTitle.Text.Trim(), CurrentRemNoteBinding());
        private void FinishRemNoteObservation(SettingsCallbackScope.Lease lease)
        {
            if (CurrentRemNoteObservation(lease))
            { remNoteBusy = false; remnoteDiscoverTarget.Enabled = remnoteProbe.Enabled = typedApplication != null; }
            remNoteScope.Finish(lease);
        }

        private async Task<T> RunRemNoteActionAsync<T>(ModuleActionKind kind, object request, SettingsCallbackScope.Lease lease) where T : class
        {
            var result = await typedApplication.ModuleActionUseCases.ExecuteAsync(kind,
                new Dictionary<string, object> { ["request"] = request }, lease.Cancellation.Token);
            if (!CurrentRemNoteObservation(lease)) return null;
            if (result.Status != OperationStatus.Succeeded || result.Output == null || !result.Values.TryGetValue("result", out var value) || value is not T typed)
                throw new InvalidOperationException(result.NoticeCode ?? "remnote-settings-unverified");
            return typed;
        }

        private async Task RunRemNoteTargetDiscoveryAsync()
        {
            if (typedApplication == null || remNoteBusy) return;
            using var lease = BeginRemNoteObservation();
            remnoteDiscoverTargetStatus.Text = "发现中 · Core typed SDK · 只读";
            try
            {
                var summary = await RunRemNoteActionAsync<RemNoteDiscoverySummary>(ModuleActionKind.RemNoteDiscoverTarget,
                    new RemNoteDiscoveryRequest("desktop-discover-" + Guid.NewGuid().ToString("N"), lease.Selector), lease);
                if (summary == null) return;
                var binding = summary.Binding;
                if (!summary.PersistableBinding || summary.CandidateCount != 1 || binding == null || binding.RootTitle != lease.Selector ||
                    summary.Evidence?.Token != binding.Evidence.Token) throw new InvalidOperationException("remnote-discovery-binding-unverified");
                settingsDraft.StageVerifiedRemNoteBinding(binding.RootRemId, binding.RootTitle);
                remNoteBindings[lease.Profile] = binding;
                remNotePendingProfiles.Add(lease.Profile);
                // The verified result changes binding identity; retire the lease before updating controls.
                InvalidateRemNoteObservation();
                remnoteRootId.Text = binding.RootRemId;
                remnoteBoundTargetStatus.Text = "verified_pending · 标题=" + binding.RootTitle + " · ID=" + MaskIdentifier(binding.RootRemId);
                remnoteBoundTargetStatus.AccessibleDescription = "verified_pending; evidence=" + binding.Evidence.Token;
                remnoteDiscoverTargetStatus.Text = "唯一目标与证据已核实 · 尚未保存";
                status.Text = "发现完成；未勾选目标、未保存也可先检查。保存不触发检查或写入。";
            }
            catch (Exception ex)
            {
                if (CurrentRemNoteObservation(lease))
                { remnoteDiscoverTargetStatus.Text = "未核实 · " + ex.Message + " · 原绑定保留"; status.Text = "发现未完成；未更改绑定。"; }
            }
            finally { FinishRemNoteObservation(lease); }
        }

        private async Task RunRemNoteProbeAsync()
        {
            if (typedApplication == null || remNoteBusy) return;
            var binding = CurrentRemNoteBinding();
            if (binding == null || binding.RootTitle != remnoteRootTitle.Text.Trim())
            { remnoteProbeStatus.Text = "未运行 · 当前标题尚无已核实绑定，请先发现或恢复历史证据"; return; }
            using var lease = BeginRemNoteObservation();
            remnoteProbeStatus.Text = "检查中 · 使用已核实绑定 · 只读";
            remnoteProbeStatus.AccessibleDescription = "ready=false; observation=checking";
            try
            {
                var summary = await RunRemNoteActionAsync<RemNoteReadinessSummary>(ModuleActionKind.RemNoteProbe,
                    new RemNoteProbeRequest("desktop-probe-" + Guid.NewGuid().ToString("N"), binding), lease);
                if (summary == null) return;
                var ready = RemNoteSettingsSdkBridge.IsReady(summary);
                remnoteProbeStatus.Text = (ready ? "当前目标就绪" : "未就绪 / 未核实") + " · " + string.Join(",", summary.ReasonCodes ?? Array.Empty<string>());
                remnoteProbeStatus.AccessibleDescription = "ready=" + ready.ToString().ToLowerInvariant() + "; evidence=" + summary.Evidence.Token;
                remnoteProbeStatus.ForeColor = ready ? System.Drawing.Color.FromArgb(36, 130, 82) : Muted;
                status.Text = ready ? "当前目标检查就绪；未授予或执行导出写入。" : "当前目标未就绪；不会尝试写入。";
            }
            catch (Exception ex)
            {
                if (CurrentRemNoteObservation(lease))
                { remnoteProbeStatus.Text = "未核实 · " + ex.Message; remnoteProbeStatus.AccessibleDescription = "ready=false; observation=failed"; }
            }
            finally { FinishRemNoteObservation(lease); }
        }

        private async Task RestoreRemNoteBindingAsync()
        {
            if (typedApplication == null || remNoteBusy || IsDisposed || Disposing || CurrentRemNoteBinding() != null) return;
            var profile = workflowDocument.ActiveProfileId;
            var settings = workflowDocument.Profiles[profile].Remnote;
            if (moduleSettings.RemNoteBindings == null || !moduleSettings.RemNoteBindings.TryGetValue(profile, out var saved) || !saved.Matches(settings)) return;
            using var lease = BeginRemNoteObservation();
            try
            {
                var result = await RunRemNoteActionAsync<BindingRestoreResult>(ModuleActionKind.RemNoteRestoreBinding, saved.Reference, lease);
                if (result == null) return;
                var binding = result.Binding;
                if (result.Status != "verified_historical" || binding == null || binding.RootRemId != saved.Settings.Target.RootRemId ||
                    binding.RootTitle != saved.Settings.Target.RootTitle || binding.Evidence.Token != saved.Reference.EvidenceToken)
                    throw new InvalidOperationException("remnote-restore-binding-mismatch");
                remNoteBindings[profile] = binding;
                InvalidateRemNoteObservation();
                remnoteBoundTargetStatus.Text = "历史证据已恢复 · 标题=" + binding.RootTitle + " · 当前连接未检查";
                remnoteBoundTargetStatus.AccessibleDescription = "verified_historical; evidence=" + binding.Evidence.Token;
            }
            catch (Exception ex)
            {
                if (CurrentRemNoteObservation(lease)) remnoteBoundTargetStatus.Text = "历史绑定未核实 · " + ex.Message + " · 配置保留";
            }
            finally { FinishRemNoteObservation(lease); }
        }

        private void PrepareRemNoteBindingPersistence()
        {
            var saved = new Dictionary<string, DesktopRemNoteSavedBinding>(moduleSettings.RemNoteBindings ?? new(), StringComparer.Ordinal);
            foreach (var pair in workflowDocument.Profiles)
            {
                if (saved.TryGetValue(pair.Key, out var previous) && !previous.Matches(pair.Value.Remnote)) saved.Remove(pair.Key);
                if (remNoteBindings.TryGetValue(pair.Key, out var binding) && pair.Value.Remnote?.Target?.RootRemId == binding.RootRemId && pair.Value.Remnote.Target.RootTitle == binding.RootTitle)
                    saved[pair.Key] = new DesktopRemNoteSavedBinding(pair.Value.Remnote, CoreV3Client.ExportRemNoteBinding(binding));
            }
            moduleSettings.RemNoteBindings = saved;
        }
    }
}
