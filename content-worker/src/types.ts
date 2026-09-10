export type JsonObject = Record<string, unknown>;

export interface ArtifactReference {
  artifactRef: string;
  kind: string;
  contractVersion: string;
  manifestPath: string;
  expectedSha256: string;
}

export interface CommonArtifactFile {
  path: string;
  sha256: string;
  bytes: number;
  mediaType: string;
}

export interface CommonArtifactManifest {
  schemaVersion: "1.0.0";
  artifactId: string;
  kind: string;
  contractVersion: string;
  mediaType: string;
  files: CommonArtifactFile[];
  createdAt: string;
  producer: {
    stageId: string;
    workerId: string;
    workerVersion: string;
    attemptId: string;
  };
  inputRefs: ArtifactReference[];
  dataClass: "public" | "personal" | "sensitive" | "secret-prohibited";
  metadata?: JsonObject;
}

export interface ManifestFile {
  path: string;
  sha256: string;
  size: number;
  media_type: string;
}

export interface TaskManifest {
  schema_version: "task-manifest/1.0.0";
  package_id: string;
  package_version: string;
  created_at: string;
  files: ManifestFile[];
}

export type AssetRole = "question" | "answer" | "context" | "unknown";

export interface TextSource {
  kind: "text";
  material_path: string;
}

export interface OcrArtifactProvenanceRef extends ArtifactReference {
  payload_path: string;
  payload_sha256: string;
}

export interface OcrTextSource {
  kind: "ocr_text";
  material_path: string;
  provenance: {
    schema_version: "ocr-provenance/1.0.0";
    producer_worker_id: "image.ocr.mineru";
    markdown_artifact: OcrArtifactProvenanceRef;
    structured_artifact?: OcrArtifactProvenanceRef;
  };
}

export interface OcrProofreadArtifactRef extends ArtifactReference {
  kind: "ocr-proofread-bundle";
  contractVersion: "1.0.0";
  payload_path: string;
  payload_sha256: string;
}

export interface OcrHumanResolutionDiff {
  segment_index: number;
  operation: "substitution" | "insertion" | "deletion";
  start: number;
  end: number;
  before: string;
  after: string;
}

export interface OcrHumanResolutionInput {
  schema_version: "ocr-human-resolution-input/1.0.0";
  resolution_id: string;
  action: "accept_original" | "user_correction";
  source_proofread_artifact: OcrProofreadArtifactRef;
  source_item_id: string;
  original_text_sha256: string;
  proofread_text_sha256: string;
  unresolved_candidates: OcrUnresolvedCandidate[];
  reason: string;
  correction?: {
    corrected_text: string;
    corrected_text_sha256: string;
    diffs: OcrHumanResolutionDiff[];
  };
}

export interface ConversationSource {
  kind: "conversation";
  material_path: string;
}

export interface MediaSetSource {
  kind: "media_set";
  assets: Array<{ material_path: string; role: AssetRole }>;
}

export interface QuestionWorkflowSource {
  kind: "question_workflow";
}

export type InputSource = TextSource | OcrTextSource | ConversationSource | MediaSetSource | QuestionWorkflowSource;

export interface QuestionTurnInput {
  turnId: string;
  turnIndex: number;
  question: string;
  parentAnswerId?: string;
}

export interface QuestionAnswerAncestryInput {
  answerId: string;
  artifactRef: string;
  turnId: string;
  turnIndex: number;
  questionSha256: string;
  answerText: string;
  answerTextSha256: string;
  parentAnswerId?: string;
}

export interface QuestionWorkflowInput {
  schemaVersion: "question-workflow-input/1.0.0";
  phase: "answer" | "make-cards";
  questionTurns: QuestionTurnInput[];
  answerAncestry: QuestionAnswerAncestryInput[];
  cardStructureRef?: string;
}

export type LanguagePracticeMode = "meaning-reading" | "writing" | "both";

export interface LanguageContextSegmentInput {
  segment_id: string;
  order: number;
  material_path: string;
  sha256: string;
}

export interface LanguageSelectionInput {
  schema_version: "language-selection/1.0.0";
  selected_text: string;
  selected_text_sha256: string;
  target_text: string;
  target_text_sha256: string;
  context_enabled: boolean;
  context_segments: LanguageContextSegmentInput[];
}

