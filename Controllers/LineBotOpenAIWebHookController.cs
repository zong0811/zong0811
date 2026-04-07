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
        // 🌟 使用您指定的 3.1 預覽版模型
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite-preview:generateContent?key={GeminiKey}";
        string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

        // 1. 讀取長期記憶
        string historyJson = await CallGasAsync(new { action = "get_chat_history", userId = userId });
        var historyList = JsonConvert.DeserializeObject<List<object>>(historyJson) ?? new List<object>();

        // 2. 工具定義
        var tools = new object[] {
            new { function_declarations = new object[] { 
                new { name = "drive_search", description = "搜尋 Google Drive 檔案", parameters = new { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" } } },
                new { name = "calendar_list", description = "查詢接下來一週的行程" },
                new { name = "calendar_add", description = "在日曆中新增行程", parameters = new { type = "object", properties = new { summary = new { type = "string" }, startTime = new { type = "string", description = "格式: yyyy-MM-ddTHH:mm:ss+08:00" }, endTime = new { type = "string", description = "格式: yyyy-MM-ddTHH:mm:ss+08:00" } }, required = new[] { "summary", "startTime", "endTime" } } },
                new { name = "gmail_send", description = "直接寄出郵件", parameters = new { type = "object", properties = new { recipient = new { type = "string" }, subject = new { type = "string" }, body = new { type = "string" } }, required = new[] { "recipient", "subject", "body" } } },
                new { name = "add_lesson_note", description = "記錄教學發現或教案筆記。當要求整理內容並記錄時必用。", parameters = new { type = "object", properties = new { category = new { type = "string", description = "分類" }, title = new { type = "string", description = "10字內精簡標題" }, content = new { type = "string", description = "整理後的詳細內容" } }, required = new[] { "category", "title", "content" } } }
            } }
        };

        // 3. 系統指令 (整合要求的匯報格式)
        string systemPrompt = $"你是一位專業教育人員，也是使用者的專屬助手。現在時間 {currentTimeInfo}。\n" +
                     "【核心原則】：若對話涉及資料整理、紀錄或建議，你必須先用內建知識回答教育政策與技巧再『執行』add_lesson_note 工具將整理內容存檔，嚴禁虛構儲存成功的訊息。\n" +
                     "【工具使用限制】：優先用內建知識回答教育政策與技巧。僅在明確提到「搜尋雲端」時調用 drive_search。\n" +
                     "【重要】：若使用者要求多項任務（如：整理後寄信並存檔），請同時調用多個對應工具。\n" +
                     "【回覆結構（必備）】：當工具執行成功後，請按照以下順序產出最終回覆：\n" +
                     "   ### [標題]\n" +
                     "   1. **詳細整理內容**：針對使用者的需求，提供深入且條列化的政策或教學知識點。\n" +
                     "   2. **儲存回報**：使用分隔線「---」，清楚列出存入試算表的類別、標題與內容摘要。\n" +
                     "   3. **專屬互動**：回覆最後針對內容主動提出一個有助於實務應用的追蹤問題。\n" +
                     "【格式規範】：務必使用 Markdown (###, **, *)，確保手機介面易於閱讀。";

        var contents = new List<object>(historyList) {
            new { role = "user", parts = new[] { new { text = userQuery } } }
        };

        var requestBody = new {
            contents = contents,
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            generationConfig = new { maxOutputTokens = 2000, temperature = 0.7 },
            tools = tools
        };

        // 第一次呼叫：判定意圖
        var res = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"));
        dynamic? result = JsonConvert.DeserializeObject(await res.Content.ReadAsStringAsync());
        var parts = result?.candidates?[0]?.content?.parts;

        if (parts == null) return "導師正在思考中...";

        List<object> modelParts = new List<object>();
        List<object> functionResponses = new List<object>();
        bool hasFunctionCall = false;

        // 4. 處理多重工具呼叫 (Parallel Function Calling 迴圈)
        foreach (var p in parts)
        {
            if (p.functionCall != null)
            {
                hasFunctionCall = true;
                string funcName = (string)p.functionCall.name;
                var args = p.functionCall.args;
                
                // 決定 GAS Payload
                object gasPayload = (funcName == "add_lesson_note") ? 
                    new { action = "custom_log", targetSheet = "教案記事本", rowContents = new[] { (string)args["category"], (string)args["title"], (string)args["content"] } } :
                    new { action = funcName, args = args };

                // 執行動作
                string gasRes = await CallGasAsync(gasPayload);
                object toolResultObject = JsonConvert.DeserializeObject(gasRes) ?? new { };

                // 紀錄模型動作與執行結果
                modelParts.Add(new { functionCall = p.functionCall });
                functionResponses.Add(new { 
                    role = "function", 
                    parts = new[] { new { functionResponse = new { name = funcName, response = toolResultObject } } } 
                });
            }
            else if (p.text != null)
            {
                modelParts.Add(new { text = (string)p.text });
            }
        }

        if (hasFunctionCall)
        {
            // 5. 第二次呼叫：產出匯報長文
            var finalContents = new List<object>(historyList);
            finalContents.Add(new { role = "user", parts = new[] { new { text = userQuery } } });
            finalContents.Add(new { role = "model", parts = modelParts });
            finalContents.AddRange(functionResponses);

            var finalBody = new {
                contents = finalContents,
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                generationConfig = new { maxOutputTokens = 2000, temperature = 0.7 },
                tools = tools
            };

            var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
            dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
            string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

            // 備援排版 (Fallback)
            if (string.IsNullOrEmpty(aiText)) {
                var lastAction = (dynamic)modelParts.Last();
                return FormatFallback((string)lastAction.functionCall.name, "{}", lastAction.functionCall.args);
            }

            return aiText;
        }

        return (string?)parts[0]?.text ?? "導師正在為您準備回覆...";
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

        private static string FormatFallback(string funcName, string rawJson, dynamic args) {
            if (funcName == "drive_search") {
                var files = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(rawJson);
                var sb = new StringBuilder("老師，我為您找到了以下檔案：\n\n");
                foreach(var f in files) sb.AppendLine($"📄 **{f["name"]}**\n🔗 {f["url"]}\n");
                return sb.ToString();
            }
            if (funcName == "calendar_list") {
                var events = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(rawJson);
                var sb = new StringBuilder("老師，您接下來的行程如下：\n\n");
                foreach(var e in events) sb.AppendLine($"⏰ {e["start"]}\n📌 {e["summary"]}\n");
                return sb.ToString();
            }
            if (funcName == "calendar_add") return $"我已完成 \"{args.summary}\" 行事曆新增。";
            if (funcName == "add_lesson_note") return $"我已完成 \"{args.title}\" 教案記事新增。";
            if (funcName == "gmail_send") return $"我已寄出 \"{args.subject}\" 的郵件。";
            return $"動作已執行成功！內容如下：\n{rawJson}";
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
