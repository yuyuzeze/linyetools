# 许可证全文索引（licenses/）

本目录汇总 KikuCaption 第三方依赖的许可证类型与获取方式。汇总清单见仓库根目录
[`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。为避免在版本库中冗余复制大量文本，各依赖的**许可证全文**
以下述权威来源为准；对外分发时应随包附上相应全文。

| 许可证 | 适用组件（示例） | 全文来源 |
|---|---|---|
| MIT | .NET、CommunityToolkit.Mvvm、NAudio、Microsoft.Data.Sqlite、Microsoft.Extensions.*、ProtectedData、faster-whisper、CTranslate2、onnxruntime、Whisper 模型 | https://opensource.org/license/mit |
| Apache-2.0 | Serilog、SQLitePCLRaw、huggingface_hub、tokenizers | https://www.apache.org/licenses/LICENSE-2.0 |
| BSD-3-Clause | numpy、PyAV | https://opensource.org/license/bsd-3-clause |
| PSF License | CPython | https://docs.python.org/3/license.html |
| GPL v3 | **FFmpeg（BtbN 构建）** | https://www.gnu.org/licenses/gpl-3.0.txt |
| GPL v2+ | **libx264** | https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt |
| Public Domain | SQLite 本体 | https://www.sqlite.org/copyright.html |

## 分发前检查清单

1. 若发布包**捆绑了 GPL 的 FFmpeg**：随附本目录 + 上述 GPL v3 / GPL v2 全文，并履行源码提供义务（见 THIRD_PARTY_NOTICES.md）。
2. 附上锁定的 Python 依赖清单 [`python/whisper_worker/requirements-lock.txt`](../python/whisper_worker/requirements-lock.txt)。
3. 附上 [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md) 与本 README。
4. 不在包中包含任何 API Key、DPAPI 密文、用户设置、会议文件或日志。
