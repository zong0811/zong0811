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
    try {
        using var client = new HttpClient();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
        string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

        string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
        var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

        var tools = new object[] {
            new { function_declarations = new object[] { 
                new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                new { name = "calendar_list", description = "查詢接下來一週的行程" },
                new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { summary = new { type = "string" }, startTime = new { type = "string", description = "格式: yyyy-MM-ddTHH:mm:ss+08:00" }, endTime = new { type = "string", description = "格式: yyyy-MM-ddTHH:mm:ss+08:00" } }, required = new[] { "summary", "startTime", "endTime" } } },
                new { name = "gmail_send", description = "直接寄出電子郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                new { name = "add_lesson_note", description = "記錄教學發現、教案筆記或整理好的政策/新聞內容。只要涉及資料歸檔必用。", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
            } }
        };

        string systemPrompt = $"你是一位專業教育人員，也是宗志的專屬助手。現在時間 {currentTimeInfo}。\n" +
                             "【核心指令】：當要求整理並紀錄時，必須先調用工具。最終回覆必須包含詳細內容摘要。\n" +
                             "【回覆結構】：1. 詳細整理內容(5-8點) -> 2. 分隔線後報告執行狀況 -> 3. 稱呼宗志並提問互動。";

        var requestBody = new {
            contents = new List<object>(historyList) { new { role = "user", parts = new[] { new { text = userQuery } } } },
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            generationConfig = new { maxOutputTokens = 2000, temperature = 0.7 },
            tools = tools
        };

        var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
        dynamic? result = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
        var parts = result?.candidates?[0]?.content?.parts;

        if (parts == null) return "導師正在思考中...";

        List<object> modelParts = new List<object>();
        List<object> functionResponses = new List<object>();
        bool hasFunctionCall = false;
        
        // 用來存放 AI 剛才產出的內容，以防斷片
        string cachedContent = ""; 
        string cachedTitle = "";

        foreach (var p in parts) {
            if (p.functionCall != null) {
                hasFunctionCall = true;
                string funcName = (string)p.functionCall.name;
                var args = p.functionCall.args;
                
                // 🌟 緩存 AI 產出的內容，以備 Fallback 使用
                if (funcName == "add_lesson_note") {
                    cachedTitle = (string)args["title"];
                    cachedContent = (string)args["content"];
                }

                object gasPayload = (funcName == "add_lesson_note") ? 
                    new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)args["category"], (string)args["title"], (string)args["content"] } } :
                    new { action = funcName, args = args };

                string gasRes = await CallGasAsync(gasPayload);
                modelParts.Add(new { functionCall = p.functionCall });
                functionResponses.Add(new { role = "function", parts = new[] { new { functionResponse = new { name = funcName, response = JsonConvert.DeserializeObject(gasRes) ?? new { } } } } });
            } else if (p.text != null) {
                modelParts.Add(new { text = (string)p.text });
            }
        }

        if (hasFunctionCall) {
            var finalContents = new List<object>(historyList);
            finalContents.Add(new { role = "user", parts = new[] { new { text = userQuery } } });
            finalContents.Add(new { role = "model", parts = modelParts });
            finalContents.AddRange(functionResponses);
            
            // 🌟 關鍵優化：在對話鏈最後加入一個「催促」指令，強迫 Flash 模型進行總結
            finalContents.Add(new { role = "user", parts = new[] { new { text = "請依照格式詳細彙報剛才處理的內容。" } } });

            var finalBody = new {
                contents = finalContents,
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                generationConfig = new { maxOutputTokens = 2000, temperature = 0.7 },
                tools = tools
            };

            var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
            dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
            string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

            // 🌟 智慧型備援：如果 AI 真的不說話，我們自己根據緩存的內容來排版
            if (string.IsNullOrEmpty(aiText)) {
                StringBuilder sb = new StringBuilder();
                if (!string.IsNullOrEmpty(cachedContent)) {
                    sb.AppendLine($"### {cachedTitle}");
                    sb.AppendLine(cachedContent); // 顯示 AI 剛剛存進去的詳細內容
                    sb.AppendLine("\n---");
                }
                sb.AppendLine("宗志老師，所有任務皆已處理完成。報告如下：");
                foreach(dynamic mp in modelParts) {
                    sb.AppendLine($"✅ 已執行動作：{mp.functionCall.name}");
                }
                sb.AppendLine("\n以上內容已存檔。對於這些資料，您還有需要我進一步分析的地方嗎？");
                return sb.ToString();
            }
            return aiText;
        }

        return (string?)parts[0]?.text ?? "導師正在準備中...";
    }
    catch (Exception ex) { return $"系統異常：{ex.Message}"; }
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

    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("AI Google助手已經設定完成");

        [HttpPost] [Route("api/LineBotOpenAIWebHook")]
        public async Task<IActionResult> POST()
        {
            try {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || lineEvent.type.ToLower() != "message" || lineEvent.message.type != "text") return Ok();

                string userId = lineEvent.source.userId;
                string userText = lineEvent.message.text;

                // 🌟 優化點 1：【第一優先】秒跳動畫
                _ = Task.Run(async () => {
                    try {
                        using var client = new HttpClient();
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ChannelAccessToken);
                        await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                            new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 30 }), Encoding.UTF8, "application/json"));
                    } catch { }
                });

                // 2. 檢查配額
                var usageResJson = await BotService.CallGasAsync(new { action = "usage_increment", userId = userId });
                dynamic? usageRes = JsonConvert.DeserializeObject(usageResJson);
                int currentCount = usageRes?.count ?? 0;

                // 3. 取得 AI 回覆
                string aiResponse = await BotService.GetGeminiResponseAsync(userId, userText);

                // 4. 異步紀錄
                _ = BotService.CallGasAsync(new { action = "sheets_append", userId = userId, userText = userText, aiResponse = aiResponse, count = currentCount });

                this.ReplyMessage(lineEvent.replyToken, $"{aiResponse}\n\n使用量：{currentCount}/500");
                return Ok();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); return Ok(); }
        }
    }
}
