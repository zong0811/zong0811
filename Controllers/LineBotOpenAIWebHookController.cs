using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace isRock.Template
{
    public static class BotService
    {
        private static string GeminiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private static string GasUrl => Environment.GetEnvironmentVariable("GAS_WEB_APP_URL") ?? "";
        private static string GasApiKey => Environment.GetEnvironmentVariable("GAS_API_KEY") ?? "";

        public static async Task<string> GetGeminiResponseAsync(string userId, string userQuery)
{
    try
    {
        using var client = new HttpClient();
        // 建議在 Render 免費版維持使用 1.5-flash，速度與穩定度平衡最佳
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
        string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

        // 1. 讀取長期記憶 (GAS)
        string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
        var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

        // 2. 定義功能工具
        var tools = new object[] {
            new { function_declarations = new object[] { 
                new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                new { name = "calendar_list", description = "查詢接下來一週的行程" },
                new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { summary = new { type = "string" }, startTime = new { type = "string", description = "格式: yyyy-MM-ddTHH:mm:ss+08:00" }, endTime = new { type = "string", description = "格式: yyyy-MM-ddTHH:mm:ss+08:00" } }, required = new[] { "summary", "startTime", "endTime" } } },
                new { name = "gmail_send", description = "直接寄出電子郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                new { name = "add_lesson_note", description = "記錄教學發現、教案筆記。涉及資料歸檔必用。", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
            } }
        };

        // 🌟 強化後的 Prompt：加入「強制任務清點」邏輯
        string systemPrompt = $"你是一位具備 30 年資歷的專業教育人員，也是使用者的專屬助手。現在時間 {currentTimeInfo}。\n" +
                             "【工具使用限制】：優先用內建知識回答教育政策與技巧。僅在明確提到「搜尋雲端」時調用 drive_search。\n" +
                             "【核心指令】：當使用者要求多項任務（如整理後寄信並存檔），你必須調用『所有』對應工具，且在最終回覆中『逐一確認』每一項任務的完成狀況。\n" +
                             " **若為查詢行事曆**：請保持【極簡清爽】，直接列出行程時間與主題即可，末端給予一句簡短的行政溫馨提醒。\n" +
                             "【其他指令回覆結構如下（嚴格執行）】：\n" +
                             "   ### [專業知識標題]\n" +
                             "   1. **詳細整理內容**：針對需求，提供至少 5-8 個深度條列要點。這是回覆的主體，嚴禁簡略。\n" +
                             "   2. **多重任務執行清單**：在分隔線「---」後，必須條列檢查並確認所有動作。例如：\n" +
                             "      - ✅ 郵件已寄送至：[收件人]\n" +
                             "      - ✅ 教案已歸檔至試算表（標題：[標題]）\n" +
                             "   3. **專屬互動**：最後針對內容提出一個引發實務討論的追蹤問題。\n" +
                             "【語氣要求】：文字溫潤、專業且精準。善用 Markdown 排版。";


        // 4. 第一階段 AI 請求
        var firstBody = new {
            contents = new List<object>(historyList) { new { role = "user", parts = new[] { new { text = userQuery } } } },
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            generationConfig = new { maxOutputTokens = 2000, temperature = 0.7 },
            tools = tools
        };

        var firstRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(firstBody), Encoding.UTF8, "application/json"));
        dynamic? firstResult = JsonConvert.DeserializeObject(await firstRes.Content.ReadAsStringAsync());
        var parts = firstResult?.candidates?[0]?.content?.parts;

        if (parts == null) return "導師正在思考中...";

        List<object> modelParts = new List<object>();
        List<object> functionResponses = new List<object>();
        bool hasFunctionCall = false;
        string cachedContent = ""; 
        string cachedTitle = "";

        // 🌟 5. 並行任務處理 (關鍵修正：明確指定 Task<dynamic>)
        var gasTasks = new List<Task<dynamic>>();

        foreach (var p in parts) {
            if (p.functionCall != null) {
                hasFunctionCall = true;
                var currentP = p; 
                // 🌟 使用 Task.Run<dynamic> 解決 CS1503 錯誤
                gasTasks.Add(Task.Run<dynamic>(async () => {
                    string fName = (string)currentP.functionCall.name;
                    var fArgs = currentP.functionCall.args;
                    string tTitle = (fName == "add_lesson_note") ? (string)fArgs["title"] : "";
                    string tContent = (fName == "add_lesson_note") ? (string)fArgs["content"] : "";

                    object payload = (fName == "add_lesson_note") ? 
                        new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)fArgs["category"], (string)fArgs["title"], (string)fArgs["content"] } } :
                        new { action = fName, args = fArgs };

                    string gRes = await CallGasAsync(payload);
                    return new {
                        mp = new { functionCall = currentP.functionCall },
                        fr = new { role = "function", parts = new[] { new { functionResponse = new { name = fName, response = JsonConvert.DeserializeObject(gRes) ?? new { } } } } },
                        ct = tTitle, 
                        cc = tContent
                    };
                }));
            } else if (p.text != null) {
                modelParts.Add(new { text = (string)p.text });
            }
        }

        // 6. 整合結果並回覆
        if (hasFunctionCall) {
            var taskResults = await Task.WhenAll(gasTasks);
            foreach (var r in taskResults) {
                modelParts.Add(r.mp);
                functionResponses.Add(r.fr);
                if (!string.IsNullOrEmpty((string)r.cc)) { cachedTitle = r.ct; cachedContent = r.cc; }
            }

            var finalContents = new List<object>(historyList);
            finalContents.Add(new { role = "user", parts = new[] { new { text = userQuery } } });
            finalContents.Add(new { role = "model", parts = modelParts });
            finalContents.AddRange(functionResponses);
            finalContents.Add(new { role = "user", parts = new[] { new { text = "請依照格式詳細彙報剛才處理的內容。" } } });

            var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(new { 
                contents = finalContents, systemInstruction = new { parts = new[] { new { text = systemPrompt } } }, 
                generationConfig = new { maxOutputTokens = 2000 }, tools = tools 
            }), Encoding.UTF8, "application/json"));

            dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
            string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

            // 🌟 智慧型備援：如果 AI 斷片（空回覆），我們手動格式化資料顯示給老師
            return string.IsNullOrEmpty(aiText) ? FormatFallbackResponse(modelParts, functionResponses, cachedTitle, cachedContent) : aiText;
        }

        return (string?)parts[0]?.text ?? "導師正在準備中...";
    }
    catch (Exception ex) { return $"系統異常：{ex.Message}"; }

    // --- 內部輔助函數 (修正資料呈現邏輯) ---
    string FormatFallbackResponse(List<object> mps, List<object> frs, string ct, string cc) {
        StringBuilder sb = new StringBuilder();
        if (!string.IsNullOrEmpty(cc)) sb.AppendLine($"### {ct}\n{cc}\n\n---");
        sb.AppendLine("所有任務皆已並行處理完成。報告如下：");
        for (int i = 0; i < mps.Count; i++) {
            dynamic m = mps[i]; 
            if (m.functionCall != null) {
                string fName = (string)m.functionCall.name;
                var rawData = ((dynamic)frs[i]).parts[0].functionResponse.response;
                string json = JsonConvert.SerializeObject(rawData);

                if (fName == "calendar_list") {
                    var evts = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json);
                    sb.AppendLine("📅 **本週行程清單：**");
                    if (evts?.Count > 0) foreach(var e in evts) sb.AppendLine($"• {e["start"]} - {e["summary"]}");
                    else sb.AppendLine("（目前尚無行程）");
                }
                else if (fName == "drive_search") {
                    var fls = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json);
                    sb.AppendLine("📁 **雲端搜尋結果：**");
                    if (fls?.Count > 0) foreach(var f in fls) sb.AppendLine($"• [{f["name"]}]({f["url"]})");
                    else sb.AppendLine("（找不到檔案）");
                }
                else {
                    sb.AppendLine($"✅ 已執行動作：{fName}");
                }
            }
        }
        sb.AppendLine("\n對於這些資料，您還有需要我進一步分析的地方嗎？");
        return sb.ToString();
    }
}

        public static async Task<string> CallGasAsync(object payloadData)
        {
            try {
                using var client = new HttpClient();
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(payloadData)) ?? new Dictionary<string, object>();
                dict["apiKey"] = GasApiKey;
                var content = new StringContent(JsonConvert.SerializeObject(dict), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(GasUrl, content);
                return await res.Content.ReadAsStringAsync();
            } catch { return "{}"; }
        }
    }

