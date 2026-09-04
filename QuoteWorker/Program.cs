using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Intelligence;   // QuoteCom / COM_STATUS / RECOVER_STATUS 都在這個命名空間(來自範例程式 Form1.cs)
using Package;        // PackageBase / P001503 / PI31001 / PI30026 都在這個命名空間

namespace QuoteWorker
{
    // =====================================================================
    // 這支是「無視窗版」的凱基 QuoteCom 報價擷取程式,改寫自你資料夾裡
    // QuoteCom Example TSEC(WinForms 版)的 Form1.cs——事件掛法、Connect2Quote
    // 的呼叫方式、RetriveLastPriceStock 的用法都是照抄那支範例裡「行情登入」
    // 「最後價格」兩個按鈕實際呼叫的程式碼,不是憑空猜的。
    //
    // 老實講在把這支程式交給你之前,有兩件事我沒辦法在這個沙盒環境驗證:
    // 1) 這個沙盒沒有 Windows/.NET 環境,也沒有真的 QuoteCom.dll 可以連結,
    //    所以這份程式碼完全沒有實際編譯、執行過。第一次在 GitHub Actions
    //    (或你自己電腦)跑,很有可能會出現編譯錯誤或欄位名稱對不上的狀況,
    //    屬於正常情況,把錯誤訊息貼給我,我再照著修。
    // 2) PI30026(單一商品最高/低價,對應 RetriveLastPriceStock 查詢結果)
    //    這個類別底下的欄位名稱(StockNo/LastMatchPrice/...)是照著
    //    「QuoteCom 回報格式附件-證券.doc」文件裡寫的名稱去猜的,PI31001
    //    那個類別在範例原始碼裡有真的看到用法可以照抄,但 PI30026 沒有——
    //    如果編譯出來說找不到這些欄位,要用 Visual Studio 打開
    //    QuoteCom.dll 看一下 PI30026 實際的屬性名稱,再回來改這段。
    // =====================================================================
    class Program
    {
        // ---- 從 GitHub Actions 的 Secrets 帶進來的環境變數 ----
        static string QuoteHost = Environment.GetEnvironmentVariable("QUOTE_HOST");
        static ushort QuotePort = ushort.Parse(Environment.GetEnvironmentVariable("QUOTE_PORT") ?? "8000");
        static string SourceId = Environment.GetEnvironmentVariable("QUOTE_SOURCE_ID");
        static string Token = Environment.GetEnvironmentVariable("QUOTE_TOKEN");
        static string LoginId = Environment.GetEnvironmentVariable("KGI_LOGIN_ID");
        static string LoginPwd = Environment.GetEnvironmentVariable("KGI_LOGIN_PWD");
        static string UpstashUrl = Environment.GetEnvironmentVariable("UPSTASH_REDIS_REST_URL");
        static string UpstashToken = Environment.GetEnvironmentVariable("UPSTASH_REDIS_REST_TOKEN");

        // 先鎖定一個很小的測試名單(對應範例程式預設值 2330|2454),
        // 團隊還沒定案完整篩選規則跟股票池之前,不要一次掃全市場。
        static string[] Symbols = (Environment.GetEnvironmentVariable("QUOTE_SYMBOLS") ?? "2330|2454|2317|2603|3037")
            .Split('|');

        // 目前唯一確定的硬性門檻:當日累積成交量 < 500 張(=500,000 股)排除。
        // 其餘篩選規則(量能突破/均線/K棒型態...)等團隊討論定案後再加進 ScreenCandidates()。
        const long MIN_TOTAL_VOLUME_SHARES = 500 * 1000;

        static QuoteCom quoteCom;
        static HttpClient http = new HttpClient();
        static Dictionary<string, SymbolState> states = new Dictionary<string, SymbolState>();
        static bool loginOk = false;
        static DateTime startedAt = DateTime.Now;

        // 每檔股票目前累積的樣本(每 10 秒一筆),用來疊出簡易 K 線跟判斷是否符合篩選條件。
        class SymbolState
        {
            public string StockNo;
            public decimal LastPrice;
            public decimal DayHigh;
            public decimal DayLow;
            public long TotalVolumeShares;
            public DateTime LastUpdate;
            public List<decimal> RecentPrices = new List<decimal>(); // 供之後疊 K 線/判斷趨勢用
        }

