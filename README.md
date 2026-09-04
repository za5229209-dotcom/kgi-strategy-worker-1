# kgi-strategy-worker-1

這支程式的工作:每個交易日開盤前由 GitHub Actions 自動啟動,連上凱基 QuoteCom 拿即時報價、每 10 秒查一次候選股票池,把符合門檻的標的寫進 DIDISTOCK 網站共用的 Upstash Redis,「策略」分頁的 5 個框最後會從這裡讀資料。

**目前狀態:第一版骨架,還沒有實際跑過**——凱基帳密/SourceId/Token 都還沒真的連線測試成功,篩選規則也只套用了「成交量 <500 張排除」這一條,其他規則等團隊討論定案後再補進 `QuoteWorker/Program.cs` 的 `ScreenCandidates()`。

## 動手之前要做的事

1. **把 QuoteCom 的 4 個 DLL 檔複製進來,一起上傳到 repo**——GitHub Actions 每次執行都是全新環境,repo 裡沒有的檔案它看不到,所以這 4 個檔案要真的提交進 git,不能只留在你自己電腦上。請從你電腦上 `QuoteComExample_TSEC\QuoteCom Example TSEC\bin\Release\` 資料夾,把這 4 個檔案複製到這個專案的 `QuoteWorker\lib\` 資料夾(這個資料夾目前是空的,要自己建立),然後跟其他檔案一起拖進 GitHub 網頁上傳:
   - `QuoteCom.dll`
   - `Package.dll`
   - `PushClient.dll`
   - `ICSharpCode.SharpZipLib.dll`

   （這幾個是凱基提供給你的用戶端元件,不是帳號機密——文件裡明講需要保密的只有 SourceId/Token 這兩組值,這兩個一律只放在 Secrets,不會出現在程式碼或 repo 裡。）

2. **在 GitHub 網站設定 Secrets**(repo 頁面 → Settings → Secrets and variables → Actions → New repository secret),需要這幾組:
   - `QUOTE_HOST`:QuoteCom 連線的網域/主機位址
   - `QUOTE_PORT`:連線 port(範例程式預設 8000,依凱基實際提供的為準)
   - `QUOTE_SOURCE_ID`、`QUOTE_TOKEN`:凱基交付元件時另外給的兩組值(不是登入帳密)
   - `KGI_LOGIN_ID`、`KGI_LOGIN_PWD`:登入用的帳號密碼
   - `UPSTASH_REDIS_REST_URL`、`UPSTASH_REDIS_REST_TOKEN`:跟 DIDISTOCK 網站後端共用同一組 Upstash Redis 的值(在 Vercel 專案的環境變數裡可以找到)

3. **先手動觸發一次測試**:repo 頁面 → Actions → 左邊選 "Poll KGI QuoteCom and publish candidates" → 右邊 "Run workflow" 按鈕,不用等排程時間到,馬上就能看執行紀錄跟錯誤訊息。

## 這是第一版,预期會需要來回修

因為完全没有在真的 Windows/.NET 環境裡編譯執行過,第一次跑大概率會遇到編譯錯誤或欄位對不上的狀況(尤其是 `PI30026` 那個類別的欄位名稱,是照文件猜的,不是照真的原始碼抄的)。跑出錯誤訊息直接複製貼給 Claude,照著訊息修就好,不用自己一個人卡住。

## 檔案說明

- `.github/workflows/poll-quotes.yml`:排程設定,平日台北時間 08:55 自動跑一次(也可以手動觸發)
- `QuoteWorker/Program.cs`:主程式——連線、登入、每 10 秒查報價、篩選、寫進 Redis
- `QuoteWorker/lib/`:放 QuoteCom 的 4 個 DLL(見上面步驟 1,要提交進 git,GitHub Actions 才拿得到)
