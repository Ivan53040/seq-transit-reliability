# SEQ Transit Reliability: 架構與程式碼學習手冊 (Codebase Learning Guide)

本手冊為 `seq-transit-reliability` 專案的核心深度學習指南。依據 `SEQ Transit Reliability Implementation Plan.md` Phase 1 所列出的 **11 個關鍵源碼檔案** 與 **面試核心問答** 撰寫，幫助你徹底理解每一行代碼背後的設計決策與取捨（Trade-offs）。

---

## 快速導航與全景資料流 (Architecture in 2 Minutes)

在面試中，請能在 **2 分鐘內** 用自己的話講出這個資料流：

```
[Translink GTFS-RT Protobuf] 
      │ (每分鐘 HTTP GET 下載)
      ▼
[Fetch Lambda] ──(寫入 raw protobuf)──> [S3 快照桶: trip-updates/v1/{timestamp}.pb]
                                                       │
                                                       ▼ (每 30 分鐘批次讀取)
                                                [Load Lambda]
                                                       │
                                         (在記憶體中用 ObservationFold 折疊最新狀態)
                                                       │
                                        (以 PostgreSQL unnest 單一交易批量 Upsert)
                                                       ▼
                                          [Aurora PostgreSQL (No-VPC)]
                                                       │ (EF Core 唯讀查詢已通過站點)
                                                       ▼
                                         [ASP.NET Core API on Lambda]
                                                       │ (CloudFront 快取 60 秒)
                                                       ▼
                                           [React Dashboard (Vite)]
```

* **數據生命週期**：從遠端 Protobuf 二進制串流，到 S3 不可變快照，到記憶體折疊與關聯式資料庫批量 Upsert，最終透過聚合計算（只算車輛已通過的站點）產出路線與站點的準點率指標。

---

## Phase 1：11 個核心架構設計深度解析

---

### 1. `proto/gtfs-realtime.proto` (GTFS 實時協議規範)
**代碼位置**：[`proto/gtfs-realtime.proto`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/proto/gtfs-realtime.proto)

#### Q: 什麼是 `stop_sequence`？`SKIPPED` 和 `NO_DATA` 的差異是什麼？

* **`stop_sequence`（站點序號）**：
  * 在一趟車程（Trip）中，車輛停靠站點的遞增整數序號（1, 2, 3...）。
  * **為什麼不能只用 `stop_id`？** 因為環狀路線或 8 字型路線（Loop routes）在同一趟行程中可能會**重複造訪同一個站點兩次**。只有 `(service_date, trip_id, stop_sequence)` 的組合才能唯一標識「這趟車的第 N 次停靠」。
* **`SKIPPED` (已跳過)**：
  * 表示該車次**不會停靠**該站點（例如：急行特快車跳站、或是道路施工臨時跳過該站）。
  * 既然不停靠，該站就沒有實際的到站/離站行為，不能算入日常準點率統計。
* **`NO_DATA` (無實時數據)**：
  * 表示傳輸協議中該站點**目前沒有任何實時預測資訊**（通常用於只預測車程前半段，後半段尚未有預測值）。
  * 本專案解析器會略過 `NO_DATA`，避免將沒有預測時間的空站點寫入資料庫。

---

