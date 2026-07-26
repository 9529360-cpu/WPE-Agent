# Binance Futures Testnet 商业认证契约

## 范围与声明边界

本契约只适用于 `binance-futures` 的 Binance Futures Testnet。Mainnet 始终禁用；离线 fake、单元测试、provider conformance、静态 UI 或 catalog 条目均不能证明真实 Testnet 认证。候选版本只有在目标 Windows 机器上完成外部凭据 smoke、保存完整证据并经发布门禁复核后，才可声明“该候选版本已通过 Binance Futures Testnet smoke”。

认证不得绕过交易授权、Risk Gate 或 `ReliableOrderExecutor`。Review 模式未经用户确认不得产生 provider mutation。未知、过期、不支持、错误或权限不足均 fail closed，不得降级为可交易。

## 冻结输入

认证运行输入必须固定并写入 artifact，敏感值只记录凭据槽引用或脱敏指纹，不记录 API key、secret、签名或完整请求头。

| 字段 | 必需值/语义 |
| --- | --- |
| `candidateVersion` | 应用程序集/项目版本和候选包 SHA-256 |
| `providerId` | `binance-futures` |
| `environment` | `Testnet`；任何 Mainnet 值立即阻断 |
| `restOrigin` | 仅 `https://testnet.binancefuture.com`，credential-free HTTPS origin，无自定义端口、路径、query 或 fragment |
| `webSocketOrigin` | `wss://stream.binancefuture.com/stream` |
| `credentialSlotRef` | WPF/DPAPI 管理的 Testnet 槽引用；artifact 不得含明文秘密 |
| `permissionEvidence` | 已知且 `read=true`、`trade=true`、`withdraw=false` |
| `serverTimeEvidence` | 本地 UTC、服务端 UTC、采样 UTC、offset ms；绝对 offset 不超过 1000 ms |
| `capabilityEvidence` | exchange/provider/instrument/native symbol/market type 精确匹配，`Available`、`CanRead=true`、`CanTrade=true`、`TestnetAvailable=true` |
| `capabilityCheckedAt` | 进入任何 mutation 前不超过 `ProviderCapabilityPrecondition.MaximumAge`（当前 15 秒），未来偏移同样拒绝 |
| `authorizationEvidence` | Review 用户确认或经授权的 Testnet Auto；默认 Review |
| `riskReceipt` | 确定性 Risk Gate 的 allow receipt、intent/artifact hash、有效期和 correlation id |
| `orderPlan` | 最小名义测试订单、唯一 client order id、退出/撤单计划；不得使用 Mainnet |
| `reconcilePlan` | 仅按持久化 client order id 查询，不因超时、断线或未知 ACK 重提订单 |

## 冻结输出

每次运行输出一个机器可读结果：

```json
{
  "schemaVersion": "wpe.binance-testnet-certification.v1",
  "candidateVersion": "<assembly-version>",
  "candidateSha256": "<sha256>",
  "runId": "<opaque-id>",
  "correlationId": "<correlation-id>",
  "disposition": "blocked | offline-contract-passed | smoke-passed | smoke-failed | manual-review-required",
  "code": "binance-cert.<stable-code>",
  "certified": false,
  "startedAtUtc": "<RFC3339>",
  "completedAtUtc": "<RFC3339>",
  "artifactManifest": "manifest.sha256"
}
```

`offline-contract-passed` 必须保持 `certified=false`，含义仅为“允许进入外部凭据 smoke”。只有同一候选 hash 的目标机 smoke 完整通过后，发布门禁才能生成 `smoke-passed`；测试代码本身不生成商业认证声明。

## 稳定状态码

| 状态码 | 处置 |
| --- | --- |
| `binance-cert.testnet-required` | 环境不是 Testnet，阻断 |
| `binance-cert.endpoint-untrusted` | REST/WS endpoint 不在冻结 allowlist，阻断 |
| `binance-cert.permissions-unknown` | 权限探测缺失或错误，阻断 |
| `binance-cert.permission-read-required` | 缺少读取权限，阻断 |
| `binance-cert.permission-trade-required` | 缺少 Testnet 交易权限，阻断 |
| `binance-cert.permission-withdraw-forbidden` | 凭据具有提现权限，不符合最小权限，阻断并更换凭据 |
| `binance-cert.clock-unknown` | 无服务端时间证据，阻断 |
| `binance-cert.clock-skew` | `abs(offset) > 1000 ms`，阻断 |
| `binance-cert.capability-unknown` | capability 缺失，阻断 |
| `binance-cert.capability-stale` | capability 超过 15 秒或异常未来时间，阻断 |
| `binance-cert.capability-invalid` | provider、symbol、market type 或状态不匹配，阻断 |
| `binance-cert.testnet-unavailable` | provider 未证明 Testnet 可用，阻断 |
| `binance-cert.lifecycle-incomplete` | 订单生命周期证据不完整，失败 |
| `binance-cert.reconcile-unknown` | 订单最终状态未知，人工复核，不得增加风险 |
| `binance-cert.reconcile-resubmitted` | reconcile 发生重复提交，失败 |
| `binance-cert.evidence-incomplete` | artifact 或 manifest 不完整，失败 |
| `binance-cert.offline-contract-passed` | 仅离线前置契约通过，未认证 |
| `binance-cert.smoke-passed` | 外部凭据 smoke 和证据复核通过 |
| `binance-cert.smoke-failed` | 外部 smoke 任一步失败，候选不得声明认证 |

