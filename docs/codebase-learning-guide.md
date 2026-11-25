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
  // PassedStops：資料品質規則在 view 裡，這裡只加「已經通過」
  db.ReliableObservations.Where(o => o.EventTime < now)
  ```
  * 「有時間、有延誤、|延誤| ≤ 1 小時」這些資料品質規則統一寫在 `reliable_stop_observation` view（`db/migrations/0006`），LINQ 和原生 SQL 查詢都查這個 view，改規則只要新增一個 `CREATE OR REPLACE VIEW` 的 migration。
  * `event_time < now` 留在查詢裡，因為 `now` 是參數（測試會傳入固定時間）。
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

## Phase 3：靜態 GTFS 課表整合與高效匯入 (Static GTFS Integration)

### 1. 靜態 GTFS vs 實時 GTFS (GTFS-RT) 的職責邊界

| 特性 | 靜態 GTFS (Static Timetable) | 實時 GTFS (GTFS-RT / TripUpdates) |
| :--- | :--- | :--- |
| **提供者格式** | 包含多個 CSV 文件的 ZIP 壓縮檔 (`SEQ_GTFS.zip`) | Google Protocol Buffers (二進制 Protobuf) |
| **更新頻率** | 每週 ~ 每月（排班班表異動時更新一次） | 每 15 ~ 60 秒持續推送即時狀態 |
| **包含資訊** | 路線名稱、車次課表、車站站名、經緯度、營運工具類型 | 即時延誤秒數、目前車輛 GPS、站點預估時間、取消狀態 |
| **設計哲學** | **靜態描述「預期計劃」**：只有靜態表才能查到「路線代碼 `100-5146` 代表 `100 號公車 (Forest Lake - City)`」。 | **實時描述「動態現實」**：只攜帶 ID 與即時位移，不攜帶冗長名稱以節省蜂巢網路頻寬。 |

---

### 2. Npgsql Binary COPY (`BeginBinaryImportAsync`) 高性能匯入

* **為什麼不用 ORM (EF Core `AddRange`) 或 SQL `INSERT`？**
  * `SEQ_GTFS.zip` 內包含超過 **13.6 萬筆 Trips**、**1.3 萬個 Stops** 與 **1,200 多條 Routes**。
  * 若使用一般的 SQL `INSERT`，即便每次批量傳入 1,000 筆，解析 SQL 文字與資料型別轉換依然耗費數十秒甚至幾分鐘。
  * **Npgsql Binary COPY** 直接透過 PostgreSQL 原生的串流二進制協定（Streaming Binary Protocol）將 C# 物件寫入資料庫：
    ```csharp
    await using var writer = await connection.BeginBinaryImportAsync(
        "COPY stage_route (...) FROM STDIN (FORMAT BINARY)", cancellationToken);
    // 直接寫入二進制資料流，繞過任何 SQL 解析器
    await writer.WriteAsync(route.RouteId, NpgsqlDbType.Text);
    ```
  * **成果**：在本地環境下，**15 萬筆靜態記錄在 5 秒內全數匯入完成**！

---

### 3. 單一交易原子換表（Atomic Table Swap）保證零讀取中斷

* **常見新手陷阱**：在正式運行的資料庫上直接執行 `TRUNCATE` 或 `DELETE`，再逐批寫入新課表。這會導致在匯入的數秒至數分鐘內，前端或外部 API 查詢時看到「空表」或「只有一半的路線名」。
* **架構解決方案（Staging + Transaction Atomic Swap）**：
  1. 在交易內建立暫存表：`CREATE TEMP TABLE stage_route ... ON COMMIT DROP`。
  2. 使用 Binary COPY 將完整的新課表先灌入 `stage_*` 暫存表。
  3. 在**同一個資料庫交易**中執行：
     ```sql
     TRUNCATE gtfs_route, gtfs_stop, gtfs_trip;
     INSERT INTO gtfs_route SELECT * FROM stage_route;
     INSERT INTO gtfs_stop SELECT * FROM stage_stop;
     INSERT INTO gtfs_trip SELECT * FROM stage_trip;
     ```
  4. 提交交易：`transaction.CommitAsync()`。
* **為什麼 API 不會看到半載入的課表？**
  * 所有寫入都在同一個交易裡，提交前其他連線看不到任何新資料。
  * 注意：`TRUNCATE` 不是一般的 MVCC 刪除，它會拿 **ACCESS EXCLUSIVE 鎖**。交易進行中，API 對 `gtfs_*` 的查詢會**排隊等待**，而不是讀到舊資料；提交瞬間鎖釋放，查詢直接看到完整的新課表。
  * 因為 COPY 進暫存表的耗時步驟發生在 `TRUNCATE` 之前，鎖只持有「換表」那一小段（幾百毫秒），所以實務上只是一次短暫等待，**API 永遠不會看到空表或半載入的資料**。
  * 如果要做到完全不擋讀取，可以改用「兩張表 + `ALTER TABLE ... RENAME`」或 `DELETE` + `INSERT`（代價是表膨脹，需要 VACUUM）。

---

### 4. LEFT JOIN 容錯設計與運具分類 (Route Types)

* **容錯原則**：
  * 當即時資料庫計算準點率時，我們使用 `LEFT JOIN` 關聯 `gtfs_route`：
    ```csharp
    var staticRoute = staticRoutes.GetValueOrDefault(g.RouteId);
    var shortName = staticRoute?.RouteShortName ?? g.RouteId;
    ```
  * 如果 Translink 臨時開闢了不在靜態班表中的緊急臨時班次（如臨時加班接駁公車），系統**絕不崩潰**，會優雅地退回顯示其原始代碼。
* **GTFS 國際標準運具代碼**：
  * `0`: Tram / Light Rail（輕軌，如黃金海岸 G:link）
  * `1`: Subway / Metro（地下鐵 / 地鐵）
  * `2`: Rail（重鐵 / 火車，如昆士蘭鐵路 Queensland Rail）
  * `3`: Bus（公車 / 巴士）
  * `4`: Ferry（渡輪，如布里斯本 CityCat）

---

## Phase 4：高階分析端點與真實指標工程 (Advanced Analysis & Metrics Engineering)

在 Phase 4 中，系統從「單純記錄延誤」升級為「能回答真實通勤者與交通主管機關決策問題」的分析引擎。我們新增了四組高階分析端點，並導入統計學上的真實指標原則與高效能 SQL 聚合。

### 1. 為什麼不能只看平均值？引入百分位數 (Median & P90)
* **平均值的盲點（The Flaw of Averages）**：
  * 在大眾運輸領域，延誤時間呈現高度偏態分佈（Right-skewed Distribution）。大部分班次正常運轉（延誤 0~60 秒），但少數嚴重事故（例如電車出軌、號誌故障）可能產生長達 45 分鐘（2700 秒）的極端延誤。
  * 若只看平均延誤（Average Delay），單一極端值會大幅拉高整條路線的數據，無法反映 90% 以上普通乘客的日常通勤感受。
* **百分位數工程 (Percentile Metrics)**：
  * **中位數 (P50 / Median Delay)**：代表 50% 乘客的日常典型延誤水準。
  * **P90 延誤 (90th Percentile Delay)**：代表「在最糟糕的 10% 情況下，通勤者需要預留多少緩衝時間才不會遲到」。
* **資料庫端高效運算（In-Database Aggregation via `percentile_cont`）**：
  * **常見新手錯誤**：把一週幾萬筆資料全部撈回 C# 應用程式記憶體中，再用 LINQ 排序算百分位數（造成龐大網路頻寬消耗與記憶體 GC 壓力）。
  * **架構最佳實踐**：利用 PostgreSQL 原生高階統計聚合函式 `percentile_cont(0.5) WITHIN GROUP (ORDER BY delay)`，在資料庫引擎層直接算出結果，只把一列彙總傳回應用程式。實際耗時請用 `EXPLAIN ANALYZE` 在收滿一週資料後量測，並把數字記在 `docs/decisions.md`。

---

### 2. 尖峰時段分析與時區精準聚合 (Time Zone Aware Hourly Aggregations)
* **問題背景**：
  * GTFS-RT 即時資料庫中的 `event_time` 都是以 UTC 時間戳記儲存（例如 `2026-10-10 22:30:00+00`）。
  * 若在資料庫直接執行 `extract(hour FROM event_time)`，提取出來的是格林威治時間（夜間 22 點），完全無法對齊昆士蘭當地民眾早晚通勤尖峰！
* **昆士蘭時區特性**：
  * 昆士蘭（布里斯本）全年不實施日光節約時間（AEST，固定為 UTC+10）。
* **SQL 時區轉換處理**：
  * 在 SQL 查詢中，使用 `extract(hour FROM event_time AT TIME ZONE 'Australia/Brisbane')::int`。
  * 這樣能將 UTC 時間在資料庫端精準對齊為昆士蘭當地時間（07:00~09:00 上班尖峰、16:00~18:00 下班尖峰），使小時分析圖表真實呈現尖峰塞車趨勢。

---

### 3. 統計誠實原則：樣本數保護 (Sample Size Honesty)
* **面試核心理念**：
  * 「如果一條路線今天只被觀測到 2 個站點，其中 1 個準點，算出準點率 50%，這有意義嗎？沒有，這只是樣本不足的隨機噪聲。」
* **架構落實**：
  1. **所有比率必帶樣本數**：API 返回的每個統計物件（`RouteSummaryResponse`, `WorstStopPerformance`, `ModeReliability`）都同時返回 `ObservedStops` 與 `OnTimeStops`。
  2. **樣本數下限保護門檻（`minObservations`）**：在最差站點分析（`GET /api/stops/worst`）中，透過 SQL 的 `HAVING count(*) >= minObservations`（預設門檻 20 筆）將低頻次站點過濾掉，防止樣本不足的站點因為單次異常而干擾真實排名。

---

### 4. EF Core 10 參數化原生 SQL (Parameterized SQL with FormattableString)
* **為什麼使用 `Database.SqlQuery<T>`？**
  * EF Core 的 LINQ 雖然直觀，但對於 PostgreSQL 的特定進階函式（如 `percentile_cont(...) WITHIN GROUP (...)` 或 `AT TIME ZONE`）沒有開箱即用的 LINQ 轉譯支援。
  * EF Core 10 提供了 `Database.SqlQuery<T>($"...")`，能將查詢結果自動映射到強型別類別。
* **安全防護（Anti-SQL Injection）**：
  * C# 的字串插值（Interpolated String）在傳入 `SqlQuery` 時會被編譯為 `FormattableString`。
  * EF Core 會自動將裡面的變數（例如 `{routeId}`, `{serviceDate}`）轉為參數化參數（`@p0`, `@p1`），**絕非字串串接**，百分之百防止 SQL Injection，同時允許 PostgreSQL 複用查詢計畫快取。

---

### 5. 雲端原生整合測試 (Integration Testing with WebApplicationFactory & Testcontainers)
* **現代測試架構**：
  * `SeqTransit.Api.Tests`：採用 `Microsoft.AspNetCore.Mvc.Testing` 中的 `WebApplicationFactory<Program>`，在記憶體中啟動真實的 ASP.NET Core Minimal API 伺服器與 HTTP 管道。
  * 透過 Testcontainers 動態啟動真實 PostgreSQL 17 容器執行真實 SQL。
  * 測試包含：
    1. **OpenAPI 3.0 自動文件驗證**：驗證 `GET /openapi/v1.json` 是否正確產生，且包含新註冊的端點定義。
    2. **狀態碼與 JSON Shape 斷言**：驗證 200 OK、404 NotFound，以及各欄位的精確型別與統計數值。

---

## Phase 5：前端儀表板與視覺化無障礙工程 (Frontend Dashboard, Route Analytics & Accessible Data Visualisation)

在 Phase 5 中，我們將通勤者體驗提升到最高水準——任何人只要兩次點擊，就能一眼查出任何路線在尖峰時段的塞車瓶頸。

### 1. 單頁應用程式路由與雲端部署對齊 (SPA Routing & CloudFront Error Responses)
* **面試常問情境題**：
  * 「為什麼在本地端用 Vite 瀏覽 `/routes/66-1` 很正常，但一布署到 S3 + CloudFront，使用者手動重新整理網頁時卻跳出 404 Not Found 或 403 Forbidden？」
* **技術原理**：
  * S3 是靜態物件儲存桶（Object Store），不是傳統的 Web Server（如 Nginx 或 Apache），它裡面根本沒有一個叫做 `/routes/66-1` 的真實物理檔案。
  * 當使用者直接在網址列輸入深層連結（Deep Link）時，S3 找不到該物件，預設會回傳 404（或若無 List 權限時回傳 403）。
* **AWS 雲端架構解法（CloudFront Function 改寫路徑）**：
  * 常見做法是在 distribution 加 `errorResponses`（404/403 → 200 `/index.html`），但這個設定是**整個 distribution 共用**的，`/api/*` 的 404 也會被改成 200 + HTML，前端就無法分辨「找不到路線」和「連不上 API」。
  * 所以我們改在預設（S3）behavior 掛一個 viewer-request 的 **CloudFront Function**：路徑最後一段沒有副檔名（例如 `/routes/66-1`）就改寫成 `/index.html`；`/assets/index.js` 這類靜態檔照常回傳。`/api/*` 走另一個 behavior，完全不受影響：
    ```javascript
    function handler(event) {
      var request = event.request;
      var lastSegment = request.uri.split('/').pop();
      if (lastSegment.indexOf('.') === -1) {
        request.uri = '/index.html';
      }
      return request;
    }
    ```
  * 瀏覽器拿到 `index.html` 後，由 **React Router** 解讀網址並渲染出正確的 `<RoutePage />`。`infra/test` 有一條斷言確保 distribution 沒有 `CustomErrorResponses`。

---

### 2. 零依賴高對比原生 SVG 圖表架構 (Zero-Dependency Accessible SVG Charts)
* **為什麼不用大型圖表套件（如 Recharts / Chart.js）？**
  1. **輕量化與效能**：第三方圖表套件通常體積龐大（動輒 300KB+），且在 React 19 發布初期經常有 Peer Dependencies 版本相容性問題。自己撰寫 SVG 僅需不到 5KB，沒有任何外部依賴。
  2. **設計系統完全契合**：圖表直接繼承 CSS 自訂屬性（`--jacaranda`, `--good`, `--fair`, `--poor`, `--surface`, `--ink`），不管是預設淺色或系統切換至暗黑模式（Dark Mode），顏色與對比度皆自動無縫適應。
  3. **響應式 ViewBox**：透過 SVG `viewBox="0 0 640 220"` 向量縮放，無論在手機、平板還是 4K 螢幕上，線條與文字皆清晰不失真。

---

### 3. 真正具備同理心的無障礙圖表工程 (Screen Reader Accessibility)
* **無障礙法規與原則（WCAG 2.1 AA）**：
  * 視覺障礙通勤者使用螢幕閱讀器（Screen Readers，如 NVDA, JAWS, VoiceOver）時無法「看」折線圖或長條圖。
* **雙層渲染設計（Dual-Layer Rendering）**：
  1. **圖形層**：SVG 本身設定 `role="img"` 與直觀的 `aria-label`。
  2. **語意層（文字替代方案）**：在 SVG 下方嵌入具有 `.visually-hidden` 樣式的語意化 HTML `<table>`，把過去 7 天的每日準點率與 24 小時各時段的延誤秒數轉化為表格文字。
  * 視力正常的使用者看到美觀的曲線與長條圖；視障使用者透過語音朗讀能按鍵導航、清晰讀出每個點的確切數據。

---

### 4. 數據缺失時的優雅降級 (Handling Missing Data & Graceful Degradation)
* **真實世界交通數據的挑戰**：
  * 昆士蘭許多通勤快線（Express Routes）**只在週一至週五營運**，週末完全無班次。
  * 如果使用者在週日打開這條路線，後端回傳的當日站點與小時數會是空的。
* **前端彈性應對**：
  * 系統不會崩潰，也不會顯示錯誤頁面。
  * 過去 7 天的統計總覽卡片（KPI Cards）與 7 天折線圖依然能呈現歷史運行表現。
  * 當日小時長條圖與站點列表則顯示溫和的提示訊息（例如「No observations recorded on this date」），並引導使用者切換服務日期。

---

### 5. URL 狀態同步原則 (Search Params as Single Source of Truth)
* **分享即所得（Shareable URL State）**：
  * 日期選擇器不只存在於 React 組件的本地 state，而是即時同步至 URL query 參數（`?date=2026-10-12`）。
  * 點擊路線進入詳情頁面時，會將當前的查詢日期與展示狀態（`?sample`）傳遞至 `/routes/:routeId?date=...`。
  * 使用者複製瀏覽器網址傳給主管、同事或面試官時，對方點開網址能 100% 重現一模一樣的視圖與篩選條件。

---

## Phase 6：AWS 無伺服器雲端部署與實戰驗證 (AWS Serverless Deployment & Live Operations)

在 Phase 6 中，我們將整套系統正式部署至 AWS 雪梨節點（`ap-southeast-2`），從本機 Docker 邁向具備公開網址、零密碼 IAM 認證與自動休眠成本控管的生產級無伺服器架構。

### 1. 多帳號組織與新版 AWS CLI 認證 (`aws login` + Express Mode)
* **AWS 多帳號隔離最佳實踐**：
  * 系統部署在專屬的工作負載帳號（`Moonshot Mode` / `183255892601`），而非組織管理帳號（Management Account）或身分管理帳號（Identity Delegated Admin），確保權限與帳單邊界清晰。
* **無永久金鑰設計**：
  * 使用 AWS CLI v2.42 的 `aws login --remote` 直接透過瀏覽器 Console 工作階段簽發短期動態憑證，本機完全不存放任何永久性的 `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY`。
  * 當 .NET SDK (`SeqTransit.Migrator` / `SeqTransit.Local`) 需要使用這些短期憑證產生 RDS IAM Token 時，透過 `aws configure export-credentials --format powershell` 僅將短期 Token 暫時載入單次終端機記憶體中，不寫入任何設定檔。

---

### 2. Aurora PostgreSQL Express 與自動休眠成本架構
* **為什麼選 Aurora Express Configuration？**
  * 透過 `scripts/create-database.ps1` 建立的 Aurora PostgreSQL 17.9 叢集啟用了 `VPCNetworkingEnabled: false` 與 `InternetAccessGatewayEnabled: true`。
  * 外部連線強制走 TLS 加密（`SslMode=VerifyFull`）加上每 15 分鐘過期的 RDS IAM Auth Token，免去了建立 VPC 與 NAT Gateway（每月固定至少 USD 32+）的費用。
* **Scale-to-Zero 自動暫停**：
  * 設定 `MinCapacity=0, MaxCapacity=2, SecondsUntilAutoPause=300`：在 30 分鐘一次的 Load 批次之間，若無查詢流量，資料庫會在閒置 5 分鐘後自動縮容至 **0 ACU** 休眠，大幅壓低每月的運算費用。
* **別忘了靜態班表！**
  * 除了執行 `SeqTransit.Migrator` 套用全部 6 個 Migration 並建立最小權限角色（`seq_ingest` / `seq_api`）之外，也必須用 `load-gtfs` 把 15 萬筆 `SEQ_GTFS.zip` 靜態路線與站點資料匯入 Aurora，線上的儀表板才會顯示真實路線編號與名稱。

---

### 3. CloudFront 邊緣路由與 API 安全防線
* **同一網域雙路由**：
  * `/*` → 指向私有 S3 靜態網站（透過 Origin Access Control 存取，S3 封鎖全部公開存取），並以 `SpaRewriteFunction` 將前端路由（如 `/routes/196-5146`）改寫回 `/index.html`。
  * `/api/*` → 指向 API Gateway HTTP API（設定限流 10 req/s、burst 20）與 `ApiFunction`（.NET 10 ARM64 Graviton Lambda），且不會將 API 的真實 `404` 誤改寫成 HTML 頁面。
* **四重自動化可觀測性與成本防護**：
  * 4 個 CloudWatch Alarms（`NoSnapshotsAlarm`、`FeedStaleAlarm`、`LoadErrorsAlarm`、`ApiErrorsAlarm`）全數綁定 SNS Email 告警。
  * 搭配 AWS Budgets 每月預算通知，確保任何異常流量或管線中斷都能第一時間收到警示。

---

## Phase 7：GitHub Actions 零金鑰自動部署 (OIDC Federation & CI/CD)

在 Phase 7 中，我們導入了現代雲端安全標準的 **GitHub OIDC（OpenID Connect）聯邦認證**，讓每次合併至 `main` 分支時，GitHub Actions 自動執行測試、資料庫遷移與 CDK 部署，**全程不在 GitHub Secrets 裡存放任何永久 AWS Access Key**。

### 1. GitHub OIDC Token 如何換取臨時 AWS 憑證？
* **傳統作法的風險**：建立 IAM User、產生一組永久有效的 `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` 塞進 GitHub Secrets。一旦外洩或忘記輪替，攻擊者就擁有長期權限。
* **OIDC 零金鑰運作流程**：
  1. 在 AWS 端透過 `SeqTransitGitHubStack` 建立 GitHub 的 OIDC Identity Provider（`token.actions.githubusercontent.com`）與專屬的部署角色 `DeployRole`。
  2. 當 GitHub Actions 的 `deploy` Job 啟動時（需宣告 `permissions: id-token: write`），GitHub 會即時簽發一張有時效性的 JWT（OIDC Token），內含 `aud`（`sts.amazonaws.com`）與 `sub`（`repo:Ivan53040/seq-transit-reliability:ref:refs/heads/main`）。
  3. `aws-actions/configure-aws-credentials@v4` 拿這張 JWT 呼叫 AWS STS 的 `AssumeRoleWithWebIdentity`。
  4. AWS 驗證簽章與 Trust Policy 條件完全吻合後，發放一張**僅存活 1 小時的短期臨時憑證**供該次部署使用。

### 2. 為什麼 Trust Policy 嚴格鎖定單一 Repo 的 `main` 分支？
* 在 `infra/lib/github-deploy-stack.ts` 中，Trust Policy 同時鎖定：
  * `token.actions.githubusercontent.com:aud == "sts.amazonaws.com"`
  * `token.actions.githubusercontent.com:sub` 同時支援傳統格式（`repo:Ivan53040/seq-transit-reliability:ref:refs/heads/main`）與 GitHub 新版防倉庫復活攻擊的不可變 ID 格式（`repo:Ivan53040@*/seq-transit-reliability@*:ref:refs/heads/main`，我們是直接查 AWS CloudTrail 的 `AssumeRoleWithWebIdentity` 事件抓出這項真實差異並修正的！）
* **安全意義**：就算是同一個 Repo 的 Pull Request（包括外部人員開的 Fork PR）或其他開發分支，都**無法**假冒身分 Assume 部署角色；唯有通過 CI 檢查並正式合併進 `main` 分支的程式碼，才能取得部署權限。

### 3. 環境變數與成本設定防回歸
* 因為 `infra/cdk.context.json` 在 `.gitignore` 中不會進版控，CI/CD 部署時所有環境參數（`SEQ_DB_HOST`、`SEQ_DB_CLUSTER_RESOURCE_ID`、`SEQ_ALERT_EMAIL`、`SEQ_MONTHLY_BUDGET_USD`、`SEQ_LOAD_INTERVAL_MINUTES`）皆透過 GitHub Repository **Variables** 注入，確保每次自動部署都精準保留 `60` 分鐘批次載入與 `$20` 預算設定，不會意外退回預設值。

### 4. 每週自動同步靜態 GTFS 班表 (`.github/workflows/sync-gtfs.yml`)
* **實戰踩坑**：Translink 會定期更新 `SEQ_GTFS.zip` 的路線 ID 版本後綴（例如 10 月的 `196-5146` 換成 11 月的 `252-11`）。若沒有跟著更新 Aurora 中的靜態班表，`LEFT JOIN` 查無資料會讓線上儀表板整排顯示 `UNKNOWN`。
* **零維護解法**：複用既有的 OIDC 部署角色（無需額外金鑰或 IAM 權限），排程於每週一凌晨 3 點（布里斯本時間）自動下載最新版 `SEQ_GTFS.zip` 並以單一交易原子換表匯入 Aurora，徹底杜絕班表過期問題。

---

## Phase 8：Amazon Bedrock 每週 AI 摘要與數字幻覺防護 (AI Guardrail Engineering)

在 Phase 8 中，我們整合了 **Amazon Bedrock Converse API**（使用雪梨區 `ap-southeast-2` 原生、超低成本的 `amazon.nova-micro-v1:0`），每週一早上 7 點（布里斯本時間）自動撰寫一份給通勤者閱讀的簡明週報，並加上面試含金量最高的**確定性數字防護網（Deterministic Numeric Guardrail）**。

### 1. 為什麼模型「永遠不做算術」？(SQL Computes, LLM Narrates)
* **LLM 的本質弱點**：大型語言模型是機率字詞預測器，做加總、百分比或跨週差值計算時非常容易出現微小算術錯誤或幻覺（Hallucination）。
* **職責分離架構**：
  1. **PostgreSQL SQL 負責 100% 的數學計算**（`WeeklySummaryStore.ComputeWeeklyFiguresAsync`）：從 `reliable_stop_observation` View 算出整週準點率、上週對比差值（percentage points）、最佳／最差路線與最塞車時段，輸出為強型別 JSON（`WeeklyFigures`）。
  2. **Bedrock 模型只負責「看數字寫人話」**：透過 System Instruction 嚴格規定只能引用 JSON 中出現的數字，寫出 2–3 句簡明英文摘要。

### 2. 如何抓出模型捏造的數字？(Numeric Guardrail)
* 在 `src/SeqTransit.Core/Summaries/SummaryGuardrail.cs` 中，我們在寫入資料庫前對模型回覆執行確定性驗證：
  * 用正則表達式抽出回覆中出現的**每一個數字**（含千分位逗號 `46,109`、小數 `84.2`、負數 `-1.8`、12/24 小時制時間）。
  * 與 `WeeklyFigures` 白名單逐一比對。
  * **只要有一個數字不在白名單中**（例如模型自己編了 `91.7%` 或自己做減法算出新數字），系統會直接**拒絕寫入**，保留上一週的有效摘要，並在 CloudWatch 記錄 `WEEKLY_SUMMARY_REJECTED` 告警。
* `tests/SeqTransit.Core.Tests/SummaryGuardrailTests.cs` 透過單元測試證明合法回覆可通過、捏造數字必然被攔截。

### 3. 最小權限與前端透明度 (Least Privilege & AI Transparency)
* **IAM 單一模型授權**：`SummaryFunction` 的 IAM 政策僅授予 `bedrock:InvokeModel` 於**單一指定模型 ARN**（`arn:aws:bedrock:ap-southeast-2::foundation-model/amazon.nova-micro-v1:0`），絕不使用 `Resource: "*"`。
* **EventBridge 時區排程**：使用 `cron(0 7 ? * MON *)` 搭配 `TimeZone.AUSTRALIA_BRISBANE`，確保每週一早上 7 點準時執行。
* **透明標示**：前端看板（`GET /api/summaries/latest`）明確標註 *「Written by AI (`amazon.nova-micro-v1:0`) from SQL figures shown」*，並在摘要下方直接並列顯示 SQL 原始數字供讀者自行核對。

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
