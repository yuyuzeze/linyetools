# Legacy 模板高级功能：文件名匹配 + 区域截图

这些功能**只在显式使用 legacy 模板时生效**（`--template` / `--legacy-template` /
`--template-dir` / `--auto-legacy-template`，或模板区域配置 `screenshot: true`）。
默认零配置 fast（`jpspec parse input.xlsx -o output`）**不加载模板、不做文件名匹配、
不启动 Excel、不计算动态截图范围**，性能与现状一致。

## 1. 文件名正则匹配

`match` 下新增（可选）：

```yaml
match:
  file_name_patterns:
    - "^.*画面設計書.*\\.xlsx$"
    - "^.*SCR-[A-Z]?\\d+.*\\.(xlsx|xlsm)$"
  require_file_name_match: false   # true 时自动匹配中不匹配即拒绝
  sheet_name_patterns: [...]
```

规则：

- 只匹配 `Path.name`，不匹配完整路径。
- 文件名先做 NFKC 规范化，默认大小写不敏感。
- 任一 pattern 命中 → `filename_score = 1.0`，否则 `0.0`。
- 文件名是**辅助信号**：不改变主评分，只作为排序 tie-break；不会让明显不匹配的
  Sheet/fingerprint 通过。
- 模板没有 `file_name_patterns` → 评分与输出与旧版**完全一致**。
- 非法正则在模板**加载阶段**报错（`TemplateValidationError`）。
- `require_file_name_match: true` 且不匹配：
  - `--template-dir` / `--auto-legacy-template` 自动匹配时该模板被**拒绝**（跳过 Sheet/fingerprint 评分，属性能预筛）。
  - 显式单个 `--legacy-template` 仍**强制运行**，并写 info diagnostic `template.filename_not_matched`。

`TemplateCandidate` / audit / CLI JSON 增加：`filename_score`、`filename_matched_pattern`、
`filename_required`、`filename_accepted`。

## 2. 凡例区域截图（`connected_region`）

`画面入出力項目一覧` 的凡例从 `■凡例` 起，到 `No.` 表头上一行止，横向收缩到与凡例
连续的内容/边框列（不截到 XFD、不截整表宽度）：

```yaml
- region_id: legend
  region_type: image
  title: 凡例
  locator:
    mode: anchor
    anchor_pattern: "^\\s*■?\\s*凡例"
    end_anchor_pattern: "^(No\\.?|№)$"
  extractor:
    kind: freeform
    options:
      screenshot: true
      screenshot_engine: excel_com
      screenshot_bounds: connected_region
      padding_rows: 1
      padding_columns: 1
      text_fallback: true
```

- 横向范围由**非空 / 合并 / 有边框-填充**的单元格连续区决定；孤立远距离 style-only 格不参与。
- 成功：输出 PNG + `AssetIR(screenshot)`，`region.asset_ids` 引用，MD/HTML 显示图片、不再输出凡例 bullet；
  asset/region metadata 记录 `capture_method` / `requested_range` / `resolved_range` / `bounds_method` / `template_region_id`。
- 失败：保留凡例 freeform 文本，写 `template.screenshot_failed`（含 Sheet / resolved range / COM 错误），转换继续。
- 一个工作簿的多张截图共用一个 `ExcelCaptureSession`（Excel 只启动一次、Workbook 只打开一次）。

## 3. 画面遷移図：固定三边 + 动态底部（`dynamic_bottom`）

见 `templates/linye-screen-transition-v1.yaml`。top（anchor+row_offset）/left/right 固定，
仅 bottom 随图内容变化：

```yaml
options:
  screenshot_bounds: dynamic_bottom
  top_from_anchor: true
  left_column: B
  right_column: BZ
  padding_bottom_rows: 2
  max_bottom_row: 2000
  text_fallback: true
```

bottom 计算（优先级）：

1. **OOXML Drawing anchor**（`twoCellAnchor.from/to` / oneCellAnchor / image / shape / connector /
   group），从 SparseWorkbookIR/资产直接读，不扫描稠密网格。
2. **Excel COM Shape bounds**（`ExcelCaptureSession.resolve_shape_bounds`，**已真正实现**）：在**同一个
   lazy ExcelCaptureSession**（即将用于截图的那一个）内遍历 `Worksheet.Shapes`，读取
   `TopLeftCell/BottomRightCell/Top/Left/Width/Height/Type/Name`，取相关 Shape 的 union bottom。
   不创建第二个 Excel.Application、不再次 `Workbooks.Open`；默认对 dynamic_bottom 区域启用
   （`prefer_com_shape_bounds: false` 可关闭）；COM 不可用/异常写 `template.com_shape_bounds_failed` 并回退。
3. **保守回退**：固定 left/right 内最后一个非空/连续边框行；无法确定时写
   `screenshot.dynamic_bottom_not_found`，保留文本，**绝不**截到第 1048576 行。

`resolved_bottom = max(ooxml_bottom, com_shape_bottom, content_bottom, bordered_bottom, merged_bottom)
+ padding_bottom_rows`；**COM 大于 OOXML 时采用 COM**（保证 Connector 不被裁掉）。再受**下一个 repeat
anchor**（section 上界，超过即排除，属于下一张图）、`max_bottom_row`（硬上限）、Excel 最大行数三重限制。
`repeat_anchor: true` 时每个 `■画面遷移図` 独立成图、独立 bottom、独立 asset，互不截入。

metadata 记录：`ooxml_bottom / com_shape_bottom / content_bottom / bordered_bottom / merged_bottom /
resolved_bottom / bounds_method / dominant_source / included_shapes / excluded_shapes`。
Shape 类型覆盖 Connector / Arrow / TextBox / AutoShape / Picture / GroupShape（外层 bounds 有效用外层，否则遍历 GroupItems）。

## 尚未支持 / 未验证

- **真实 PNG 未在当前环境验证**：本机 pywin32 已装但 Excel COM 不可用（`-2147221005`，Excel 未安装/注册）。
  **COM 实现和 mock 测试完成，但真实 PNG 未能在当前环境验证。** 需在装有桌面 Excel 的机器上确认。
- `absoluteAnchor`（无单元格锚点）在无 OOXML 且 Excel 不可用时退化为内容/边框回退；有 Excel 时由 COM
  `TopLeftCell/BottomRightCell` 覆盖。
- 无真实生产 XLSX，`linye-screen-transition-v1.yaml` 仅以合成 fixture 验证，**不代表真实业务准确率**。