export interface LanguageTargetResolutionInput {
  schema_version: "language-target-resolution/1.0.0" | "language-target-resolution/2.0.0";
  decision_id: string;
  target_token: string;
  action: "accept_candidate" | "user_correction";
  selected_text: string;
  selected_text_sha256: string;
  confirmed: true;
  candidateOccurrence?: {
    segmentId: string;
    order: number;
    start: number;
    end: number;
    exactText: string;
    sourceSha256: string;
  };
}

export interface InputHints {
  category_id?: string;
  subtype?: string;
  answer_state?: "provided" | "missing" | "ambiguous" | "not_applicable";
  context_state?: "provided" | "missing" | "ambiguous" | "not_applicable";
  risk_state?: "ordinary" | "verification_required" | "high_stakes";
  target_term?: string;
  requested_templates?: string[];
  languagePracticeMode?: LanguagePracticeMode;
}

export interface LanguageWritingHumanOverride {
  pronunciation: string;
  meaning: string;
  confirmed: true;
}

export type HumanOverrides = JsonObject & {
  languagePracticeMode?: LanguagePracticeMode;
  languageWriting?: { units?: Record<string, LanguageWritingHumanOverride> };
  languageTargetResolution?: LanguageTargetResolutionInput;
};

export interface StandardInputItem {
  item_id: string;
  source: InputSource;
  hints?: InputHints;
  human_overrides?: HumanOverrides;
  ocr_human_resolution?: OcrHumanResolutionInput;
  language_selection?: LanguageSelectionInput;
}

export interface StandardInput {
  schema_version: "standard-input/1.0.0" | "standard-input/2.0.0" | "standard-input/3.0.0" | "standard-input/4.0.0" | "standard-input/5.0.0" | "standard-input/6.0.0" | "standard-input/7.0.0" | "standard-input/8.0.0";
  language_practice_resume?: {
    schema_version: "language-practice-resume/1.0.0";
    decision_ref: ArtifactReference & { payload_path: string; payload_sha256: string };
    learning_ref: ArtifactReference & { payload_path: string; payload_sha256: string };
    input_ref: ArtifactReference & { payload_path: string; payload_sha256: string };
  };
  package_id: string;
  task_id: string;
  input_version: string;
  mode: "card" | "question";
  languagePracticeMode?: LanguagePracticeMode;
  human_overrides?: HumanOverrides;
  questionWorkflow?: QuestionWorkflowInput;
  items: StandardInputItem[];
}

export interface CategoryDefinition {
  id: string;
  description: string;
  prompt_file: string;
  allowed_templates: string[];
  template_prompt_files?: Record<string, string>;
}

export interface CategoryRegistry {
  schema_version: "category-registry/1.0.0" | "category-registry/2.0.0";
  categories: CategoryDefinition[];
}

export interface SourceSegment {
  segment_index: number;
  segment_id: string;
  source_kind: "text" | "ocr_text" | "conversation_turn" | "question_turn";
  source_path: string;
  start: number;
  end: number;
  text: string;
  turn_id?: string;
  role?: "user" | "assistant";
}

export interface RemovedFragmentCandidate {
  segment_index: number;
  exact_text: string;
  occurrence?: number;
  reason: "navigation" | "control" | "advertisement" | "sharing_prompt" | "site_footer" | "duplicate";
  evidence: string;
}

export interface ResolvedRemoval extends RemovedFragmentCandidate {
  source_start: number;
  source_end: number;
}

export interface FormatAnomaly {
  segment_index: number;
  kind: "suspected_ocr" | "broken_table" | "broken_formula" | "ambiguous_layout" | "other";
  severity: "notice" | "requires_human";
  evidence: string;
}

export interface CleaningResponse {
  cleaned_text: string;
  removed_fragments: RemovedFragmentCandidate[];
  format_anomalies: FormatAnomaly[];
}

export interface ClassifiedUnitCandidate {
  unit_id: string;
  source_segment_indexes: number[];
  category_id: string;
  subtype: string;
  answer_state: "provided" | "missing" | "ambiguous" | "not_applicable";
  context_state: "provided" | "missing" | "ambiguous" | "not_applicable";
  risk_state: "ordinary" | "verification_required" | "high_stakes";
  question?: string;
  supplied_answer?: string;
  target_term?: string;
  unresolved_reasons: string[];
}