        static void Main(string[] args)
        {
            if (string.IsNullOrEmpty(QuoteHost) || string.IsNullOrEmpty(SourceId) || string.IsNullOrEmpty(Token)
                || string.IsNullOrEmpty(LoginId) || string.IsNullOrEmpty(LoginPwd))
            {
                Console.WriteLine("[FATAL] 缺少必要的環境變數(QUOTE_HOST / QUOTE_SOURCE_ID / QUOTE_TOKEN / KGI_LOGIN_ID / KGI_LOGIN_PWD),請確認 GitHub Actions 的 Secrets 都有設定。");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine("[INFO] 啟動,候選股票池: " + string.Join(",", Symbols));

            quoteCom = new QuoteCom(QuoteHost, QuotePort, SourceId, Token);
            quoteCom.SourceId = SourceId; // 照抄範例程式 button1_Click 的呼叫順序(先設定屬性再連線)
            quoteCom.OnGetStatus += OnQuoteGetStatus;
            quoteCom.OnRcvMessage += OnQuoteRcvMessage;
            quoteCom.OnRecoverStatus += OnRecoverStatus;

            // Connect2Quote = 連線+登入一次做完,area 用空白(不指定),照抄範例程式的預設值。
            quoteCom.Connect2Quote(QuoteHost, QuotePort, LoginId, LoginPwd, ' ', "");

            // 等待登入結果,最多等 30 秒
            for (int i = 0; i < 30 && !loginOk; i++) Thread.Sleep(1000);
            if (!loginOk)
            {
                Console.WriteLine("[FATAL] 30 秒內沒有收到登入成功訊息,程式結束。請檢查帳密/SourceId/Token 是否正確。");
                Environment.Exit(1);
                return;
            }

            // 主迴圈:每 10 秒查一次這批股票的最新價格,最長跑 5 小時(涵蓋台股盤中時間),
            // 由 GitHub Actions 排程負責在開盤前啟動這支程式。
            DateTime deadline = DateTime.Now.AddHours(5);
            int tick = 0;
            while (DateTime.Now < deadline)
            {
                short status = quoteCom.RetriveLastPriceStock(string.Join("|", Symbols));
                if (status < 0)
                    Console.WriteLine("[WARN] RetriveLastPriceStock 回傳錯誤: " + quoteCom.GetSubQuoteMsg(status));

                tick++;
                // 每 6 次(約 1 分鐘)寫一次候選名單到 Redis,不用每 10 秒都寫,省成本也省 AI 呼叫次數。
                if (tick % 6 == 0)
                {
                    PublishCandidates();
                }

                Thread.Sleep(10000);
            }

            quoteCom.Logout();
            quoteCom.Dispose();
            Console.WriteLine("[INFO] 收工,程式結束。");
        }

        static void OnQuoteGetStatus(object sender, COM_STATUS staus, byte[] msg)
        {
            var enc = new UTF8Encoding();
            switch (staus)
            {
                case COM_STATUS.CONNECT_READY:
                    Console.WriteLine("[STATUS] 連線成功");
                    break;
                case COM_STATUS.CONNECT_FAIL:
                    Console.WriteLine("[STATUS] 連線失敗: " + enc.GetString(msg));
                    break;
                case COM_STATUS.LOGIN_READY:
                    Console.WriteLine("[STATUS] 登入成功: " + enc.GetString(msg));
                    break;
                case COM_STATUS.LOGIN_FAIL:
                    Console.WriteLine("[STATUS] 登入失敗: " + enc.GetString(msg));
                    break;
                case COM_STATUS.DISCONNECTED:
                    Console.WriteLine("[STATUS] 斷線: " + enc.GetString(msg));
                    break;
            }
            ((QuoteCom)sender).Processed();
        }

        static void OnRecoverStatus(object sender, string Topic, RECOVER_STATUS status, uint RecoverCount)
        {
            // 回補資料狀態,目前只印出來,不特別處理。
            Console.WriteLine("[RECOVER] Topic=" + Topic + " status=" + status + " count=" + RecoverCount);
        }

        static void OnQuoteRcvMessage(object sender, PackageBase package)
        {
            switch (package.DT)
            {
                case (ushort)DT.LOGIN:
                    P001503 login = (P001503)package;
                    if (login.Code == 0)
                    {
                        Console.WriteLine("[LOGIN] 成功,可註冊檔數(Qnum)= " + login.Qnum);
                        loginOk = true;
                    }
                    else
                    {
                        Console.WriteLine("[LOGIN] 失敗,Code=" + login.Code);
                    }
                    break;

                case (ushort)DT.QUOTE_LAST_PRICE_STOCK:
                    // ↓↓↓ 這段欄位名稱是照文件猜的,如果編譯錯誤請對照 QuoteCom.dll 實際定義修正 ↓↓↓
                    PI30026 last = (PI30026)package;
                    UpdateState(last.StockNo, last.LastMatchPrice, last.DayHighPrice, last.DayLowPrice, last.TotalMatchQty);
                    break;

                case (ushort)DT.QUOTE_STOCK_MATCH1:
                case (ushort)DT.QUOTE_STOCK_MATCH2:
                    // 如果之後改成用 SubQuotesMatch 訂閱逐筆成交,會走這裡(目前主迴圈用的是查詢式,不會觸發)。
                    PI31001 match = (PI31001)package;
                    UpdateState(match.StockNo, match.Match_Price, match.Match_Price, match.Match_Price, match.Total_Qty);
                    break;
            }
        }

        static void UpdateState(string stockNo, decimal price, decimal high, decimal low, long totalQty)
        {
            SymbolState st;
            if (!states.TryGetValue(stockNo, out st))
            {
                st = new SymbolState { StockNo = stockNo, DayHigh = price, DayLow = price };
                states[stockNo] = st;
            }
            st.LastPrice = price;
            st.DayHigh = Math.Max(st.DayHigh, high);
            st.DayLow = st.DayLow == 0 ? low : Math.Min(st.DayLow, low);
            st.TotalVolumeShares = totalQty;
            st.LastUpdate = DateTime.Now;
            st.RecentPrices.Add(price);
            if (st.RecentPrices.Count > 60) st.RecentPrices.RemoveAt(0); // 只留最近 60 筆(約 10 分鐘)
        }

        // =====================================================================
        // 篩選邏輯:目前只套用唯一確定的規則(成交量 < 500 張排除)。
        // 團隊討論出完整規則之後,只需要改這個函式,不用動上面抓報價/連線的部分。
        // =====================================================================
        static List<SymbolState> ScreenCandidates()
        {
            return states.Values
                .Where(s => s.TotalVolumeShares >= MIN_TOTAL_VOLUME_SHARES)
                .OrderByDescending(s => s.TotalVolumeShares)
                .Take(5) // 「策略」分頁目前有 5 個框
                .ToList();
        }

        static void PublishCandidates()
        {
            var candidates = ScreenCandidates();
            Console.WriteLine("[PUBLISH] 目前符合門檻的候選: " + candidates.Count + " 檔");

            if (string.IsNullOrEmpty(UpstashUrl) || string.IsNullOrEmpty(UpstashToken))
            {
                Console.WriteLine("[WARN] 沒有設定 UPSTASH_REDIS_REST_URL / UPSTASH_REDIS_REST_TOKEN,這次不寫入 Redis(只在這裡印出來)。");
                return;
            }

            var sb = new StringBuilder("[");
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"stockNo\":\"").Append(c.StockNo).Append("\",")
                  .Append("\"lastPrice\":").Append(c.LastPrice).Append(",")
                  .Append("\"dayHigh\":").Append(c.DayHigh).Append(",")
                  .Append("\"dayLow\":").Append(c.DayLow).Append(",")
                  .Append("\"totalVolumeShares\":").Append(c.TotalVolumeShares).Append(",")
                  .Append("\"updatedAt\":\"").Append(c.LastUpdate.ToString("o")).Append("\"}");
            }
            sb.Append("]");

            try
            {
                // 沿用專案既有的 upstashCommand 慣例:SET strategy:candidates <json>
                string url = UpstashUrl.TrimEnd('/') + "/set/strategy%3Acandidates/" + Uri.EscapeDataString(sb.ToString());
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", "Bearer " + UpstashToken);
                var resp = http.SendAsync(req).Result;
                Console.WriteLine("[PUBLISH] 寫入 Redis 狀態: " + (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR] 寫入 Redis 失敗: " + ex.Message);
            }
        }
    }
}