## 认证矩阵

| 维度 | 可进入外部 smoke | Fail-closed 条件 | 生产边界/证据 |
| --- | --- | --- | --- |
| 环境 | `IsTestnet=true` 且 execution enabled | Mainnet、execution disabled；即使 endpoint 外观为 Testnet，`IsTestnet=false` 仍全拒绝 | `BinanceFuturesAdapter.ValidateEnvironment` |
| REST endpoint | 精确官方 HTTPS origin | HTTP、Mainnet host、host suffix、端口、路径、query、fragment | `ProviderEndpointPolicy` + profile validation |
| WebSocket endpoint | `wss://stream.binancefuture.com/stream` | 任何替代 origin | `BinanceStreamClient` 冻结默认值 |
| 权限 | read/trade 为 true，withdraw 为 false | 缺失字段不推断；trade false 或 withdraw true | `/fapi/v2/account` 脱敏 evidence |
| 时钟 | `abs(server-local) <= 1000 ms` | 缺失或超过 1000 ms 时 `CanTrade=false` | `/fapi/v1/time` + `ApplyClockSkew` |
| capability | 精确 instrument，Available，fresh，Testnet 可用 | unknown/unsupported/stale/error/过期 | capability artifact；最大年龄 15 秒 |
| 标准订单状态 | NEW/PARTIALLY_FILLED/FILLED/CANCELED/REJECTED/EXPIRED | 未知、空值、新 provider 状态归一为 `UNKNOWN` | `NormalizeStandardOrderStatus` |
| reconcile | 原 client order id 只读查询且不重提 | 未知、查询失败、重复 place | reconcile artifact + mutation count |
| mutation 路径 | Risk Gate receipt 后由 `ReliableOrderExecutor` 执行 | 任何直连、无 receipt、未知 capability | audit correlation 链 |

Binance 的 `EXPIRED_IN_MATCH` 归一为终态 `EXPIRED`。未识别状态不猜测为成交、撤单或可重试，而是 `UNKNOWN` 并进入恢复/人工复核。提现权限为最小权限认证失败；生产 capability 的 `CanTrade` 还必须同时通过服务端时钟边界。

## 离线故障注入矩阵

| 故障 | 冻结结果 | Mutation 约束 |
| --- | --- | --- |
| HTTP 429 | 有界重试耗尽后 `Unavailable` | 仅重复原只读 GET，不得 POST/DELETE |
| HTTP 418 | 与 Binance rate-limit/IP-ban 等价处理，最终 `Unavailable` | 不得探测性下单 |
| timeout | 重试耗尽后 `UNKNOWN`/Unavailable，不推断请求结果 | mutation count 必须为零；真实下单超时只允许按 client order id reconcile |
| malformed JSON | `Unavailable` | 不使用默认值生成 capability 或订单 |
| partial payload | `Unavailable` | 缺字段不推断 read/trade/成交 |
| timestamp drift | 超过 1000 ms 时 `CanTrade=false` | mutation 前阻断 |
| duplicate order event | 完全相同事件去重，真实状态迁移保留 | 不因重复事件再次提交、撤单或挂保护 |
| 敏感错误字段 | 状态可审计，值全部 `[REDACTED]` | API key、secret、signature、account id 不得进入 artifact/log |

所有故障注入均使用本地 `HttpMessageHandler` 或内存事件，不访问网络。异常不得把 capability 提升为 Available，也不得触发 provider mutation。

## 请求签名与序列化 conformance

