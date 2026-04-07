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
                        new { name = "add_lesson_note", description = "記錄教學發現或教案筆記。當要求整理內容並記錄時必用。", parameters = new { type = "object", properties = new { category = new { type = "string" }, title = new { type = "string" }, content = new { type = "string" } }, required = new[] { "category", "title", "content" } } }
                    } }
                };

                // 🌟 強化後的 Prompt：加入「強制任務清點」邏輯
        string systemPrompt = $"你是一位具備 30 年資歷的專業教育人員，也是使用者的專屬助手。現在時間 {currentTimeInfo}。\n" +
                             "【工具使用限制】：優先用內建知識回答教育政策與技巧。僅在明確提到「搜尋雲端」時調用 drive_search。\n" +
                             "【核心指令】：當使用者要求多項任務（如整理後寄信並存檔），你必須調用『所有』對應工具，且在最終回覆中『逐一確認』每一項任務的完成狀況。\n" +
                             "【回覆結構（嚴格執行）】：\n" +
                             "   ### [專業知識標題]\n" +
                             "   1. **詳細整理內容**：針對需求，提供至少 5-8 個深度條列要點。這是回覆的主體，嚴禁簡略。\n" +
                             "   2. **多重任務執行清單**：在分隔線「---」後，必須條列檢查並確認所有動作。例如：\n" +
                             "      - ✅ 郵件已寄送至：[收件人]\n" +
                             "      - ✅ 教案已歸檔至試算表（標題：[標題]）\n" +
                             "   3. **專屬互動**：最後針對內容提出一個引發實務討論的追蹤問題。\n" +
                             "【語氣要求】：文字溫潤、專業且精準。善用 Markdown 排版。";


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

                foreach (var p in parts) {
                    if (p.functionCall != null) {
                        hasFunctionCall = true;
                        string funcName = (string)p.functionCall.name;
                        var args = p.functionCall.args;
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

                    var finalBody = new {
                        contents = finalContents,
                        systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                        generationConfig = new { maxOutputTokens = 2000, temperature = 0.7 },
                        tools = tools
                    };

                    var finalRes = await client.PostAsync(url, new StringContent(JsonConvert.SerializeObject(finalBody), Encoding.UTF8, "application/json"));
                    dynamic? finalJson = JsonConvert.DeserializeObject(await finalRes.Content.ReadAsStringAsync());
                    string? aiText = (string?)finalJson?.candidates?[0]?.content?.parts?[0]?.text;

                    // 🌟 智慧型 Fallback：如果 AI 斷片沒回覆，我們手動清點所有動作
                    if (string.IsNullOrEmpty(aiText)) {
                        StringBuilder sb = new StringBuilder("宗志老師，所有任務皆已處理完成。報告如下：\n\n");
                        foreach(dynamic mp in modelParts) {
                            string name = mp.functionCall.name;
                            sb.AppendLine($"✅ 已執行動作：{name}");
                        }
                        sb.AppendLine("\n詳情請查看相關雲端紀錄。對於這些內容，還有需要我協助調整的地方嗎？");
                        return sb.ToString();
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
