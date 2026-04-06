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
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
        string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

        // 1. 關鍵步驟：從 GAS 讀取持久化記憶
        string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
        var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

        // 2. 建立動態內容陣列
        var contents = new List<object>();
        contents.AddRange(historyList); // 加入過去的對話紀錄
        contents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } }); // 加入當下的提問

        var tools = new object[] {
            new { function_declarations = new object[] { 
                new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                new { name = "calendar_list", description = "查詢接下來一週的行程" },
                new { name = "calendar_add", description = "新增日曆行程", parameters = new { type = "object", properties = new { summary = new { type = "string" }, startTime = new { type = "string" }, endTime = new { type = "string" } }, required = new[] { "summary", "startTime", "endTime" } } },
                new { name = "gmail_send", description = "直接寄出 Gmail 郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                new { name = "add_lesson_note", description = "記錄教案筆記", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
            } }
        };

        var requestBody = new {
            contents = contents, // 使用包含記憶的內容
            systemInstruction = new { 
                parts = new[] { new { text = $"你是一位資深的教育人員。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣與使用者對話。" } } 
            },
            generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 },
            tools = tools
        };

                var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
                var resStr = await res.Content.ReadAsStringAsync();
                dynamic? result = JsonConvert.DeserializeObject(resStr);
                
                var part = result?.candidates?[0]?.content?.parts?[0];

                if (part?.functionCall != null)
                {
                    string funcName = (string)part.functionCall.name;
                    object gasPayload;

                    // 2. 邏輯判斷：如果是教案記事，則重新封裝給 GAS 的 custom_log
                    if (funcName == "add_lesson_note") 
                    { 
                        gasPayload = new { 
                            action = "custom_log", 
                            targetSheet = "教案記事本", // 指定寫入的分頁 
                            rowContents = new[] { 
                                (string)part.functionCall.args.category, 
                                (string)part.functionCall.args.title, 
                                (string)part.functionCall.args.content 
                            } 
                        }; 
                    }
                    else 
                    {
                        gasPayload = new { action = funcName, args = part.functionCall.args };
                    }

                    string gasRes = await CallGasAsync(gasPayload);

                    object toolResultObject;
                    try { toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { }; }
                    catch { toolResultObject = new { raw_data = gasRes }; }

                    var finalBody = new {
                        contents = new object[] {
                            new { role = "user", parts = new object[] { new { text = userQuery } } },
                            new { role = "model", parts = new object[] { new { functionCall = part.functionCall } } },
                            new { role = "function", parts = new object[] { 
                                new { functionResponse = new { name = funcName, response = new { content = toolResultObject } } } 
                            } }
                        }
                    };
                    var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
                    dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
                    return (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text ?? "動作已執行。";
                }
                return (string?)part?.text ?? "系統還在設定中...";
            }
            catch (Exception ex) { return $"系統異常：{ex.Message}"; }
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
        public IActionResult Get() => Ok("Bot is Alive!");        
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
