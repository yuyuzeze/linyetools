# 脱敏利用者検索API

- **文档 ID**: api-spec
- **Schema 版本**: 1.0
- **模板**: fixture-api-spec v1.0
- **来源**: fixtures/workbooks/api-spec.xlsx
- **ingestor**: openpyxl+ooxml
- **creator**: Fixture Team
- **last_modified_by**: None
- **asset_directory**: fixtures/assets
- **template_match**: {"candidates": [{"accepted": true, "score": 1.0, "template_id": "fixture-api-spec", "version": "1.0"}], "mode": "template", "unrecognized_ranges": {"API概要": [], "リクエスト": ["A1:A1", "B1:E1"], "レスポンス": ["A1:A1", "B1:D1"]}}

## API概要

### api-info

- **API仕様書**: 
- **api_id**: API-DEMO-001
- **api_name**: 利用者検索API
- **endpoint**: /v1/demo-users
- **http_method**: GET

## リクエスト

### request-fields

| パラメータ名 | 場所 | データ型 | 必須 | 説明 |
| --- | --- | --- | --- | --- |
| query | query | string | false | 検索語 |
| limit | query | integer | false | 最大件数 |

### unrecognized-1

| リクエスト項目 |
| --- |

### unrecognized-grid-residual

|  |  |  |  |
| --- | --- | --- | --- |

## レスポンス

### response-fields

| 項目名 | データ型 | 必須 | 説明 |
| --- | --- | --- | --- |
| userId | string | true | 合成利用者ID |
| displayName | string | true | 表示名 |

### unrecognized-1

| レスポンス項目 |
| --- |

### unrecognized-grid-residual

|  |  |  |
| --- | --- | --- |
