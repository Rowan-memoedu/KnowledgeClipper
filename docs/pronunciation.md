# 中英文朗读

新合成请求沿用已选音色、语速和音量，单独发送朗读指令：中文采用标准普通话，英文采用自然美式英语，正常语速、连贯措辞、按标点停顿；单词或短语正常读一遍。指令通过 MiMo 的 user 消息传入，原文保留在 assistant 消息，不在原文中插入风格标签。

自动英文朗读继续保留原先的过滤规则；手动选择音色时，中英混合材料仍按原文顺序朗读。两种路径的指令均依据实际送去合成的文本确定。

新录音缓存包含朗读指令。历史重播仍核验并播放原录音，不因升级自动重新合成或改写历史。明确重新朗读才生成新录音。共享 MiMoTTS 客户端优先在完整句子处切分长材料，其次使用分句标点和空格，逐字保留原文。

离线回归：`KnowledgeClipper.SpeechUsage.Tests` 与 `KnowledgeClipper.LanguageHistory.Tests`，均已接入维护检查。

真实合成检查：在仓库根目录运行已构建的 `KnowledgeClipper.SpeechUsage.Tests.dll --pronunciation <新的 D 盘产物目录>`。使用公开的中文与英文单词、句子，保存可试听 MP3 并验证解码；不会播放到系统音箱，也不改动用户草稿。合成及解码通过不能替代对发音自然度的试听。

接口依据：[MiMo 官方语音合成文档](https://mimo.mi.com/docs/en-US/quick-start/usage-guide/audio/speech-synthesis-v2.5)。