export interface ClassificationResponse {
  units: ClassifiedUnitCandidate[];
}

export interface CardBlockCandidate {
  type: "markdown" | "text" | "asset_ref";
  text?: string;
  material_path?: string;
  provenance: "source" | "generated" | "human_confirmed";
}

export interface CardCandidate {
  template: string;
  front_blocks: CardBlockCandidate[];
  back_blocks: CardBlockCandidate[];
  answer_provenance: "source" | "generated" | "human_confirmed" | "not_applicable";
}

export interface CardGenerationResponse {
  generated_content: Array<{ kind: string; text: string }>;
  cards: CardCandidate[];
  unresolved_reasons: string[];
}

export interface GateResult {
  gate_id: string;
  status: "pass" | "fail" | "notice";
  evidence: string;
}

export interface AiTrace {
  stage: "ocr_proofreading" | "ocr_lexical_audit" | "cleaning" | "classification" | "card_generation" | "language_writing" | "question_answer";
  call_key: string;
  mode: "replay" | "live";
  prompt_files: Array<{ path: string; sha256: string }>;
  prompt_sha256: string;
  request_sha256: string;
  provider: string;
  model: string;
  response_sha256: string;
  response: unknown;
  attempts: number;
}

export interface OcrCorrectionCandidate {
  segment_index: number;
  operation: "substitution" | "insertion" | "deletion";
  start: number;
  end: number;
  before: string;
  after: string;
  confidence: number;
  evidence_type: "lexical_context" | "cross_segment_consistency" | "document_context";
  evidence: string;
}

export interface ResolvedOcrCorrection extends OcrCorrectionCandidate {
  source_start: number;
  source_end: number;
  edit_distance: number;
}

export interface OcrUnresolvedCandidate {
  segment_index: number;
  start: number;
  end: number;
  exact_text: string;
  reason: "ambiguous_text" | "insufficient_context" | "protected_content" | "structural_ambiguity";
  evidence: string;
}

export interface OcrProofreadingResponse {
  proofread_text: string;
  corrections: OcrCorrectionCandidate[];
  unresolved: OcrUnresolvedCandidate[];
}

export interface OcrProofreadItem {
  item_id: string;
  state: "ready" | "unresolved";
  provenance: OcrTextSource["provenance"];
  source_ref: { path: string; sha256: string };
  original_text: string;
  original_text_sha256: string;
  proofread_text: string;
  proofread_text_sha256: string;
  source_segments: SourceSegment[];
  proofread_segments: SourceSegment[];
  corrections: ResolvedOcrCorrection[];
  unresolved: OcrUnresolvedCandidate[];
  gates: GateResult[];
}

export interface OcrProofreadBundle {
  schema_version: "ocr-proofread-bundle/1.0.0";
  bundle_id: string;
  package_id: string;
  task_id: string;
  input_version: string;
  attempt_id: string;
  created_at: string;
  items: OcrProofreadItem[];
  release_state: "ready" | "unresolved";
  ai_trace_keys: string[];
  gates: GateResult[];
}

export interface OcrHumanResolutionRecord {
  resolution_id: string;
  item_id: string;
  state: "resolved" | "unresolved";
  action: "accept_original" | "user_correction";
  actor: "user";
  provenance: "human_submitted";
  reason: string;
  source_proofread_artifact: OcrProofreadArtifactRef;
  source_ocr: {
    path: string;
    sha256: string;
    text: string;
  };
  frozen_proofread: {
    text: string;
    sha256: string;
    corrections: ResolvedOcrCorrection[];
    unresolved_candidates: OcrUnresolvedCandidate[];
  };
  user_diffs: OcrHumanResolutionDiff[];
  resolved_text: string;
  resolved_text_sha256: string;
  protected_ranges: Array<{ segment_index: number; start: number; end: number; exact_text: string }>;
  unresolved_reasons: string[];
  gates: GateResult[];
}

