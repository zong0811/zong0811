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

    // --- 2. 對話歷史管理員 (記憶 5輪) ---
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
        private class CacheEntry { public string Result { get; set; } public DateTime ExpireTime { get; set; } }
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

    // --- 4. Gemini 服務 (v1.1 版：Token 統計 + 台灣時間) ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private const string ModelName = "gemini-3.1-flash-lite-preview";

        public static async Task<(string text, int tokens)> GetResponseAsync(string userId, string userQuery)
        {
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult))
                return (cachedResult, 0);

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={ApiKey}";
            string currentTimeInfo = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");

            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { 
                    parts = new[] { new { 
                        text = $"你是一位資深的教育人員。現在是台灣時間 {currentTimeInfo}。請用溫柔而堅定的語氣與使用者對話。" 
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
            catch (Exception) { return ("導師暫時切斷了與外界的聯繫。", 0); }
        }
    }

    // ---5. 新增：動畫管理員 ---
public static class LoadingAnimationManager
{
    public static async Task StartLoadingAsync(string accessToken, string userId, int seconds = 20)
    {
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(userId)) return;

        string url = "https://api.line.me/v2/bot/chat/loading/start";
        var requestBody = new { chatId = userId, loadingSeconds = seconds };

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            await client.PostAsync(url, content);
        }
        catch { /* 忽略動畫錯誤，確保不影響主流程 */ }
    }
}

// --- 6.LINE WebHook 控制器更新 ---
public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
{
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

                // 1. 檢查配額
                int currentCount = UsageManager.GetAndIncrementCount(out bool isOverLimit);
                if (isOverLimit) {
                    this.ReplyMessage(lineEvent.replyToken, "🌟 今日配額已滿。");
                    return Ok();
                }

                // 2. 啟動「動畫」 (這會讓使用者看到三個點在跳動)
                // 注意：這是一個非同步呼叫，我們不使用 await 等它完，直接往下跑 Gemini
                _ = LoadingAnimationManager.StartLoadingAsync(this.ChannelAccessToken, userId);

                // 3. 處理對話歷史與呼叫 Gemini
                ChatHistoryManager.AddMessage(userId, "user", userText);
                var (responseMsg, totalTokens) = await GeminiLLM.GetResponseAsync(userId, userText);
                ChatHistoryManager.AddMessage(userId, "assistant", responseMsg);

                // 4. 組合最終訊息
                string tokenInfo = totalTokens > 0 ? $"總計消耗：{totalTokens} tokens" : "（來自快取）";
                string finalReply = $"{responseMsg}\n\n次數：{currentCount}/500\n{tokenInfo}";

                // 5. 回覆訊息 (一旦回覆，LINE 會自動停止動畫)
                this.ReplyMessage(lineEvent.replyToken, finalReply);
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
