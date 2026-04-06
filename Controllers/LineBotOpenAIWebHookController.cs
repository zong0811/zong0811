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
                // 🌟 維持使用您指定的 3.1 預覽版模型
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
                string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

                // 1. 讀取長期記憶
                string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
                var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

                // 2. 工具定義 (強化時區標籤引導)
                var tools = new object[] {
                    new { function_declarations = new object[] { 
                        new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                        new { name = "calendar_list", description = "查詢接下來一週的行程" },
                        new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { 
                            summary = new { type = "string", description = "活動名稱" }, 
                            startTime = new { type = "string", description = "ISO格式且務必包含 +08:00，例如: 2026-04-07T16:00:00+08:00" }, 
                            endTime = new { type = "string", description = "ISO格式且務必包含 +08:00，例如: 2026-04-07T17:00:00+08:00" } 
                        }, required = new[] { "summary", "startTime", "endTime" } } },
                        new { name = "gmail_send", description = "直接寄出電子郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                        new { name = "add_lesson_note", description = "記錄教案筆記", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
                    } }
                };

                // 3. 第一階段：判定意圖
                var contents = new List<object>();
                contents.AddRange(historyList);
                contents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });

                var requestBody = new {
                    contents = contents,
                    systemInstruction = new { parts = new[] { new { text = $"你是一位溫柔且專業的資深教育助理。現在是台灣時間 {currentTimeInfo}。獲得工具執行結果後，請務必詳細列出內容回答使用者。" } } },
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
                    object toolResultObject;
                    try { toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { raw_data = gasRes }; }
                    catch { toolResultObject = new { raw_data = gasRes }; }

                    // 🌟 核心修正：將結果完整餵回給 3.1 模型做總結
                    var finalContents = new List<object>();
                    finalContents.AddRange(historyList);
                    finalContents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });
                    finalContents.Add(new { role = "model", parts = new object[] { new { functionCall = part.functionCall } } });
                    finalContents.Add(new { role = "function", parts = new object[] { new { functionResponse = new { name = funcName, response = toolResultObject } } } });

                    var finalBody = new {
                        contents = finalContents,
                        systemInstruction = requestBody.systemInstruction,
                        tools = tools
                    };

                    var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
                    dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
                    string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

                    return aiText ?? "我已經完成動作了！詳細內容您可以直接查看 Google 試算表或日曆喔。";
                }

                return (string?)part?.text ?? "AI正在為您準備回覆...";
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
    } // 這裡關閉 BotService

    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive! V2.1");

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

                // A. 配額檢查
                var usageResJson = await BotService.CallGasAsync(new { action = "usage_increment", userId = userId });
                dynamic? usageRes = JsonConvert.DeserializeObject(usageResJson);
                int currentCount = usageRes?.count ?? 0;

                if ((bool?)(usageRes?.isOverLimit) ?? false) {
                    this.ReplyMessage(replyToken, "🌟 今日配額已滿。");
                    return Ok();
                }

                // B. 啟動 Loading 動畫
                _ = Task.Run(async () => {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ChannelAccessToken);
                    await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                        new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 20 }), Encoding.UTF8, "application/json"));
                });

                // C. 取得 AI 回覆
                string aiResponse = await BotService.GetGeminiResponseAsync(userId, userText);

                // D. 異步紀錄對話
                _ = BotService.CallGasAsync(new { 
                    action = "sheets_append", userId = userId, userText = userText, aiResponse = aiResponse, count = currentCount
                });

                // E. 回覆 LINE
                this.ReplyMessage(replyToken, $"{aiResponse}\n\n使用量：{currentCount}/500");
                return Ok();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); return Ok(); }
        }
    } // 這裡關閉 Controller
} // 這裡關閉 Namespace