| 规则 | 冻结要求 |
| --- | --- |
| Canonical ordering | 签名前按参数名使用 `StringComparer.Ordinal` 升序排列，不依赖字典插入顺序 |
| URL encoding | 参数名和值按 UTF-8 RFC 3986 语义编码；空格为 `%20`，`/`、`+`、`&`、`=` 必须转义 |
| Decimal | 数量、价格使用 `InvariantCulture`，小数点始终为 `.` |
| Timestamp | UTC Unix milliseconds，使用 `InvariantCulture` 十进制整数 |
| Duplicate | 同名参数在签名前拒绝，不采用 first/last wins |
| Null/empty | null 参数省略；显式空字符串保留为 `name=`；空参数名拒绝 |
| `recvWindow` | 输入限制为 `1000..60000`，超过上限序列化为 `60000` |
| HMAC | HMAC-SHA256 覆盖不含 `signature` 的完整 canonical query，输出小写 hex |
| Secret handling | `signature`、API key、secret 只存在于瞬时内存请求；异常和日志必须脱敏 |

固定离线向量使用公开非凭据字符串 `phase4-public-api-key` / `phase4-public-test-secret`、timestamp `1700000000123`，验证 canonical query 与预计算 HMAC。fake handler 只捕获内存请求，mutation count 必须为零；不得把该向量描述为真实 Binance 凭据。

## 订单生命周期与 reconcile

目标机 smoke 必须以最小允许名义金额观察并关联以下事实：提交已被 provider 接受、至少一次部分成交或可证明的等价受控路径、完全成交、独立新订单的安全撤单。每个事件必须含 run/correlation/client-order/provider-order id、symbol、side、quantity、状态、provider event time、observed time 和脱敏 response hash。

超时、连接中断或 ACK 未知后，执行状态必须保持 ambiguous/unknown 并冻结新增风险。reconcile 只能通过原 client order id 调用只读查询；找到订单后恢复其真实状态，找不到、查询失败或状态冲突时输出 `manual-review-required`。禁止用第二次 place 调用“探测”结果。保护单、reduce-only、部分成交数量和最终撤单也必须进入同一审计关联链。

## 证据 artifact

每个 run 使用独立目录，至少包含：

| 文件 | 内容 |
| --- | --- |
| `access.json` | 候选 hash、endpoint、脱敏 credential slot、权限结果 |
| `clock.json` | 本地/服务端时间、offset、采样时间 |
| `capability.json` | capability 全字段、freshness 判定和 reason code |
| `order-lifecycle.jsonl` | 只追加的订单/成交/撤单事件链 |
| `reconcile.json` | ambiguous 注入、只读查询和无重提证明 |
| `audit.jsonl` | 授权、Risk Gate、`ReliableOrderExecutor`、保护与最终状态审计 |
| `manifest.sha256` | 上述文件的 SHA-256；生成后不可静默替换 |

artifact 不得含 API key、secret、签名、Authorization header、完整账户标识或未脱敏异常。run id、correlation id、client order id 与候选 hash 必须可交叉核对。

## 总集成外部凭据 smoke 清单

以下步骤必须串行、人工授权并在目标机执行；本离线测试不执行这些步骤：

1. 核对候选程序集版本、包 SHA-256、Windows 目标机和空白 run 目录。
2. 在 WPF 安全配置中选择 Testnet 槽，确认 Mainnet disabled，endpoint 精确匹配冻结 origin。
3. 使用专用低权限凭据；读取与期货交易开启，提现关闭；确认日志和 artifact 无秘密。
4. 查询服务端时间并验证 `abs(offset) <= 1000 ms`，否则停止并同步系统时钟。
5. 只读查询账户、市场目录和目标 symbol；在 15 秒 freshness 窗内取得 `Available` capability。
6. 在默认 Review 模式创建最小订单意图，保存用户确认、确定性 Risk Gate receipt、intent/artifact hash。
7. 仅通过 `ReliableOrderExecutor` 提交，观察 accepted、部分/完全成交事实及保护/reduce-only 行为。
8. 使用另一笔最小订单验证 cancel；记录 provider 最终状态，不以本地推断替代。
9. 注入 ACK/连接不确定场景或使用认证环境允许的等价演练；按原 client order id reconcile，确认零重复 place。
10. 确认未知/查询失败进入人工复核并冻结新增风险；不得自动重试 mutation。
11. 生成七类 artifact、敏感信息扫描和 `manifest.sha256`，由非执行者复核 correlation 链。
12. 仅当所有步骤对同一候选 hash 通过时输出 `binance-cert.smoke-passed`；否则输出具体失败码并保持“未认证”。

## 发布声明

允许的候选声明：`WPE <version> (<sha256>) passed Binance Futures Testnet credentialed smoke on <target/time>; Mainnet disabled.`

只有离线测试时必须声明：`Offline Binance Testnet certification contract passed; credentialed target-machine smoke not run; not certified.`

不得声明 Binance Mainnet 支持、真实资金验证、收益/胜率表现，亦不得将其他 provider 的离线 conformance 扩大为真实 Testnet 支持。
