# 脱敏利用者検索API

## API概要

### api-info

- **API ID**: API-DEMO-001
- **API名**: 利用者検索API
- **エンドポイント**: /v1/demo-users
- **HTTPメソッド**: GET

## リクエスト

### request-fields

| パラメータ名 | 場所 | データ型 | 必須 | 説明 |
| --- | --- | --- | --- | --- |
| query | query | string | false | 検索語 |
| limit | query | integer | false | 最大件数 |

## レスポンス

### response-fields

| 項目名 | データ型 | 必須 | 説明 |
| --- | --- | --- | --- |
| userId | string | true | 合成利用者ID |
| displayName | string | true | 表示名 |