export interface OcrHumanResolutionBundle {
  schema_version: "ocr-human-resolution-bundle/1.0.0";
  bundle_id: string;
  package_id: string;
  task_id: string;
  input_version: string;
  attempt_id: string;
  created_at: string;
  records: OcrHumanResolutionRecord[];
  release_state: "ready" | "unresolved";
  gates: GateResult[];
}

export interface LearningUnit {
  unit_id: string;
  source_segment_ids: string[];
  category_id: string;
  subtype: string;
  answer_state: ClassifiedUnitCandidate["answer_state"];
  context_state: ClassifiedUnitCandidate["context_state"];
  risk_state: ClassifiedUnitCandidate["risk_state"];
  question?: string;
  supplied_answer?: string;
  target_term?: string;
  generated_content: Array<{ kind: string; text: string }>;
  state: "ready" | "unresolved" | "failed_content_gate" | "failed_ai";
  unresolved_reasons: string[];
  gates: GateResult[];
}

export interface LearningItem {
  item_id: string;
  source_kind: InputSource["kind"];
  source_refs: Array<{ path: string; sha256: string; role?: AssetRole }>;
  source_segments: SourceSegment[];
  cleaning: {
    state: "ready" | "not_applicable" | "failed_content_gate" | "unresolved" | "failed_ai";
    cleaned_text?: string;
    removals: ResolvedRemoval[];
    format_anomalies: FormatAnomaly[];
  };
  proofreading?: {
    state: "ready" | "unresolved";
    proofread_text: string;
    proofread_text_sha256: string;
    proofread_segments: SourceSegment[];
    corrections: ResolvedOcrCorrection[];
    unresolved: OcrUnresolvedCandidate[];
  };
  human_resolution?: OcrHumanResolutionRecord;
  units: LearningUnit[];
  state: "ready" | "partial" | "unresolved" | "failed_content_gate" | "failed_ai";
  unresolved_reasons: string[];
  gates: GateResult[];
}

export interface LearningBundle {
  schema_version: "learning-bundle/1.0.0" | "learning-bundle/2.0.0" | "learning-bundle/3.0.0";
  bundle_id: string;
  package_id: string;
  task_id: string;
  input_version: string;
  attempt_id: string;
  created_at: string;
  input: {
    manifest_path: string;
    manifest_sha256: string;
    standard_input_path: string;
    standard_input_sha256: string;
  };
  ocr_proofread_bundle_path?: string;
  ocr_proofread_bundle_sha256?: string;
  ocr_human_resolution_bundle_path?: string;
  ocr_human_resolution_bundle_sha256?: string;
  worker: {
    name: "content-understanding-card-worker";
    version: string;
    category_registry_sha256: string;
    settings_revision_id?: string;
    prompt_set_id?: string;
    prompt_set_sha256?: string;
    capability_profile_id?: string;
    capability_profile_version?: string;
    capability_profile_sha256?: string;
    language_practice_default?: "ask" | LanguagePracticeMode;
  };
  items: LearningItem[];
  ai_trace: AiTrace[];
  release_state: "ready" | "partial" | "unresolved" | "failed";
  unresolved_items: Array<{ item_id: string; reasons: string[] }>;
  gates: GateResult[];
}

export interface CardBlock extends CardBlockCandidate {
  block_id: string;
}

export interface CardRecord {
  card_id: string;
  item_id: string;
  unit_id: string;
  variant_group_id: string;
  category_id: string;
  subtype: string;
  template: string;
  front_blocks: CardBlock[];
  back_blocks: CardBlock[];
  answer_provenance: CardCandidate["answer_provenance"];
  source_segment_ids: string[];
}

export interface CardBundle {
  schema_version: "card-bundle/1.0.0";
  bundle_id: string;
  package_id: string;
  task_id: string;
  attempt_id: string;
  created_at: string;
  learning_bundle_path: string;
  learning_bundle_sha256: string;
  cards: CardRecord[];
  withheld_units: Array<{ item_id: string; unit_id: string; reasons: string[] }>;
  release_state: "ready" | "partial";
  gates: GateResult[];
}

