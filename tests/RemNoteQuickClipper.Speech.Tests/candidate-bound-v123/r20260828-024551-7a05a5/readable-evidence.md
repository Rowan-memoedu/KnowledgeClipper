# speech.edge v1.2.3 candidate-bound 后端验收

- authority checkpoint: `3EFB89D6D642F8546ABD14C5A114DE00E14BB7173EF33F03D3FD06DDAA2ADB55`
- authority release tree: `7B27BACDB1DBB56991272972A5F9309B1D6F782787EB0B99EBD21248F7959574`
- worker: `speech.edge 1.2.0` / `51FF5937C297DAD562AB781D6114374649E1A965EDBB43D575264BEED3DFB19C`
- playback runtime: FakePlayer；物理扬声器：否

## 输入文本 → 语言/音色选择 → 音频 bytes/SHA

|策略|输入文本|语言|音色|rate|volume|pitch|bytes|SHA-256|
|---|---|---|---|---:|---:|---:|---:|---|
|auto|这是中文自然语音测试。|zh-CN|zh-CN-XiaoxiaoNeural|12|-7|18|13968|`861d675f0fc525266fc3e6072cf1f47c21d1712b424fb00170cf14f441877a9e`|
|auto|This is an English natural voice test.|en-US|en-US-AvaMultilingualNeural|12|-7|18|16128|`3c7a1e56c8e0695a819371cca3d1867a5c1cbda91e7b34e35ccf65e5d3f2dede`|
|auto|Esta es una prueba de voz natural en español.|es-ES|es-ES-XimenaNeural|12|-7|18|18576|`18aa6237ce6c83295c56584b4b7969de72b22409fda3e8ad801cb48442d820a2`|
|auto|これは日本語の自然な音声テストです。|ja-JP|ja-JP-NanamiNeural|12|-7|18|20448|`5ea81f7f58f1a36a5c41470af8833910831f8b77f3dcabe3a314dc26651be48f`|
|auto|이것은 한국어 자연스러운 음성 테스트입니다.|ko-KR|ko-KR-SunHiNeural|12|-7|18|21888|`06cf8b7cad818c3efb8885b7267eafc61007f2166f40f00e28f62fe193959723`|
|override|这是中文自然语音测试。|zh-CN|zh-CN-XiaoxiaoNeural|-8|4|-22|16992|`419ce4129ddc62cd6ed85d655893e70a9ab9589b16bd1a22308cac3ab1371139`|
|override|This is an English natural voice test.|en-US|en-US-AvaMultilingualNeural|-8|4|-22|19152|`f5a6e17f23c398c8b9a000530d872340d30cb29aceed6c27ae52239b8c30c089`|
|override|Esta es una prueba de voz natural en español.|es-ES|es-ES-XimenaNeural|-8|4|-22|22608|`542f25648426ce93bf9f03e91fca74869a940b8781dac058959b56cbc25e5d3a`|
|override|これは日本語の自然な音声テストです。|ja-JP|ja-JP-NanamiNeural|-8|4|-22|24912|`5ccb48464dcf8b5e5fe615a10ed25d5711a7e7dd8b5a6f54f098966c677e3695`|
|override|이것은 한국어 자연스러운 음성 테스트입니다.|ko-KR|ko-KR-SunHiNeural|-8|4|-22|26496|`8ed62469a9f34b3d58b24ac77aeb72cee728c0642d3440e5700af58a63461e47`|

## FakePlayer 播放状态

|case|attempt|cancel reason|worker status|playback state|cache hit|
|---|---|---|---|---|---|
|natural-completion|pn||completed|idle_replayable|False|
|replay|pr||completed|idle_replayable|False|
|user-stop|pu|user_stop|cancelled|stopped|False|
|superseded|ps|superseded|cancelled|superseded|False|
|task-cancel|pc|task_cancel|cancelled|cancelled|False|
|timeout|pt|timeout|failed|timed_out|False|

Core cache reuse: synth cacheHit=True, reusedFromAttemptId=attempt-20260828024632894-cd09c7866f4f410fb73c8d3d98e66883; playback reused=False.
