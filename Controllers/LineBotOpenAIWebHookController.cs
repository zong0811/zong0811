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

    // --- 4. Gemini 服務 (動態按鈕提示工程) ---
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
                        text = $"你是一位資深教育導師。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣與家長對話。" +
                               "請在回覆的最末端，固定使用一個 '|' 符號隔開，隨後提供 3 個適合家長繼續追問的短選項（每個選項 10 字以內），用逗號隔開。" +
                               "範例格式：[你的導師回覆內容] | 建議問題一, 建議問題二, 建議問題三"
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

                if (!response.IsSuccessStatusCode) return ("AI連線失敗，請稍後再試。", 0);

                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "目前暫時無法回覆。";
                int totalTokens = result?.usageMetadata?.totalTokenCount ?? 0;

                SearchCacheManager.SetCache(userQuery, textResult);
                return (textResult, totalTokens);
            }
            catch (Exception ex)
            {
                Console.WriteLine($">>> [Gemini 異常]: {ex.Message}");
                return ("AI暫時斷線。", 0);
            }
        }
    }

    // --- 5. LINE WebHook 控制器 (動態解析與發送) ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        [HttpHead] [HttpGet]
        [Route("api/LineBotOpenAIWebHook")]
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
                    if (isOverLimit) 
                    {
                        this.ReplyMessage(lineEvent.replyToken, "🌟 今日使用次數已達上限。");
                        return Ok();
                    }

                    ChatHistoryManager.AddMessage(userId, "user", userText);
                    var (rawResponse, totalTokens) = await GeminiLLM.GetResponseAsync(userId, userText);
                    ChatHistoryManager.AddMessage(userId, "assistant", rawResponse);

                    // --- v1.2 核心邏輯：動態解析按鈕 ---
                    string displayMsg = rawResponse;
                    var quickReplyButtons = new List<isRock.LineBot.QuickReplyItem>();

                    if (rawResponse.Contains("|"))
                    {
                        var parts = rawResponse.Split('|');
                        displayMsg = parts[0].Trim(); // 前半部是回覆內容
                        var suggestions = parts[1].Split(','); // 後半部是追問建議

                        foreach (var suggestion in suggestions)
                        {
                            if (!string.IsNullOrWhiteSpace(suggestion))
                                quickReplyButtons.Add(new isRock.LineBot.QuickReplyTextAction(suggestion.Trim(), suggestion.Trim()));
                        }
                    }

                    // 如果 AI 沒給建議，加入預設溫馨按鈕
                    if (quickReplyButtons.Count == 0)
                    {
                        quickReplyButtons.Add(new isRock.LineBot.QuickReplyTextAction("請再多說一點", "請導師針對剛才的主題再多分享一些，謝謝。"));
                        quickReplyButtons.Add(new isRock.LineBot.QuickReplyTextAction("適合的家庭活動", "請問導師針對這個話題，有什麼適合在家與孩子一起做的活動嗎？"));
                    }

                    string tokenInfo = totalTokens > 0 ? $"消耗：{totalTokens} tokens" : "（來自快取）";
                    string finalReply = $"{displayMsg}\n\n次數：{currentCount}/500 | {tokenInfo}";

                    this.ReplyMessage(lineEvent.replyToken, finalReply, quickReplyButtons);
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"======= [WebHook 崩潰] =======\n{ex}");
                return Ok();
            }
        }
    }
}