// --- 4.5 自動喚醒服務 (Self-Ping) ---
    public static class SelfPingService
    {
        private static readonly HttpClient client = new HttpClient();
        public static void Start()
        {
            _ = Task.Run(async () =>
            {
                // 等待 5 秒讓系統完全啟動
                await Task.Delay(5000); 
                while (true)
                {
                    try
                    {
                        // 這是你的 Render 網址
                        var response = await client.GetAsync("https://zong0811.onrender.com/api/LineBotOpenAIWebHook");
                        Console.WriteLine($"[Self-Ping] Status: {response.StatusCode} at {DateTime.Now}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Self-Ping] Error: {ex.Message}");
                    }
                    await Task.Delay(TimeSpan.FromMinutes(10)); 
                }
            });
        }
    }

    
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("AI Google助手已經設定完成，加入自我喚醒功能");

        [HttpPost] [Route("api/LineBotOpenAIWebHook")]
        public async Task<IActionResult> POST()
        {
            try {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || lineEvent.type.ToLower() != "message" || lineEvent.message.type != "text") return Ok();

                string userId = lineEvent.source.userId;
                string userText = lineEvent.message.text;

                _ = Task.Run(async () => {
                    try {
                        using var client = new HttpClient();
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ChannelAccessToken);
                        await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                            new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 30 }), Encoding.UTF8, "application/json"));
                    } catch { }
                });

                var usageResJson = await BotService.CallGasAsync(new { action = "usage_increment", userId = userId });
                dynamic? usageRes = JsonConvert.DeserializeObject(usageResJson);
                int currentCount = usageRes?.count ?? 0;

                string aiResponse = await BotService.GetGeminiResponseAsync(userId, userText);

                _ = BotService.CallGasAsync(new { action = "sheets_append", userId = userId, userText = userText, aiResponse = aiResponse, count = currentCount });

                this.ReplyMessage(lineEvent.replyToken, $"{aiResponse}\n\n使用量：{currentCount}/500");
                return Ok();
            }
            catch { return Ok(); }
        }
    }
}
