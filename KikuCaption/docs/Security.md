# 安全与密钥存储

## 翻译 API Key（Windows DPAPI，CurrentUser）

V1 使用 **Windows DPAPI（当前用户范围）** 加密保存翻译 API Key（PROJECT.md 5.6，M6 §8），
实现见 [`DpapiTranslationSecretStore`](../src/KikuCaption.Translation/Security/DpapiTranslationSecretStore.cs)。

- 明文密钥**从不落盘**；本地只保存 `ProtectedData.Protect(..., CurrentUser)` 的**密文**。
- 密文位置：`%LOCALAPPDATA%/KikuCaption/secrets/translation.key`（当前用户目录，随用户 ACL）。
- 混入固定应用 entropy，使密文只对本应用 + 当前 Windows 用户可解。
- 写入用「临时文件 + 原子替换」，崩溃不会留下半写密钥。
- 提供**保存 / 读取 / 删除 / 替换**；UI 用 `PasswordBox`，只显示「已配置 / 未配置」，**页面重开不回显真实密钥**。
- **解密失败**（换用户 / 密文损坏）**抛出并保留密文**（供重试或重新输入），**绝不静默删除**。
- 未选择 Windows Credential Manager（DPAPI 即 PROJECT.md 默认，无需改变既定安全选择）。

## API Key 绝不出现在

`appsettings.json`、Git、日志、SQLite、`session.json`、`transcript.json`、崩溃报告、UI 普通文本框回显。
密钥仅在请求瞬间从 DPAPI 读取并放入 HTTP 头，随请求发出；日志只记录**脱敏错误类别**（如 `Auth`/`RateLimited`）。

## 翻译错误不泄露凭据

`TranslationJob.LastErrorCode` 与日志只保存去标识的错误类别/短消息（`Auth`/`Timeout`/`RateLimited`/`ServiceUnavailable`/
`BadRequest`/`InvalidConfig`/`Network`/`InvalidResponse`/`InputTooLong`/`Cancelled`）。
**不保存**完整请求正文、完整响应、完整字幕到普通错误字段。

## 传输与响应安全

- Endpoint 强制 **HTTPS**；不关闭 TLS 证书验证。
- 使用 `IHttpClientFactory` 单一可复用客户端（不为每条字幕新建 `HttpClient`）。
- 限制翻译输入最大长度（`MaxInputCharacters`）、响应最大字节（`MaxResponseBytes`）；超长输入拒绝、超大响应判失败。
- 检查 HTTP 状态、JSON 结构；空输出/错误 HTML 不当作翻译文本。
- 单元测试用注入的 `HttpMessageHandler`，**不访问真实网络**；测试**不使用真实公司密钥**。

## 最少外发（PROJECT.md 13）

只有**日语 final 文本**在启用翻译时逐条外发；音频、PCM、partial、视频、整场历史**不外发**。详见 [Translation.md](Translation.md)。

## 密钥泄漏自查

发布前对仓库、`appsettings*.json`、日志、SQLite 数据库执行明文密钥扫描（M6 验证已执行，见 [Verification.md](Verification.md)）：
确认 `ApiKey` 不在任何 appsettings、测试密钥不在源码/配置/日志/数据库、`src` 下不含 `*.key`。
