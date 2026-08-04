# 脱敏利用者検索画面

- **文档 ID**: screen-design
- **Schema 版本**: 1.0
- **模板**: fixture-screen-design v1.0
- **来源**: fixtures/workbooks/screen-design.xlsx
- **ingestor**: openpyxl+ooxml
- **creator**: Fixture Team
- **last_modified_by**: None
- **asset_directory**: fixtures/assets
- **template_match**: {"candidates": [{"accepted": true, "score": 1.0, "template_id": "fixture-screen-design", "version": "1.0"}], "mode": "template", "unrecognized_ranges": {"改訂履歴": [], "画面項目": ["A1:A1", "A7:A8", "B1:H8"], "表紙": ["A12:A12", "A11:D12"]}}

## 表紙

### 表紙

- **画面設計書**: 
- **screen_id**: SCR-DEMO-001
- **screen_name**: 利用者検索
- **author**: 担当A
- **created_at**: 2026-01-15

### 画面レイアウト

_空表_

![脱敏検索画面](fixtures/screens/layout.png)

![Picture](fixtures/assets/表紙-image-1.png)

[検索ボタンで一覧を更新](ooxml://xl/drawings/drawing1.xml#shape-1)

### unrecognized-1

| 画面レイアウト |
| --- |

### unrecognized-grid-residual

|  |  |  |  |
| --- | --- | --- | --- |

## 改訂履歴

### 改訂履歴

<table class="excelspec-table">
  <tr>
  </tr>
  <tr>
    <th data-source-cell="A2">版</th>
    <th data-source-cell="B2">日付</th>
    <th data-source-cell="C2">変更内容</th>
    <th data-source-cell="D2">担当</th>
  </tr>
  <tr>
    <td data-source-cell="A3">1.0</td>
    <td data-source-cell="B3">2026-01-15</td>
    <td data-source-cell="C3">新規作成</td>
    <td data-source-cell="D3">担当A</td>
  </tr>
  <tr>
    <td data-source-cell="A4">1.1</td>
    <td data-source-cell="B4">2026-02-01</td>
    <td data-source-cell="C4">検索条件を追加</td>
    <td data-source-cell="D4">担当B</td>
  </tr>
</table>

## 画面項目

### 画面項目表

<table class="excelspec-table">
  <tr>
  </tr>
  <tr>
    <th data-source-cell="C3">データ型</th>
    <th data-source-cell="D3">桁数</th>
  </tr>
  <tr>
    <td data-source-cell="A4">USR-ID</td>
    <td data-source-cell="B4">利用者ID</td>
    <td data-source-cell="C4">文字列</td>
    <td data-source-cell="D4">12</td>
    <td data-source-cell="E4">○</td>
    <td data-source-cell="F4">利用者ID</td>
    <td data-source-cell="G4">英数字</td>
    <td data-source-cell="H4"></td>
  </tr>
  <tr>
    <td data-source-cell="A5">USR-NAME</td>
    <td data-source-cell="B5">利用者名</td>
    <td data-source-cell="C5">文字列</td>
    <td data-source-cell="D5">40</td>
    <td data-source-cell="E5"></td>
    <td data-source-cell="F5">利用者名</td>
    <td data-source-cell="G5"></td>
    <td data-source-cell="H5"></td>
  </tr>
  <tr>
    <td data-source-cell="A6">SEARCH</td>
    <td data-source-cell="B6">検索</td>
    <td data-source-cell="C6">ボタン</td>
    <td data-source-cell="D6"></td>
    <td data-source-cell="E6"></td>
    <td data-source-cell="F6">検索</td>
    <td data-source-cell="G6"></td>
    <td data-source-cell="H6">押下で検索</td>
  </tr>
</table>

### unrecognized-1

<table class="excelspec-table">
  <tr>
  </tr>
</table>

### unrecognized-2

| 備考 |
| --- |
| 個人名・実データを含まない合成 fixture |

### unrecognized-grid-residual

<table class="excelspec-table">
  <tr>
  </tr>
  <tr>
    <td data-source-cell="B7"></td>
    <td data-source-cell="C7"></td>
    <td data-source-cell="D7"></td>
    <td data-source-cell="E7"></td>
    <td data-source-cell="F7"></td>
    <td data-source-cell="G7"></td>
    <td data-source-cell="H7"></td>
  </tr>
  <tr>
    <td data-source-cell="B8"></td>
    <td data-source-cell="C8"></td>
    <td data-source-cell="D8"></td>
    <td data-source-cell="E8"></td>
    <td data-source-cell="F8"></td>
    <td data-source-cell="G8"></td>
    <td data-source-cell="H8"></td>
  </tr>
</table>