### 2. `src/SeqTransit.Core/Gtfs/TripUpdateFeedParser.cs` (實時 Feed 解析器)
**代碼位置**：[`src/SeqTransit.Core/Gtfs/TripUpdateFeedParser.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Gtfs/TripUpdateFeedParser.cs)

#### Q: 為什麼有些站點更新會被略過 (Skipped)？何時 Service Date 需要猜測？

* **略過站點更新（`ToObservation(...) returns null`）的條件**：
  1. **無 TripId**：沒有穩定的主鍵可以關聯。
  2. **無 `stop_sequence`**：無法區分環狀路線的多重停靠。
  3. **`ScheduleRelationship` 是 `SKIPPED` 或 `NO_DATA`**：不具有有效的實際運行觀測數據。
  4. **同時缺少 `arrival`、`departure` 與 `event_time`**：完全沒有時間戳或延遲秒數的無效節點。
* **何時 `ServiceDate` 是推測的？**
  * GTFS-RT 協議中的 `TripDescriptor.start_date` 是 `optional`（選填）。
  * 如果來源 Feed **沒有提供 `start_date`**，代碼會在 [`BrisbaneTime.DateOf(feedTimestamp)`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Gtfs/TripUpdateFeedParser.cs#L134-L138) 使用「Feed 快照時間點在布里斯本時區（UTC+10）的日期」作為備用。
  * **潛在風險**：跨午夜運行的深夜班次（例如 23:45 出發、00:30 到達的車次），若沒有 `start_date`，過午夜後的快照會被誤判為隔天的營運日（Service Date）。

---

### 3. `SnapshotKeys.cs` 與 `SnapshotFetcher.cs` (快照命名與採集)
**代碼位置**：
* [`src/SeqTransit.Core/Ingestion/SnapshotKeys.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Ingestion/SnapshotKeys.cs)
* [`src/SeqTransit.Core/Ingestion/SnapshotFetcher.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Ingestion/SnapshotFetcher.cs)

#### Q: 為什麼 S3 Key 是用 Feed 本身的時間戳建立的？哪些錯誤會自動重試？

* **為什麼以 Feed 時間戳做 S3 Key（`trip-updates/v1/{unix-seconds:D10}.pb`）？**
  1. **天然冪等性（Idempotence）**：若同一分鐘重試抓取、或是抓到相同版本，寫入 S3 時檔名完全相同，直接覆蓋不會造成重複快照。
  2. **字典序排序即時間序**：使用 10 位數補零（`D10`），字串字母排序就等於時間戳升冪排序。S3 `ListObjectsV2` 原生支援字典序，Loader 只要呼叫「列出大於上一次 Watermark 的 Key」，即可在一次請求中取得接續的未處理快照。
* **哪些錯誤會重試？**
  * 代碼中的 [`IsTransient`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Ingestion/SnapshotFetcher.cs#L77-L83) 定義了瞬時可重試錯誤：
    * 網路斷線 / DNS 失敗（`HttpRequestException` 且無 StatusCode）。
    * HTTP 5xx 伺服器錯誤（Translink 端暫時故障）。
    * HTTP 429 Too Many Requests（觸發限流）。
    * HTTP Client Timeout（`TaskCanceledException` 非主動取消）。
  * **不可重試**：4xx 客戶端錯誤（如 404、403），代表請求路徑或權限有誤，重試亦無意義。

---

### 4. `src/SeqTransit.Core/Ingestion/ObservationFold.cs` (記憶體狀態折疊)
**代碼位置**：[`src/SeqTransit.Core/Ingestion/ObservationFold.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Ingestion/ObservationFold.cs)

#### Q: 為什麼最新快照優先？寫入前為何要先在記憶體中「折疊」？

* **最新快照優先（Newest Wins）**：
  * 在一趟行駛過程中，每分鐘的快照都會更新對前方站點的延誤預測。越接近當前時間點的快照，其 GPS 追蹤與交通預測越精確，因此最後一筆觀測值（`LastFeedAt` 最新）代表最接近實際發生的事實。
  * 同時保留最早看見該站點的時間（`FirstSeenAt = Min(...)`），方便追蹤此站點何時首次被納入排程。
* **為何寫入前要在記憶體先折疊？**
  1. **大幅減少 I/O**：若 30 分鐘有 30 個快照，直接寫入需發送 30 輪資料庫請求；折疊後合併成單一狀態集，只需一次資料庫互動。
  2. **消除重複主鍵衝突**：PostgreSQL 的 `INSERT ... ON CONFLICT DO UPDATE` 在**單一 SQL 陳述式內**若遇到多筆相同 Primary Key 的資料，會直接噴錯 `ON CONFLICT DO UPDATE command cannot affect row a second time`。折疊後保證送入資料庫的每一筆 `(service_date, trip_id, stop_sequence)` 都是唯一的。

---