export interface LanguagePracticeDecisionRecord {
  decision_id: string;
  item_id: string;
  unit_id: string;
  category_id: "language_zh" | "language_en";
  state: "requires_human";
  reasons: Array<"language_practice_mode_required" | "language_writing_target_not_unique" | "language_writing_details_required">;
  allowed_modes: LanguagePracticeMode[];
  requested_override_paths: string[];
  source_segment_ids: string[];
  target_term?: string;
  target_occurrence_count?: number;
  current_mode?: LanguagePracticeMode;
  pronunciation_state?: "missing" | "unreliable";
  meaning_state?: "missing" | "unreliable";
  target_token?: string;
  selected_text_sha256?: string;
  target_text_sha256?: string;
  context_segment_sha256s?: string[];
  candidate_positions?: Array<{ segment_id: string; order: number; start: number; end: number; exact_text: string; source_sha256: string }>;
  suggested_user_fields?: string[];
}

export interface LanguagePracticeDecisionBundle {
  schema_version: "language-practice-decision/1.0.0" | "language-practice-decision/2.0.0";
  decision_bundle_id: string;
  package_id: string;
  task_id: string;
  input_version: string;
  attempt_id: string;
  created_at: string;
  learning_bundle_path: string;
  learning_bundle_sha256: string;
  state: "requires_human";
  decisions: LanguagePracticeDecisionRecord[];
}

export interface QuestionAnswerRecord {
  answer_id: string;
  artifact_ref: string;
  turn_id: string;
  turn_index: number;
  question: string;
  question_sha256: string;
  answer_text: string;
  answer_text_sha256: string;
  parent_answer_id?: string;
  provenance: "persisted" | "generated";
}

export interface QuestionAnswerBundle {
  schema_version: "question-answer/1.0.0";
  answer_bundle_id: string;
  package_id: string;
  task_id: string;
  input_version: string;
  attempt_id: string;
  created_at: string;
  phase: "answer";
  learning_bundle_path: string;
  learning_bundle_sha256: string;
  question_chain_sha256: string;
  new_answer_id: string;
  turns: QuestionAnswerRecord[];
  gates: GateResult[];
}

export interface WorkerPaths {
  manifestPath: string;
  inputPath: string;
  outputDir: string;
  promptRoot: string;
  registryPath: string;
  attemptId: string;
  operation?: "understand-and-card" | "question.answer" | "question.make-cards";
  jobRoot?: string;
  signal?: AbortSignal;
  settingsRevisionId?: string;
  promptSetId?: string;
  promptSetSha256?: string;
  capabilityProfileId?: string;
  capabilityProfileVersion?: string;
  capabilityProfileSha256?: string;
  languagePracticeDefault?: "ask" | LanguagePracticeMode;
}

export interface CommonWorkerRequest {
  schemaVersion: "1.0.0";
  requestId: string;
  operationId: string;
  taskId: string;
  runId: string;
  stageId: string;
  attemptId: string;
  workerId: string;
  operation: string;
  idempotencyKey: string;
  attemptPath: string;
  outputPath: string;
  progressPath: string;
  resultPath: string;
  cancelSignal: { schemaVersion: "1.0.0"; path: string; gracePeriodMs: 2000 };
  inputRefs: ArtifactReference[];
  configSnapshot: JsonObject;
  payload: JsonObject;
}

export interface CommonWorkerError {
  code: string;
  category: "contract" | "worker" | "dependency" | "transient" | "resource" | "manual" | "effect" | "cancelled" | "internal";
  retryable: boolean;
  detailsRef: string;
  message?: string;
  blame?: "input" | "worker" | "dependency" | "orchestrator" | "user" | "unknown";
}

export interface CommonWorkerResult {
  schemaVersion: "1.0.0";
  requestId: string;
  operationId: string;
  taskId: string;
  runId: string;
  stageId: string;
  attemptId: string;
  worker: { id: "content.deepseek"; version: string };
  contractVersion: "1.0.0";
  idempotencyKey: string;
  status: "completed" | "partial" | "failed" | "cancelled" | "unknown_external_state";
  outputs: ArtifactReference[];
  itemDiagnostics?: Array<{ itemId: string; status: "completed" | "failed" | "cancelled" | "unknown_external_state"; outputs: ArtifactReference[]; error: CommonWorkerError | null }>;
  error: CommonWorkerError | null;
  startedAt: string;
  finishedAt: string;
  warnings: string[];
  metrics?: JsonObject;
  payload?: JsonObject;
}
