# ExcelSpec Demo 式样书

按截图风格生成的两类多 Sheet 脱敏测试文件。

## 输入 Excel

| 文件 | 类型 | Sheets |
|------|------|--------|
| `workbooks/SCR-A0010_画面設計書_保証一覧.xlsx` | 画面設計書 | 表紙 / 修正履歴 / 画面レイアウト / 画面入出力項目一覧 / 画面アクション一覧 / 入力チェック |
| `workbooks/RPT-B0020_帳票設計書_予見事故記録簿.xlsx` | 帳票設計書 | 表紙 / 修正履歴 / 帳票概要 / 帳票レイアウト / 帳票編集定義 |

重新生成：

```powershell
python demo\build_demo_workbooks.py
```

## 转换命令

```powershell
cd ExcelSpec

# 画面設計書
python -m excelspec convert demo\workbooks\SCR-A0010_画面設計書_保証一覧.xlsx `
  -o demo\out -f md `
  --template demo\templates\demo-screen-design.yaml `
  --asset-dir demo\out\assets

# 帳票設計書
python -m excelspec convert demo\workbooks\RPT-B0020_帳票設計書_予見事故記録簿.xlsx `
  -o demo\out -f md `
  --template demo\templates\demo-report-design.yaml `
  --asset-dir demo\out\assets

# 也可自动匹配模板目录，并输出 html / json / jsonl
python -m excelspec convert demo\workbooks -o demo\out -f html --template-dir demo\templates --asset-dir demo\out\assets
```

## 输出目录

结果在 `demo/out/`：

- `.md` / `.html`：可读式样书
- `.json`：完整 DocumentIR
- `.jsonl`：AI 知识库分块
- `assets/`：从布局 sheet 抽出的嵌入图
