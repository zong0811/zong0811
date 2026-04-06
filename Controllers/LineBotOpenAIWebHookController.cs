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
        // 🌟 依照要求，維持使用 gemini-3.1-flash-lite-preview
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
        string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

        // 1. 從 GAS 讀取歷史記憶
        string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
        var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

        // 2. 工具定義 (加入時區描述，解決 12 AM 問題)
        var tools = new object[] {
            new { function_declarations = new object[] { 
                new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                new { name = "calendar_list", description = "查詢接下來一週的行程內容" },
                new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { 
                    summary = new { type = "string" }, 
                    startTime = new { type = "string", description = "格式務必為: yyyy-MM-ddTHH:mm:ss+08:00" }, 
                    endTime = new { type = "string", description = "格式務必為: yyyy-MM-ddTHH:mm:ss+08:00" } 
                }, required = new[] { "summary", "startTime", "endTime" } } },
                new { name = "gmail_send", description = "直接寄出電子郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                new { name = "add_lesson_note", description = "記錄教案筆記", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
            } }
        };

        // 3. 第一次呼叫內容：包含歷史記憶
        var contents = new List<object>();
        contents.AddRange(historyList);
        contents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });

        var requestBody = new {
            contents = contents,
            systemInstruction = new { parts = new[] { new { text = $"你是一位資深的教育人員。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣對話。當獲得工具執行結果後，請詳細整理並條列內容回覆給使用者。" } } },
            generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 },
            tools = tools
        };

        var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
        var resStr = await res.Content.ReadAsStringAsync();
        dynamic? result = JsonConvert.DeserializeObject(resStr);
        var part = result?.candidates?[0]?.content?.parts?[0];

        // 4. 處理工具呼叫
        if (part?.functionCall != null)
        {
            string funcName = (string)part.functionCall.name;
            object gasPayload = (funcName == "add_lesson_note") ? 
                new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)part.functionCall.args.category, (string)part.functionCall.args.title, (string)part.functionCall.args.content } } :
                new { action = funcName, args = part.functionCall.args };

            string gasRes = await CallGasAsync(gasPayload);
            
            // 解析 GAS 結果
            object toolResultObject;
            try { toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { result = gasRes }; }
            catch { toolResultObject = new { result = gasRes }; }

            // 🌟 關鍵修正：第二次呼叫必須建立完整的上下文鏈結 (Context Chain)
            var finalContents = new List<object>();
            finalContents.AddRange(historyList); // 加入對話歷史
            finalContents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } }); // 使用者問題
            finalContents.Add(new { role = "model", parts = new object[] { new { functionCall = part.functionCall } } }); // AI 要求呼叫工具
            finalContents.Add(new { role = "function", parts = new object[] { 
                new { functionResponse = new { name = funcName, response = new { content = toolResultObject } } } 
            } }); // 工具執行結果

            var finalBody = new {
                contents = finalContents,
                systemInstruction = requestBody.systemInstruction, // 角色設定也要補回
                tools = tools
            };

            var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
            var finalResStr = await finalRes.Content.ReadAsStringAsync();
            dynamic? finalJson = JsonConvert.DeserializeObject(finalResStr);
            
            // 嘗試取得 AI 生成的最終文字
            string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;
            
            return aiText ?? "動作已成功執行，但我暫時無法產生摘要。請確認您的雲端紀錄。";
        }

        return (string?)part?.text ?? "AI正在思考中...";
    }
    catch (Exception ex) { return $"系統連線出了問題：{ex.Message}"; }
}

        public static async Task<string> CallGasAsync(object payloadData)
        {
            try {
                using var client = new HttpClient();
                string json = JsonConvert.SerializeObject(payloadData);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
                dict["apiKey"] = GasApiKey;

                var content = new StringContent(JsonConvert.SerializeObject(dict), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(GasUrl, content);
                return await res.Content.ReadAsStringAsync();
            } catch { return "{}"; }
        }
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
