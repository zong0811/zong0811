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
                // 🌟 使用您指定的 3.1 預覽版模型
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
                string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

                // 1. 讀取記憶
                string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
                var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

                // 2. 工具定義
                var tools = new object[] {
                    new { function_declarations = new object[] { 
                        new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                        new { name = "calendar_list", description = "查詢接下來一週的行程" },
                        new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { summary = new { type = "string" }, startTime = new { type = "string", description = "yyyy-MM-ddTHH:mm:ss+08:00" }, endTime = new { type = "string", description = "yyyy-MM-ddTHH:mm:ss+08:00" } }, required = new[] { "summary", "startTime", "endTime" } } },
                        new { name = "gmail_send", description = "直接寄出郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                        new { name = "add_lesson_note", description = "記錄教案筆記", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
                    } }
                };

                // 3. 第一階段：判斷意圖
                var contents = new List<object>();
                contents.AddRange(historyList);
                contents.Add(new { role = "user", parts = new object[] { new { text = userQuery } } });

                // 🌟 強化指令：要求 AI 使用特定格式回覆
                string systemPrompt = $"你是一位專業教育助理。現在時間 {currentTimeInfo}。請遵守回覆規範：\n" +
                                     "1. 行事曆新增後必說：我已完成 \"[事項名稱]\" 行事曆新增。\n" +
                                     "2. 教案記事新增後必說：我已完成 \"[標題]\" 教案記事新增。\n" +
                                     "3. 寄信後必說：我已寄出 \"[主旨]\" 的郵件。\n" +
                                     "4. 搜尋檔案必列出：\"[檔案名稱]\" 及其下載網址清單。";

                var requestBody = new {
                    contents = contents,
                    systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                    generationConfig = new { maxOutputTokens = 1500, temperature = 0.5 },
                    tools = tools
                };

                var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
                dynamic? result = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
                var part = result?.candidates?[0]?.content?.parts?[0];

                // 4. 第二階段：處理執行結果
                if (part?.functionCall != null)
                {
                    string funcName = (string)part.functionCall.name;
                    object gasPayload = (funcName == "add_lesson_note") ? 
                        new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)part.functionCall.args.category, (string)part.functionCall.args.title, (string)part.functionCall.args.content } } :
                        new { action = funcName, args = part.functionCall.args };

                    string gasRes = await CallGasAsync(gasPayload);
                    object toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { raw_data = gasRes };

                    // 構建完整對話鏈
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

                    // 🌟 修正點：必須讀取 finalRes 而不是 res
                    var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
                    var finalResStr = await finalRes.Content.ReadAsStringAsync();
                    dynamic? finalJson = JsonConvert.DeserializeObject(finalResStr);
                    
                    string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

                    // 如果 AI 還是沒吐出文字，根據動作類型強制產生回饋
                    if (string.IsNullOrEmpty(aiText)) {
                        if (funcName == "calendar_add") return $"我已完成 \"{part.functionCall.args.summary}\" 行事曆新增。";
                        if (funcName == "add_lesson_note") return $"我已完成 \"{part.functionCall.args.title}\" 教案記事新增。";
                        if (funcName == "gmail_send") return $"我已寄出 \"{part.functionCall.args.subject}\" 的郵件。";
                    }

                    return aiText ?? "動作已執行，請檢查相關內容。";
                }

                return (string?)part?.text ?? "導師正在為您準備...";
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
        public IActionResult Get() => Ok("Bot is Alive! 2026/04/06");

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

                var usageResJson = await BotService.CallGasAsync(new { action = "usage_increment", userId = userId });
                dynamic? usageRes = JsonConvert.DeserializeObject(usageResJson);
                int currentCount = usageRes?.count ?? 0;

                _ = Task.Run(async () => {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.ChannelAccessToken);
                    await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                        new StringContent(JsonConvert.SerializeObject(new { chatId = userId, loadingSeconds = 20 }), Encoding.UTF8, "application/json"));
                });

                string aiResponse = await BotService.GetGeminiResponseAsync(userId, userText);

                _ = BotService.CallGasAsync(new { action = "sheets_append", userId = userId, userText = userText, aiResponse = aiResponse, count = currentCount });

                this.ReplyMessage(lineEvent.replyToken, $"{aiResponse}\n\n使用量：{currentCount}/500");
                return Ok();
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); return Ok(); }
        }
    }
}
