using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace isRock.Template
{
    // --- 1. 使用量管理員 (每日 500 次，15:00 重置) ---
    public static class UsageManager
    {
        private static int _todayCount = 0;
        private static DateTime _nextResetTime = GetNextResetTime();
        private static readonly object _lock = new object();

        private static DateTime GetNextResetTime()
        {
            DateTime now = DateTime.UtcNow.AddHours(8); 
            DateTime resetToday = new DateTime(now.Year, now.Month, now.Day, 15, 0, 0);
            return now < resetToday ? resetToday : resetToday.AddDays(1);
        }

        public static int GetAndIncrementCount(out bool isOverLimit)
        {
            lock (_lock)
            {
                if (DateTime.UtcNow.AddHours(8) >= _nextResetTime)
                {
                    _todayCount = 0;
                    _nextResetTime = GetNextResetTime();
                }
                isOverLimit = _todayCount >= 500;
                if (!isOverLimit) _todayCount++;
                return _todayCount;
            }
        }
    }

    // --- 2. 對話歷史管理員 (縮減至 5 輪，確保指令執行精準度) ---
    public static class ChatHistoryManager
    {
        private static readonly ConcurrentDictionary<string, List<object>> _history = new ConcurrentDictionary<string, List<object>>();
        private const int MaxHistory = 5; 

        public static List<object> GetHistory(string userId)
        {
            return _history.GetOrAdd(userId, _ => new List<object>());
        }

        public static void AddMessage(string userId, string role, string content)
        {
            var userHistory = GetHistory(userId);
            string geminiRole = role.ToLower() == "assistant" ? "model" : "user";
            userHistory.Add(new { role = geminiRole, parts = new[] { new { text = content } } });
            if (userHistory.Count > (MaxHistory * 2)) userHistory.RemoveAt(0);
        }
    }

    // --- 3. 搜尋快取管理員 (30 分鐘) ---
    public static class SearchCacheManager
    {
        private class CacheEntry {
            public string Result { get; set; }
            public DateTime ExpireTime { get; set; }
        }
        private static readonly ConcurrentDictionary<string, CacheEntry> _searchCache = new ConcurrentDictionary<string, CacheEntry>();

        public static bool TryGetCache(string query, out string result)
        {
            if (_searchCache.TryGetValue(query, out var entry))
            {
                if (DateTime.Now < entry.ExpireTime) { result = entry.Result; return true; }
                _searchCache.TryRemove(query, out _);
            }
            result = null; return false;
        }

        public static void SetCache(string query, string result)
        {
            _searchCache[query] = new CacheEntry { Result = result, ExpireTime = DateTime.Now.AddMinutes(30) };
        }
    }

    // --- 4. Gemini 服務 (AI 建議按鈕指令強化) ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private const string ModelName = "gemini-3.1-flash-lite-preview";

        public static async Task<(string text, int tokens)> GetResponseAsync(string userId, string userQuery)
        {
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult))
                return (cachedResult, 0);

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={ApiKey}";
            string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy年MM月dd日 dddd HH:mm");

            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { 
                    parts = new[] { new { 
                        text = $"你是一位資深的教育顧問。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣與老師對話。" +
                               "【強制指令】：回覆結尾必須換行輸入一個 '|' 符號，隨後提供 3 個建議追問按鈕（每個 10 字內），用逗號隔開。" +
                               "範例：[回覆內容] \n| 更短版本, 三年級難度, 做成學習單"
                    } } 
                },
                generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 }
            };

            try 
            {
                using var client = new HttpClient();
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return ("導師正在沉思，請稍後再試。", 0);

                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光。";
                int totalTokens = result?.usageMetadata?.totalTokenCount ?? 0;

                SearchCacheManager.SetCache(userQuery, textResult);
                return (textResult, totalTokens);
            }
            catch (Exception ex) { return ("AI 暫時切斷了聯繫。", 0); }
        }
    }

    // --- 5. LINE WebHook 控制器 (V1.3：Loading 動畫與動態按鈕) ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        private string ChannelToken => Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");

        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive!");

        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST()
        {
            try
            {
                this.ChannelAccessToken = ChannelToken;
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || string.IsNullOrEmpty(lineEvent.replyToken)) return Ok();

                if (lineEvent.type.ToLower() == "message" && lineEvent.message.type == "text")
                {
                    string userId = lineEvent.source.userId;
                    string userText = lineEvent.message.text;

                    // 1. 立即啟動讀取中動畫 (Feature 1)
                    _ = StartLoadingAnimation(userId, 5);

                    int currentCount = UsageManager.GetAndIncrementCount(out bool isOverLimit);
                    if (isOverLimit) { this.ReplyMessage("🌟 今日配額已滿。"); return Ok(); }

                    ChatHistoryManager.AddMessage(userId, "user", userText);
                    var (rawResponse, totalTokens) = await GeminiLLM.GetResponseAsync(userId, userText);
                    ChatHistoryManager.AddMessage(userId, "assistant", rawResponse);

                    // 2. 解析動態建議按鈕 (Feature 2)
                    string displayMsg = rawResponse;
                    var quickReplyItems = new List<isRock.LineBot.QuickReplyItem>();

                    if (rawResponse.Contains("|"))
                    {
                        var parts = rawResponse.Split('|');
                        displayMsg = parts[0].Trim(); 
                        var suggestions = parts[1].Split(new[] { ',', '，' });
                        foreach (var s in suggestions) {
                            if (!string.IsNullOrWhiteSpace(s) && quickReplyItems.Count < 5)
                                quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction(s.Trim(), s.Trim()));
                        }
                    }

                    // 備援按鈕 (若 AI 沒給建議)
                    if (quickReplyItems.Count == 0)
                    {
                        quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction("更短版本", "請給我更簡短的版本"));
                        quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction("三年級難度", "請將難度調整為國小三年級程度"));
                        quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction("做成學習單", "請幫我設計成學習單內容"));
                    }

                    string tokenInfo = totalTokens > 0 ? $"總計消耗：{totalTokens} tokens" : "（來自快取）";
                    string finalText = $"{displayMsg}\n\n次數：{currentCount}/500\n{tokenInfo}";

                    // 3. 封裝並發送 (修正 ReplyMessage 呼叫)
                    var replyMsg = new isRock.LineBot.TextMessage(finalText);
                    replyMsg.quickReply.items.AddRange(quickReplyItems);

                    // 重要：使用 MessageBase 物件時，SDK 會自動處理 replyToken
                    this.ReplyMessage(replyMsg);
                }
                return Ok();
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); return Ok(); }
        }

        private async Task StartLoadingAnimation(string userId, int seconds)
        {
            try {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ChannelToken);
                var body = new { chatId = userId, loadingSeconds = seconds };
                var json = JsonConvert.SerializeObject(body);
                await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                    new StringContent(json, Encoding.UTF8, "application/json"));
            } catch { }
        }
    }
}
