# 先识别，后选择；Content 0.12.0

## 首次识别

沿用 `standard-input/6.0.0`，保留完整 `language_selection` 和上下文段；省略模式和 `hints.requested_templates`，有效 settings 默认为 `ask`。
Content 把经过输入 SHA 校验的整个 language_selection 传入实际 classification payload。模式偏好不决定类别，不硬编码中英文判断。
模型仍可输出 general_learning 或真实 unresolved；普通内容走该类别允许的卡型。类别与请求模板冲突时，`requested_template_category_conflict` 会在任何 card_generation 调用前阻止该 unit。
语言识别且需选择时：completed WorkerResult + learning-bundle/1.0.0 + language-practice-decision/2.0.0，无 CardBundle。

## 选择后的新 attempt

仍为 common `understand-and-card`、input kind `content-input`、contractVersion `1.0.0`。
模块输入改用 `standard-input/8.0.0`，只新增 `language_practice_resume`：

```json
{
  "schema_version": "standard-input/8.0.0",
  "package_id": "与首次相同",
  "task_id": "与首次相同",
  "input_version": "新的版本",
  "mode": "card",
  "human_overrides": { "languagePracticeMode": "both" },
  "items": ["完整保留首次 items，不能再追加 requested_templates"],
  "language_practice_resume": {
    "schema_version": "language-practice-resume/1.0.0",
    "input_ref": { "artifactRef": "首次content-input", "kind": "content-input", "contractVersion": "1.0.0", "manifestPath": "首次manifest路径", "expectedSha256": "manifest散列", "payload_path": "首次输入路径", "payload_sha256": "首次输入散列" },
    "learning_ref": { "artifactRef": "首次learning artifactId", "kind": "learning-bundle", "contractVersion": "1.0.0", "manifestPath": "learning manifest路径", "expectedSha256": "manifest散列", "payload_path": "learning payload路径", "payload_sha256": "payload散列" },
    "decision_ref": { "artifactRef": "首次decision artifactId", "kind": "language-practice-decision", "contractVersion": "2.0.0", "manifestPath": "decision manifest路径", "expectedSha256": "manifest散列", "payload_path": "decision payload路径", "payload_sha256": "payload散列" }
  }
}
```

以上是字段说明，真实合法 JSON 样例由离线 Canary 从首次产物生成。
三个 ref 的 common 部分须原样放入新 content-input artifact manifest.inputRefs；三个 payload 也须在新 manifest.files 声明，bytes/SHA 匹配。WorkerRequest.inputRefs 继续只引用本次 content-input（以及现有 config snapshot）。首次 manifest/payload 可在新 job 保持相对路径复制；不得改写 manifest。源材料路径与内容、items、原 unit ID、选词/context SHA 保持一致。顶层 mode 可省略或与 human_overrides 一致；不可在 item override 夹带新的分类。
settingsRevisionId、Prompt 散列、category registry 散列、capability profile 散列必须匹配首次；发生变化先重新识别，不套用旧分类。

## 续跑语义与边界

只接受绑定的首次模式决策，核验两份工件的 producer/attempt、学习→首次输入散列、decision→学习散列和目标 token。此次 v8 只处理 mode choice，不兼任目标修正、读音修正或多次 resume。
使用冻结清洗响应重新过本地 delete-only 门禁，使用最终 LearningBundle units 重新过分类/来源门禁，然后只移除 language_practice_mode_required。所有其他 unresolved 原因仍保留。
不调用 cleaning/classification provider；新 ai_trace 只包含真正发生的制卡/读音请求。item.gates 的 classification.frozen-resume 指向首次工件 SHA，输出 manifests 保留祖先引用。both 仍固定 language_context 在前、language_writing 在后；不可靠读音/释义仍 withheld，不发布伪成功卡。
不能把冻结模型分类包装为人工分类 override。Core 负责 waiting_input/choice 状态、目的端、预览确认及运行配置来源；Content 不更改这些公共业务语义。

## 离线证据的诚实边界

测试经过 production common loader → 实际 classification payload → decision → 新 attempt → 实际模板分派；记录 provider-requests.fake.json。模型响应使用明确 Fake provider，验证代码接线与门禁，不声称真实 DeepSeek 语义质量已通过。
保留 T050622 原始 classification response 的散列验证，原响应 replay 在模板分派前 unresolved；成功 fixture 不作为原响应被 AI 自动修正的证据。
