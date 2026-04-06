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

                // 1. 讀取長期記憶
                string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
                var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

                // 2. 工具定義
                var tools = new object[] {
                    new { function_declarations = new object[] { 
                        new { name = "drive_search", description = "搜尋使用者的 Google Drive 檔案。僅當使用者明確要求『找檔案』時才調用。", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                        new { name = "calendar_list", description = "查詢接下來一週的行程" },
                        new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { summary = new { type = "string" }, startTime = new { type = "string", description = "格式為: yyyy-MM-ddTHH:mm:ss+08:00" }, endTime = new { type = "string", description = "格式為: yyyy-MM-ddTHH:mm:ss+08:00" } }, required = new[] { "summary", "startTime", "endTime" } } },
                        new { name = "gmail_send", description = "直接寄出郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                        new { name = "add_lesson_note", description = "紀錄教案靈感或學生觀察筆記。", parameters = new {type = "object", properties = new {category = new { type = "string" },title = new { type = "string" }, content = new { type = "string" }}, required = new[] { "category", "title", "content" }}}
                    } }
                };

                // 3. 系統提示詞優化 (解決誤觸搜尋問題)
                string systemPrompt = $"你是一位專業教育助理。現在時間 {currentTimeInfo}。\n" +
                                     "【運作守則】\n" +
                                     "1. 優先使用內建知識回答時事、教育政策、教學技巧。只有使用者明確說「搜尋雲端」或「找檔案」才調用 drive_search。\n" +
                                     "2. 回覆語氣溫柔專業，使用繁體中文。條列化回覆內容。\n" +
                                     "3. 雲端檔案請顯示檔名並製作超連結。行事曆請用『● [時間] [事項]』呈現。";

                var contents = new List<object>();
                contents.AddRange(historyList);
                contents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });

                var requestBody = new {
                    contents = contents,
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 },
                    tools = tools
                };

                var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
                dynamic? result = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
                var part = result?.candidates?[0]?.content?.parts?[0];

                // 4. 第二階段：處理 Function Calling
                if (part?.functionCall != null)
                {
                    string funcName = (string)part.functionCall.name;
                    object gasPayload = (funcName == "add_lesson_note") ? 
                        new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)part.functionCall.args.category, (string)part.functionCall.args.title, (string)part.functionCall.args.content } } :
                        new { action = funcName, args = part.functionCall.args };

                    string gasRes = await CallGasAsync(gasPayload);
                    object toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { };

                    var finalContents = new List<object>();
                    finalContents.AddRange(historyList);
                    finalContents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });
                    finalContents.Add(new { role = "model", parts = new object[] { new { functionCall = part.functionCall } } });
                    finalContents.Add(new { role = "function", parts = new object[] { new { functionResponse = new { name = funcName, response = toolResultObject } } } });

                    var finalBody = new {
                        contents = finalContents,
                        systemInstruction = requestBody.systemInstruction,
                        generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 },
                        tools = tools
                    };

                    var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
                    dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
                    return (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text ?? "已為您處理完成。";
                }

                return (string?)part?.text ?? "老師，我正在為您思考中...";
            }
            catch (Exception ex) { return $"系統訊息：{ex.Message}"; }
        }

        public static async Task<string> CallGasAsync(object payloadData)
        {
            try {
                using var client = new HttpClient();
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(payloadData)) ?? new Dictionary<string, object>();
                dict["apiKey"] = GasApiKey;
                var res = await client.PostAsync(GasUrl, new StringContent(JsonConvert.SerializeObject(dict), Encoding.UTF8, "application/json"));
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
            try
            {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || lineEvent.type.ToLower() != "message" || lineEvent.message.type != "text") return Ok();

                string userId = lineEvent.source.userId;
                string userText = lineEvent.message.text;

                // --- 優化點：並行啟動使用量計算與 Gemini 回應 ---
                var usageTask = BotService.CallGasAsync(new { action = "usage_increment", userId = userId });
                var aiTask = BotService.GetGeminiResponseAsync(userId, userText);

                // 啟動動畫 (非同步)
                _ = Task.Run(async () => {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ChannelAccessToken);
                    await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                        new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 10 }), Encoding.UTF8, "application/json"));
                });

                // 等待回應
                string aiResponse = await aiTask;
                dynamic? usageRes = JsonConvert.DeserializeObject(await usageTask);
                int count = usageRes?.count ?? 0;

                // 異步寫入日誌
                _ = BotService.CallGasAsync(new { action = "sheets_append", userId = userId, userText = userText, aiResponse = aiResponse, count = count });

                this.ReplyMessage(lineEvent.replyToken, $"{aiResponse}\n\n使用量：{count}/500");
                return Ok();
            }
            catch { return Ok(); }
        }
    }
}
