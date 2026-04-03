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
    // --- 1. 使用量管理員 (每日 500 次，下午 3 點重置) ---
    public static class UsageManager
    {
        private static int _todayCount = 0;
        private static DateTime _nextResetTime = GetNextResetTime();
        private static readonly object _lock = new object();

        private static DateTime GetNextResetTime()
        {
            DateTime now = DateTime.UtcNow.AddHours(8); // 確保以台灣時間計算
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
                    Console.WriteLine($">>> [系統狀態] 次數已重置。下次重置點：{_nextResetTime:yyyy/MM/dd HH:mm}");
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

    // --- 3. 搜尋快取管理員 (節省重複搜尋點數) ---
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

    // --- 4. Gemini 服務 (整合快取與台灣時區) ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "你的API金鑰";
        private const string ModelName = "gemini-3.1-flash-lite-preview";

        public static async Task<string> GetResponseAsync(string userId, string userQuery)
        {
            // A. 先檢查快取
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult))
            {
                Console.WriteLine($">>> [快取命中] 使用者問題: {userQuery}");
                return cachedResult;
            }

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={ApiKey}";
            DateTime twNow = DateTime.UtcNow.AddHours(8);
            string currentTimeInfo = twNow.ToString("yyyy年MM月dd日 dddd");

            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { 
                    parts = new[] { new { 
                        text = $"你是一位溫暖的華德福導師。現在是台灣時間 {currentTimeInfo}。請用溫柔且充滿生命力的語氣回答。" 
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

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"======= [Gemini API 錯誤] {response.StatusCode} =======");
                    return "導師正在沉思，請稍後再試。";
                }

                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                string textResult = result?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光，但現在無法言語。";

                // B. 存入快取
                SearchCacheManager.SetCache(userQuery, textResult);
                
                return textResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($">>> [HttpClient 異常]: {ex.Message}");
                return "導師暫時切斷了與外界的聯繫。";
            }
        }
    }

    // --- 5. LINE WebHook 控制器 ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        // 支援 Better Stack 監控 (GET & HEAD)
        [HttpHead]
        [HttpGet]
        [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get()
        {
            return Ok("Bot is Alive!");
        }

        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST()
        {
            try
            {
                this.ChannelAccessToken = Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN");
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                
                if (lineEvent == null || lineEvent.replyToken == "00000000000000000000000000000000") return Ok();

                if (lineEvent.type.ToLower() == "message" && lineEvent.message.type == "text")
                {
                    string userId = lineEvent.source.userId;
                    string userText = lineEvent.message.text;

                    // 1. 檢查次數
                    int currentCount = UsageManager.GetAndIncrementCount(out bool isOverLimit);
                    if (isOverLimit) 
                    {
                        this.ReplyMessage(lineEvent.replyToken, "🌟 今日的智慧分享已達 500 次上限，請等待下午三點靈魂甦醒後再會。");
                        return Ok();
                    }

                    // 2. 處理對話
                    ChatHistoryManager.AddMessage(userId, "user", userText);
                    
                    // 傳入 userId 與 userText 以利快取判定
                    string responseMsg = await GeminiLLM.GetResponseAsync(userId, userText);
                    
                    ChatHistoryManager.AddMessage(userId, "assistant", responseMsg);

                    // 3. 回覆 LINE
                    this.ReplyMessage(lineEvent.replyToken, $"{responseMsg}\n\n次數記錄：{currentCount}/500");
                }
                return Ok();
            }
            catch (Exception ex)
            {
                Console.WriteLine("======= [WebHook 控制器崩潰] =======");
                Console.WriteLine(ex.ToString());
                return Ok();
            }
        }
    }
}