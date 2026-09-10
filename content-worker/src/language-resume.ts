import { readFile } from "node:fs/promises";
import type { LoadedContracts } from "./contracts.js";
import type { CommonArtifactManifest, LanguagePracticeDecisionBundle, LearningBundle, StandardInput, WorkerPaths } from "./types.js";
import { packagePath, readJson, sha256, stableStringify } from "./util.js";

type Ref = NonNullable<StandardInput["language_practice_resume"]>["learning_ref"];
function invalid(detail: string): never { throw new Error(`INPUT_STALE language_practice_resume ${detail}`); }
const same = (a: unknown, b: unknown): boolean => stableStringify(a) === stableStringify(b);

/** Read immutable ancestry; no provider invocation, no rewriting frozen model output. */
export async function loadLanguageResume(contracts: LoadedContracts, paths: WorkerPaths): Promise<LearningBundle | undefined> {
  const resume = contracts.input.language_practice_resume;
  if (!resume) return undefined;
  if (!contracts.commonInputManifest || resume.schema_version !== "language-practice-resume/1.0.0"
    || !same(Object.keys(resume).sort(), ["schema_version", "decision_ref", "learning_ref", "input_ref"].sort())) invalid("identity");
  async function bound<T>(ref: Ref, kind: string, version: string): Promise<{ data: T; manifest: CommonArtifactManifest }> {
    if (!ref || !same(Object.keys(ref).sort(), ["artifactRef", "kind", "contractVersion", "manifestPath", "expectedSha256", "payload_path", "payload_sha256"].sort())) invalid("reference fields");
    if (ref.kind !== kind || ref.contractVersion !== version) invalid("reference identity");
    const { payload_path, payload_sha256, ...commonRef } = ref;
    if (!contracts.commonInputManifest!.inputRefs.some(r => same(r, commonRef))) invalid("ancestry reference");
    const bytes = await readFile(packagePath(contracts.packageRoot, ref.manifestPath));
    if (sha256(bytes) !== ref.expectedSha256) invalid("manifest hash");
    const manifest = JSON.parse(bytes.toString("utf8")) as CommonArtifactManifest;
    if (manifest.schemaVersion !== "1.0.0" || manifest.artifactId !== ref.artifactRef || manifest.kind !== kind || manifest.contractVersion !== version) invalid("manifest identity");
    if (kind !== "content-input" && manifest.producer.workerId !== "content.deepseek") invalid("producer");
    const file = manifest.files.find(f => f.path === payload_path);
    const declared = contracts.filesByPath.get(payload_path);
    const payload = await readFile(packagePath(contracts.packageRoot, payload_path));
    if (!file || !declared || file.sha256 !== payload_sha256 || declared.sha256 !== payload_sha256
      || file.bytes !== payload.length || declared.size !== payload.length || sha256(payload) !== payload_sha256) invalid("payload binding");
    return { data: JSON.parse(payload.toString("utf8")) as T, manifest };
  }
  const {data: decision, manifest: decisionManifest} = await bound<LanguagePracticeDecisionBundle>(resume.decision_ref, "language-practice-decision", "2.0.0");
  const {data: learning, manifest: learningManifest} = await bound<LearningBundle>(resume.learning_ref, "learning-bundle", "1.0.0");
  const {data: original, manifest: originalManifest} = await bound<StandardInput>(resume.input_ref, "content-input", "1.0.0");
  if (learning.schema_version !== "learning-bundle/1.0.0" || decision.schema_version !== "language-practice-decision/2.0.0"
    || decision.state !== "requires_human" || learning.release_state === "failed"
    || decision.learning_bundle_sha256 !== resume.learning_ref.payload_sha256
    || learning.input.standard_input_sha256 !== resume.input_ref.payload_sha256
    || learning.input.manifest_sha256 !== resume.input_ref.expectedSha256
    || decision.attempt_id !== learning.attempt_id || decision.task_id !== learning.task_id
    || original.task_id !== learning.task_id || original.input_version !== learning.input_version
    || decision.input_version !== original.input_version || decision.package_id !== original.package_id
    || learning.package_id !== original.package_id
    || decisionManifest.producer.attemptId !== learning.attempt_id || learningManifest.producer.attemptId !== learning.attempt_id) invalid("bundle links");
  const {payload_path: ignoredPath, payload_sha256: ignoredSha, ...originalRef} = resume.input_ref;
  void ignoredPath; void ignoredSha;
  if (!learningManifest.inputRefs.some(r => same(r, originalRef)) || !decisionManifest.inputRefs.some(r => same(r, originalRef))) invalid("original input ancestry");
  // Resume is mode selection only. It cannot silently change units, target, hints or content.
  if (!same(original.items, contracts.input.items) || original.package_id !== contracts.input.package_id
    || original.task_id !== contracts.input.task_id || original.input_version === contracts.input.input_version
    || paths.attemptId === learning.attempt_id || original.language_practice_resume) invalid("input identity or new attempt");
  const mode = contracts.input.human_overrides?.languagePracticeMode;
  if (!mode || !["meaning-reading", "writing", "both"].includes(mode)
    || (contracts.input.languagePracticeMode !== undefined && contracts.input.languagePracticeMode !== mode)
    || !same(Object.keys(contracts.input.human_overrides ?? {}), ["languagePracticeMode"])) invalid("explicit mode");
  if (!same(learning.worker.prompt_set_sha256, paths.promptSetSha256)
    || !paths.promptSetSha256 || !same(learning.worker.settings_revision_id, paths.settingsRevisionId)
    || !same(learning.worker.capability_profile_sha256, paths.capabilityProfileSha256)
    || learning.worker.category_registry_sha256 !== sha256(await readFile(paths.registryPath))) invalid("settings changed");
  for (const item of original.items) {
    if (item.source.kind !== "text" || !item.language_selection) invalid("source kind");
    const sourcePaths = [item.source.material_path, ...item.language_selection.context_segments.map(s => s.material_path)];
    for (const sourcePath of sourcePaths) {
      const first = originalManifest.files.find(f => f.path === sourcePath);
      const current = contracts.filesByPath.get(sourcePath);
      if (!first || !current || first.sha256 !== current.sha256 || first.bytes !== current.size) invalid("source changed");
    }
  }
  if (!decision.decisions.some(d => d.reasons.includes("language_practice_mode_required"))) invalid("not a mode decision");
  for (const d of decision.decisions) {
    const item = original.items.find(i => i.item_id === d.item_id);
    const unit = learning.items.find(i => i.item_id === d.item_id)?.units.find(u => u.unit_id === d.unit_id);
    const s = item?.language_selection;
    if (!s || !unit || unit.category_id !== d.category_id || !same(unit.source_segment_ids, d.source_segment_ids)
      || d.selected_text_sha256 !== s.selected_text_sha256 || d.target_text_sha256 !== s.target_text_sha256
      || !same(d.context_segment_sha256s, s.context_segments.map(c => c.sha256))) invalid("decision selection");
    const token = sha256(stableStringify({item_id: item!.item_id, selected_text_sha256: s.selected_text_sha256, target_text_sha256: s.target_text_sha256, context_enabled: s.context_enabled, context_segment_sha256s: s.context_segments.map(c => c.sha256)}));
    if (d.target_token !== token) invalid("decision token");
  }
  return learning;
}
