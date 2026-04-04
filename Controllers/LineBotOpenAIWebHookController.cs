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
                    Console.WriteLine($">>> [系統狀態] 次數已重置。");
                }
                isOverLimit = _todayCount >= 500;
                if (!isOverLimit) _todayCount++;
                return _todayCount;
            }
        }
    }

    // --- 2. 對話歷史管理員 (記憶 10 輪) ---
    public static class ChatHistoryManager
    {
        private static readonly ConcurrentDictionary<string, List<object>> _history = new ConcurrentDictionary<string, List<object>>();
        private const int MaxHistory = 10; 

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
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

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
            _searchCache[query] = new CacheEntry { Result = result, ExpireTime = DateTime.Now.Add(CacheDuration) };
        }
    }

    // --- 4. Gemini 服務 (強化提示詞指令) ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private const string ModelName = "gemini-3.1-flash-lite-preview";

        public static async Task<(string text, int tokens)> GetResponseAsync(string userId, string userQuery)
        {
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult))
                return (cachedResult, 0);

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={ApiKey}";
            DateTime twNow = DateTime.UtcNow.AddHours(8);
            string currentTimeInfo = twNow.ToString("yyyy年MM月dd日 dddd HH:mm");

            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { 
                    parts = new[] { new { 
                        text = $"你是一位資深的教育人員。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣與家長對話。" +
                               "【重要強制指令】：在你的回答結束後，必須先換行，然後輸入一個管道符號 '|'，接著提供 3 個建議追問的短標題（每個 10 字以內），用逗號隔開。" +
                               "回覆範例：親愛的家長...回覆內容 \n| 建立睡前儀式, 晨間節奏調整, 適合的小故事"
                    } } 
                },
                generationConfig = new { maxOutputTokens = 1000, temperature = 0.7 }
            };

            try 
            {
                using var client = new HttpClient();
                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return ("AI正在連線中，請稍後再試。", 0);

                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光。";
                int totalTokens = result?.usageMetadata?.totalTokenCount ?? 0;

                SearchCacheManager.SetCache(userQuery, textResult);
                return (textResult, totalTokens);
            }
            catch (Exception ex) { return ("AI暫時切斷了聯繫。", 0); }
        }
    }

    // --- 5. LINE WebHook 控制器 (修正按鈕發送邏輯) ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive!");

        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST()
        {
            try
            {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || string.IsNullOrEmpty(lineEvent.replyToken)) return Ok();

                if (lineEvent.type.ToLower() == "message" && lineEvent.message.type == "text")
                {
                    string userId = lineEvent.source.userId;
                    string userText = lineEvent.message.text;

                    int currentCount = UsageManager.GetAndIncrementCount(out bool isOverLimit);
                    if (isOverLimit) {
                        this.ReplyMessage(lineEvent.replyToken, "🌟 今日配額已滿。");
                        return Ok();
                    }

                    ChatHistoryManager.AddMessage(userId, "user", userText);
                    var (rawResponse, totalTokens) = await GeminiLLM.GetResponseAsync(userId, userText);
                    ChatHistoryManager.AddMessage(userId, "assistant", rawResponse);

                    // --- 解析邏輯 ---
                    string displayMsg = rawResponse;
                    var quickReplyItems = new List<isRock.LineBot.QuickReplyItem>();

                    if (rawResponse.Contains("|"))
                    {
                        var parts = rawResponse.Split('|');
                        displayMsg = parts[0].Trim(); 
                        var suggestions = parts[1].Split(',');

                        foreach (var suggestion in suggestions)
                        {
                            if (!string.IsNullOrWhiteSpace(suggestion))
                                quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction(suggestion.Trim(), suggestion.Trim()));
                        }
                    }

                    // 備援：若 AI 沒給建議，強制加入預設按鈕
                    if (quickReplyItems.Count == 0)
                    {
                        quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction("請再多說一點", "請導師再多跟我分享一些，謝謝。"));
                        quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction("對孩子的幫助？", "這對孩子的成長有什麼幫助呢？"));
                        quickReplyItems.Add(new isRock.LineBot.QuickReplyTextAction("居家活動建議", "有什麼適合在家做的活動嗎？"));
                    }

                    string tokenInfo = totalTokens > 0 ? $"總計消耗：{totalTokens} tokens" : "（來自快取）";
                    string finalText = $"{displayMsg}\n\n次數：{currentCount}/500\n{tokenInfo}";

                    // --- 重要修正：確保使用正確的 SDK 物件發送按鈕 ---
                    var replyMsg = new isRock.LineBot.TextMessage(finalText);
                    replyMsg.quickReply.items.AddRange(quickReplyItems);

                    // 使用 ReplyMessage 發送 Message 物件
                    this.ReplyMessage(lineEvent.replyToken, replyMsg);
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return Ok();
            }
        }
    }
}
