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
    // --- 服務層：負責所有對外 API 溝通 ---
    public static class BotService
    {
        private static string GeminiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private static string GasUrl => Environment.GetEnvironmentVariable("GAS_WEB_APP_URL") ?? "";
        private static string GasApiKey => Environment.GetEnvironmentVariable("GAS_API_KEY") ?? "";

        public static async Task<string> GetGeminiResponseAsync(string userId, string userQuery)
{
    try {
        using var client = new HttpClient();
        // 🌟 維持使用您要求的 3.1 預覽版模型
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
        
        // 取得台灣時間，這能幫助 AI 正確判斷「明天」、「下午」
        string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

        // 1. 讀取長期記憶
        string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
        var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

        // 2. 工具定義：加入「強制時區規範」解決 12AM 偏移問題
        var tools = new object[] {
            new { function_declarations = new object[] { 
                new { name = "drive_search", description = "搜尋 Google Drive 中的檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                new { name = "calendar_list", description = "查詢接下來一週的行程" },
                new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { 
                    summary = new { type = "string", description = "活動名稱" }, 
                    startTime = new { type = "string", description = "ISO格式且務必包含時區偏移，例如: 2026-04-07T16:00:00+08:00" }, 
                    endTime = new { type = "string", description = "ISO格式且務必包含時區偏移，例如: 2026-04-07T17:00:00+08:00" } 
                }, required = new[] { "summary", "startTime", "endTime" } } },
                new { name = "gmail_send", description = "直接寄出郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                new { name = "add_lesson_note", description = "記錄教學筆記或教案靈感", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
            } }
        };

        // 3. 準備發送內容
        var contents = new List<object>();
        contents.AddRange(historyList);
        contents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });

        var requestBody = new {
            contents = contents,
            systemInstruction = new { parts = new[] { new { text = $"你是一位資深的教育人員。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣對話。當執行工具後收到回傳資料，請務必將內容條列化整理給使用者。" } } },
            generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 },
            tools = tools
        };

        // 第一次呼叫 (判定意圖)
        var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
        var resStr = await res.Content.ReadAsStringAsync();
        
        // Debug: 如果 API 回傳錯誤，你會在 Console 看到原因
        if (!res.IsSuccessStatusCode) Console.WriteLine($"API Error 1: {resStr}");

        dynamic? result = JsonConvert.DeserializeObject(resStr);
        var part = result?.candidates?[0]?.content?.parts?[0];

        // 4. 處理工具執行 (Function Calling)
        if (part?.functionCall != null)
        {
            string funcName = (string)part.functionCall.name;
            object gasPayload = (funcName == "add_lesson_note") ? 
                new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)part.functionCall.args.category, (string)part.functionCall.args.title, (string)part.functionCall.args.content } } :
                new { action = funcName, args = part.functionCall.args };

            // 呼叫 GAS
            string gasRes = await CallGasAsync(gasPayload);
            object toolResultObject;
            try { toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { }; }
            catch { toolResultObject = new { raw_data = gasRes }; }

            // 🌟 核心修正：建構嚴謹的「多輪對話鏈」
            var finalContents = new List<object>();
            finalContents.AddRange(historyList); // 1. 歷史
            finalContents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } }); // 2. 提問
            finalContents.Add(new { role = "model", parts = new object[] { new { functionCall = part.functionCall } } }); // 3. 呼叫命令
            finalContents.Add(new { role = "function", parts = new object[] { 
                new { functionResponse = new { name = funcName, response = toolResultObject } } 
            } }); // 4. 執行結果 (直接傳入物件，不需額外包 content)

            var finalBody = new {
                contents = finalContents,
                systemInstruction = requestBody.systemInstruction,
                tools = tools
            };

            // 第二次呼叫 (總結結果)
            var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
            var finalResStr = await finalRes.Content.ReadAsStringAsync();
            
            // Debug: 檢查第二次呼叫是否出錯
            if (!finalRes.IsSuccessStatusCode) Console.WriteLine($"API Error 2: {finalResStr}");

            dynamic? finalJson = JsonConvert.DeserializeObject(finalResStr);
            string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

            return aiText ?? "我已完成動作，但暫時無法產生文字摘要，請確認雲端紀錄。";
        }

        return (string?)part?.text ?? "AI正在思考中...";
    }
    catch (Exception ex) { return $"系統連線異常：{ex.Message}"; }
}

    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        // 🌟 建議加在這裡：這讓瀏覽器或測試工具可以直接透過 GET 存取這個網址
        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive! 04/06版");        
        [HttpPost] [Route("api/LineBotOpenAIWebHook")]
        public async Task<IActionResult> POST()
        {
            try
            {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || lineEvent.type.ToLower() != "message" || lineEvent.message.type != "text") return Ok();

                string userId = lineEvent.source.userId;
                string userText = lineEvent.message.text;
                string replyToken = lineEvent.replyToken;

                var usageResJson = await BotService.CallGasAsync(new { action = "usage_increment", userId = userId });
                dynamic? usageRes = JsonConvert.DeserializeObject(usageResJson);
                int currentCount = usageRes?.count ?? 0;

                if ((bool?)(usageRes?.isOverLimit) ?? false) {
                    this.ReplyMessage(replyToken, "🌟 今日配額已滿。");
                    return Ok();
                }

                _ = Task.Run(async () => {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ChannelAccessToken);
                    await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                        new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 20 }), Encoding.UTF8, "application/json"));
                });

                string aiResponse = await BotService.GetGeminiResponseAsync(userId, userText);

                _ = BotService.CallGasAsync(new { 
                    action = "sheets_append", userId = userId, userText = userText, aiResponse = aiResponse, count = currentCount
                });

                this.ReplyMessage(replyToken, $"{aiResponse}\n\n使用量：{currentCount}/500");
                return Ok();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); return Ok(); }
        }
    }
}
