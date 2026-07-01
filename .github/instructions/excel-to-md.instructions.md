---
name: ExcelToMd
description: Excel (.xlsx) を Markdown に変換する Python ツール。日本語設計書の表紙・一覧・画像出力の改修・実行時に参照する。
applyTo: "ExcelToMd/**"
---

# ExcelToMd

`ExcelToMd/excel_to_md.py` は `.xlsx` を UTF-8 の Markdown（GFM 管道表）に変換する。依存は **Python 3.10+** と **openpyxl** のみ。

## 実行

```bash
cd ExcelToMd
pip install -r requirements.txt

# 単一ファイル
python excel_to_md.py <入力.xlsx> -o <出力ディレクトリ>

# フォルダ一括（各 xlsx → 出力先の同名サブフォルダ）
python excel_to_md.py <入力フォルダ> -o <出力ディレクトリ>

# サブフォルダも含む
python excel_to_md.py <入力フォルダ> -o <出力ディレクトリ> --recursive
```

| オプション | 説明 |
|-----------|------|
| `--one-file` | 全シートを 1 つの `.md` に統合 |
| `--no-shapes` | DrawingML のシェイプ内テキストを出力しない（デフォルトは出力） |
| `--no-export-images` | 埋め込み画像を出力しない（デフォルトは出力） |

## 出力規約（必須）

ユーザー向け Markdown の**見出し・ラベル・空表記はすべて日本語**にする。中国語の見出しは使わない。

| 用途 | 見出し・表記 |
|------|-------------|
| 表紙メタデータ | `### 表紙情報` |
| 主データ表 | `### 一覧` |
| 埋め込み画像 | `### 埋め込み画像（{dir}/）` |
| シェイプ文字 | `### シェイプ内テキスト（DrawingML、順序は保証されません）` |
| 空シート | `_（空）_` |
| 画像ファイル名 | `図{sheet}-{idx}` 形式 |

シェイプ内テキストが**0 件のときはセクションごと出力しない**。

## アーキテクチャ

```
convert_path()          # 単一ファイル or フォルダ一括
  └ convert_workbook()  # 1 ワークブック
       ├ sheet_to_markdown_content()   # 表紙 + 一覧
       ├ export_sheet_embedded_images() # xl/media から PNG 等
       └ extract_shape_texts_from_xlsx()
```

### 表紙シート（`表紙` タブ）

縦積みレイアウト専用。`cover_sheet_to_markdown()` が処理する。

| Excel ブロック | Markdown |
|----------------|----------|
| プロジェクト帯（長文） | プロジェクト情報テーブル「プロジェクト」行 |
| サブシステム名 | 「サブシステム」行 |
| 基本設計 / 詳細設計 | 「設計フェーズ」行 |
| SCR-A0300 等 | 「画面ID」行 |
| 譲渡 等の短い画面名 + 画面設計書 | 見出し `**譲渡 — 画面設計書**` |
| 文書番号・バージョン・作成者・作成日 | 「文書属性」2 列テーブル |

判定: シート名が `表紙`、または `画面設計書` + 文書属性表があり一覧ヘッダが無い場合。

### 表（一覧）の変換

- **GFM 管道表**のみ。HTML `<table>` は使わない。
- **結合セル**: `_pipe_cell_value()` — 結合範囲の**左上セルのみ**値を持つ。他は空。
- **全空行をスキップ**、**全空列を `_trim_empty_columns()` で削除**。
- 表紙と一覧の境界: `_detect_main_table_start_row()`（`No.` / `画面項目名` 等のヘッダ行を検出）。

### 表紙情報（メタデータ）

- `_extract_row_metadata_entries()` で行を左から走査。
- **既知ラベル**（`_KNOWN_METADATA_LABELS`）の右隣の最初の非空セルを値とする。
- 結合セル重複防止: `_cell_raw_value()` は `_pipe_cell_value()` と同様、左上以外は `None`。
- 結合範囲を跨いで進む: `_next_col_after_cell()`。
- 表紙全体で **kv・standalone・凡例行の重複を除去**（`seen_kv` / `seen_standalone`）。
- 日付は `YYYY/M/D` 形式（`_format_metadata_value()`）。

既知ラベルに追加する場合は `_KNOWN_METADATA_LABELS` を更新する。

### 画像

- `{ワークブック名}_images/` に PNG 等を保存。
- Markdown は `![alt](相対パス)` で**直接参照**（HTML ラッパーは作らない）。

## 改修時の注意

1. **結合セルを読む処理**は必ず左上判定を入れる。列走査で同一値が繰り返すバグの主因。
2. 表紙の key-value は**左から順にペア化しない**。ラベル列の右隣セルを値とする。
3. `「システム」` 等の曖昧な部分一致でラベル判定しない（長文が誤ってラベルになる）。
4. 変更範囲は最小限。既存の `openpyxl` + 標準ライブラリ方針を維持。
5. README.md の CLI 説明も合わせて更新する。
6. コミットはユーザーが明示したときのみ。

## 対象ドキュメントの想定

林業業務システム等の日本語 Excel 設計書（例: `画面入出力項目一覧`、`入力チェック`）。表紙に `プロダクト` / `サブシステム` / `作成日` / `画面ID`、一覧に `No.` / `画面項目名` / `種別` 等。

## 既知の制限

- `.xlsx` のみ（`.xls` 非対応）
- 矢印・フロー拓扑は Markdown で再現不可
- シェイプ内テキストの順序は画面と一致しない場合がある
- チャートオブジェクトは `xl/media` に無いことがあり、画像化できない場合がある

## テスト

```bash
python -m py_compile ExcelToMd/excel_to_md.py
python ExcelToMd/excel_to_md.py --help
```

実ファイルで変換後、表紙情報の重複・ペア誤り・一覧の空列がないかプレビューで確認する。