### 5. `src/SeqTransit.Core/Ingestion/SnapshotLoader.cs` (快照載入器)
**代碼位置**：[`src/SeqTransit.Core/Ingestion/SnapshotLoader.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Core/Ingestion/SnapshotLoader.cs)

#### Q: 如果載入過程中途崩潰 (Crash) 會發生什麼事？

* **單一交易保護（Atomicity）**：
  * 載入器將整批觀測資料（`stop_delay_observation`）、取消資料（`trip_cancellation`）與快照記錄（`feed_snapshot`）包裹在同一個資料庫 Transaction 中。
  * 資料庫中的 Watermark（水位標記）是透過 `SELECT max(feed_timestamp) FROM feed_snapshot` 查出來的。
* **崩潰時的行為**：
  * 若載入過程任一步驟異常拋錯，資料庫交易會自動 **Rollback**。
  * `feed_snapshot` 不會增加任何記錄，資料庫 Watermark **停留在崩潰前的狀態**。
  * 下一次調度執行時，Loader 依然會從舊的 Watermark 開始重新抓取這批快照。由於設計具備冪等性，重新載入完全安全。

---

### 6. `db/migrations/*.sql` 與 `ObservationWriter.cs` (高效批量寫入)
**代碼位置**：
* [`db/migrations/0001_initial_schema.sql`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/db/migrations/0001_initial_schema.sql)
* [`src/SeqTransit.Data/ObservationWriter.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Data/ObservationWriter.cs)

#### Q: SQL 中的 `unnest` 與 `ON CONFLICT ... WHERE` 分別解決了什麼問題？

* **`unnest`（陣列展開）**：
  * 代碼不採取「逐行產生 `INSERT VALUES (...), (...)...`」的龐大 SQL 字串拼接，而是將每一欄序列化為陣列（如 `@service_date::date[]`, `@trip_id::text[]`），透過 PostgreSQL 原生的 `unnest(...)` 展開成虛擬資料表。
  * **好處**：幾萬筆資料僅需傳遞一組陣列參數，在單一網路 Round-trip 內完成，極其節省網路頻寬與 SQL 解析開銷。
* **`ON CONFLICT ... WHERE EXCLUDED.last_feed_at > t.last_feed_at`**：
  * 這是保證**亂序載入安全**的關鍵條件防護。
  * 如果歷史補跑或不同批次產生時間交錯，只有當「新傳入的快照時間（`EXCLUDED.last_feed_at`）」**大於**「資料庫現有記錄的時間（`t.last_feed_at`）」時，才允許更新數值。
  * 徹底杜絕「舊數據意外覆蓋新數據」的 Race Condition。

---

### 7. `src/SeqTransit.Data/TransitDataSource.cs` (資料來源與 IAM 認證)
**代碼位置**：[`src/SeqTransit.Data/TransitDataSource.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Data/TransitDataSource.cs)

#### Q: 應用程式如何在沒有密碼的情況下登入 Aurora 資料庫？

* **AWS RDS IAM Database Authentication**：
  * 程式碼透過 `RDSAuthTokenGenerator.GenerateAuthTokenAsync(...)`，使用執行個體（Lambda）自身的 IAM 角色憑證向 AWS STS 簽署產生一個臨時 Token（格式類似預先簽署的 SigV4 授權字串）。
  * 這個 Token 有效期為 15 分鐘，充當連線時的密碼傳給 PostgreSQL。
* **Npgsql 連線池定期刷新**：
  * 代碼設定 `builder.UsePeriodicPasswordProvider(..., successRefreshInterval: 10 分鐘)`。
  * 連線池會在背景每 10 分鐘自動換發新 Token，因此長生命週期的連線池永遠具備有效認證，系統中無需儲存任何明文密碼或 Secrets Manager 密鑰。

---

### 8. `ReliabilityQueries.cs` 與 `ReliabilityEndpoints.cs` (準點率計算)
**代碼位置**：
* [`src/SeqTransit.Data/ReliabilityQueries.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Data/ReliabilityQueries.cs)
* [`src/SeqTransit.Api/Endpoints/ReliabilityEndpoints.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Api/Endpoints/ReliabilityEndpoints.cs)

#### Q: 為什麼準點率統計只計算「車輛已經通過」的站點？

* **關鍵過濾條件**：
  ```csharp
  // PassedStops 過濾條件
  .Where(o => o.EventTime != null && o.EventTime < now)
  ```
* **核心原因**：
  * GTFS-RT 中的未來站點時間是「**預測值（Prediction）**」，隨路況隨時可能劇烈跳動（塞車、號誌、事故）。
  * 只有當站點的 `EventTime < now`（事件時間已經過去），該數值才沉澱為車輛通過該站的「**實際最終延誤（Observed Outcome）**」。
  * 如果把未來站點的延誤也算進去，得出的準點率是「未來預報」而非「真實運行表現」，會嚴重失真。

---

### 9. `src/SeqTransit.Ingestion/Functions.cs` (Lambda 生命週期)
**代碼位置**：[`src/SeqTransit.Ingestion/Functions.cs`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/src/SeqTransit.Ingestion/Functions.cs)

#### Q: Lambda 容器中，哪些代碼只初始化一次，哪些是每次呼叫都執行？

* **只初始化一次（冷啟動時，跨呼叫重複使用）**：
  * `static readonly HttpClient Http = new(...)`：重用連線與 Socket，避免通訊埠耗盡（Socket Exhaustion）。
  * `static readonly Lazy<NpgsqlDataSource> DataSource = ...`：PostgreSQL 連線池跨調用複用，大幅減少重複 TCP 與 TLS 握手開銷。
  * 建構子中的 `AmazonS3Client` 與 S3 / Ingestion 物件綁定。
* **每次調用執行（`HandleAsync`）**：
  * `Settings.Budget(context, timeout)`：從 Lambda 上下文中讀取剩餘執行時間（`context.RemainingTime`），動態建立超時 CancellationToken。
  * 下載 Feed、解析快照、或讀取 S3 批次並寫入資料庫。
  * 結構化紀錄（例如 `SNAPSHOT_STORED` 或 `FEED_STALE`）。

---

### 10. `infra/lib/seq-transit-stack.ts` (CDK 基礎架構)
**代碼位置**：[`infra/lib/seq-transit-stack.ts`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/infra/lib/seq-transit-stack.ts)

#### Q: 為什麼架構完全沒有放進 VPC？四個 CloudWatch Alarm 分別監控什麼？

* **為什麼沒有 VPC？（關鍵成本考量）**
  * 在 AWS 中，若 Lambda 放進 VPC，要存取公網（抓取 Translink 外部 API）必須透過 **NAT Gateway**。
  * 單一 NAT Gateway 每月固定費用至少 **~$32 USD**，這對小型/個人 Side Project 是一筆不必要的固定負擔。
  * 本專案採用 **Aurora Express Configuration（具備 Internet Access Gateway）**，資料庫可直接透過公共網域名稱並搭配 TLS + IAM Authentication 進行連線，因此 Lambda 完全留在無 VPC 的公網環境，兼顧安全性與 **$0 NAT 成本**。
* **四個 CloudWatch Alarm**：
  1. `NoSnapshotsAlarm`：若 5 分鐘內存入 S3 的快照少於 3 個即告警（代表抓取端崩潰或 Translink API 斷線）。
  2. `FeedStaleAlarm`：若 10 分鐘內快照年齡多次超過 5 分鐘（代表 Translink 伺服器本身卡死未更新數據）。
  3. `LoadErrorsAlarm`：載入 Lambda 連續 2 次執行失敗（資料庫連線或 SQL 語句異常）。
  4. `ApiErrorsAlarm`：API 5 分鐘內拋出 5 次以上 5xx 錯誤。

---

### 11. `web/src/App.tsx` (前端看板狀態處理)
**代碼位置**：[`web/src/App.tsx`](file:///c:/Users/ivank/Desktop/Sideproject/seq-transit-reliability/web/src/App.tsx)

#### Q: 前端如何載入資料？加載中與錯誤狀態如何優雅處理？

* **載入機制與取消防護**：
  * 使用 React `useEffect` 監聽日期變更，並搭配原生 `AbortController`。若使用者快速切換日期，前一個尚未完成的 HTTP 請求會被主動 abort，防止 Race Condition（舊日期的回應晚到覆蓋新日期）。
* **狀態機器設計（Union Type）**：
  * 定義明確的狀態型別：
    ```typescript
    type Load =
      | { state: 'loading' }
      | { state: 'error'; message: string }
      | { state: 'ready'; report: DayReport };
    ```
  * 載入中顯示骨架標題；錯誤時提供「Try again」按鈕與明確指引；若當天無數據（可能剛過午夜尚未有批次載入），明確告知使用者批次載入的延遲特性，而非顯示空白。
* **開發者友善**：支援 `?sample` 參數，無需後端 API 亦能測試前端完整 UI 與版面配置。

---

## 面試直球對決 (Interview Defense Q&A)

當面試官翻看你的程式碼時，最容易問的五個高難度問題：

### 1. 為什麼不用 VPC？什麼情況下你會把它放回 VPC？
> **回答要點**：
> 「為了成本最佳化。Aurora Express 支援公網網關且強制要求 TLS 與短期 IAM Token 認證，沒有密碼外洩風險，讓我們完全免除了 NAT Gateway 每月 $32+ 美元的固定開支。但若企業有嚴格合規（如 PCI-DSS/HIPAA）要求所有資料庫流量不得經過公網，或是需要整合私有企業內部系統時，我會將其移回 VPC 並搭配 PrivateLink / NAT Gateway。」

### 2. 載入函式中途崩潰，下一次會發生什麼事？
> **回答要點**：
> 「因為寫入是單一資料庫交易，崩潰時會自動 Rollback，資料庫的 Watermark（`max(feed_timestamp)`）不會前移。下一次執行會重新處理這批快照。再加上我們的寫入邏輯有 `ObservationFold` 消除重複鍵，以及 `WHERE EXCLUDED.last_feed_at > t.last_feed_at` 條件防護，重新處理完全是冪等且無副作用的。」

### 3. 你怎麼確保準點率數字是客觀真實的？
> **回答要點**：
> 「主要有兩大原則：第一，只統計 `EventTime < now` 的站點，排除未發生的未來預測；第二，準點率必須與樣本數（Observed Stops）一併呈現，低於特定樣本數的結果不具統計意義。此外，準點窗口配置為 -60 秒至 +300 秒，這符合 Translink 官方發布的準點公認標準。」

### 4. 如果要擴展到全澳洲所有交通機關，架構要怎麼改？
> **回答要點**：
> 「目前架構是針對單一機構設計的。如果要擴充到全國多個機構：
> 1. S3 快照路徑擴展為 `agency_id/trip-updates/v1/...`。
> 2. 將 Fetch 與 Load 分離成 EventBridge Pipe 或 SQS 隊列，依 agency 併發處理。
> 3. 資料庫表格將 `agency_id` 納入複合主鍵，並在 PostgreSQL 上按 `agency_id` 與日期進行分區（Table Partitioning）。」

### 5. 這個架構目前每月成本是多少？如何再砍一半？
> **回答要點**：
> 「目前在低流量下每月成本約 $5~$15 美元，主要來自 Aurora Serverless v2 運行的 ACU 時數（當它被批次載入喚醒時）。S3、Lambda 與 CloudFront 幾乎在免費額度內。如果要進一步減半：
> 1. 將 Load 間隔從 30 分鐘拉長到 60 分鐘，讓 Aurora 休眠更久。
> 2. S3 原始快照加上 gzip 壓縮（可縮減 70% 儲存空間）。」

---

## 本地實戰操作檢查清單

你可以同時打開三個終端機，體驗整個系統的運作：

1. **終端機 1（後端 API）**：
   ```powershell
   dotnet run --project src/SeqTransit.Api
   # 測試端點: http://localhost:5080/api/health
   # 查詢今日路線: http://localhost:5080/api/routes/on-time
   ```

2. **終端機 2（前端 Dashboard）**：
   ```powershell
   cd web
   npm run dev
   # 瀏覽器打開: http://localhost:5173
   ```

3. **終端機 3（查看資料庫即時筆數）**：
   ```powershell
   docker compose exec db psql -U postgres -d seq_transit -c "SELECT count(*) FROM stop_delay_observation;"
   ```
