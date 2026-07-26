# WPE SBOM / License Scan 门禁执行版

面向 WPE 现有 `.NET + Next` 主线，目标是把供应链风险前置到 CI，避免强传染许可证和未知许可依赖进入主干。

## 1. 工具候选

### SBOM 生成
- `.NET`：`CycloneDX/cyclonedx-dotnet`
- `Next.js / Node`：`CycloneDX/cyclonedx-node-npm` 或 `cdxgen`
- 全栈兜底：`anchore/syft` 生成 SPDX
- 仓库级归档：GitHub SBOM Export API

### License 扫描
- 首选：`oss-review-toolkit/ort`
- 深度校验：`aboutcode-org/scancode-toolkit`
- 辅助：`microsoft/sbom-tool`（偏企业化 SPDX 输出）

## 2. 推荐流水线

1. `restore / install`
2. 生成组件清单
3. 生成 SBOM
4. license 扫描
5. policy 判定
6. 输出 `NOTICE` / `THIRD_PARTY_NOTICES`
7. 上传 SBOM 和审计产物

## 3. CI 失败规则

- 任一依赖命中 `禁止清单` 直接失败
- 任一依赖许可证未知、`NOASSERTION`、无法解析，进入失败
- 任一 `MIT / Apache` 依赖的传递依赖命中强传染许可，失败
- `LGPL` 进入闭源主干时，若存在静态链接 / 合并分发，失败
- `CC-BY` 出现在代码/库依赖链中，默认失败，必须人工放行
- `GPL` 直接失败
- SBOM 生成失败直接失败
- NOTICE 产物生成失败直接失败

## 4. 清单策略

### 允许直接引入
- `MIT`
- `Apache-2.0`

### 仅设计借鉴
- 仅文档、接口、流程、架构，不复制代码
- `CC-BY-4.0` 的仓库优先按此类处理

### 需要法务审查
- `LGPL-3.0`
- `NOASSERTION`
- 许可证与依赖树不清晰的仓库

### 禁止引入
- `GPL-3.0`
- 任何会把闭源主项目拉入传染范围的派生复用

## 5. 产物要求

- `sbom.spdx.json`
- `sbom.cdx.json`
- `license-report.json`
- `THIRD_PARTY_NOTICES.md`
- `NOTICE`
- `policy-decision.json`

## 6. NOTICE 生成规则

- 列出所有直接依赖与高风险传递依赖
- 对每个组件保留 `name / version / license / source / attribution`
- `MIT / Apache` 自动收录
- `LGPL / CC-BY / NOASSERTION` 进入人工审核段
- `GPL` 不进入发布包

## 7. WPE 落地建议

- `.NET` 主线优先用 `cyclonedx-dotnet + ort`
- `Next` 主线优先用 `cyclonedx-node-npm + ort`
- 在 PR 上跑轻量门禁，在 nightly 跑 `scancode-toolkit` 深扫
- 所有门禁结果统一写入 `policy-decision.json`
- 发布时强制带 `SBOM + NOTICE + license-report`

## 8. 最小可行配置

- 生成：`cyclonedx-dotnet`、`cyclonedx-node-npm`
- 聚合：`syft`
- 扫描：`ort`
- 深扫：`scancode-toolkit`
- 审计输出：`NOTICE` + `THIRD_PARTY_NOTICES.md`

## 9. 结论

对于 WPE，最稳妥的主线是 `SBOM 生成 + ORT policy gate + ScanCode 深扫 + NOTICE 归档`。
这样既能覆盖 `.NET` 与 `Next`，也能把已识别的 `MIT / Apache / LGPL / GPL / CC-BY / NOASSERTION` 风险统一收口。
