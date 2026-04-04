using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace isRock.Template
{
    // --- 1. 使用量管理員 (維持 V1.1 核心) ---
    public static class UsageManager
    {
        private static int _todayCount = 0;
        private static DateTime _nextResetTime = GetNextResetTime();
        private static readonly object _lock = new object();
        private static DateTime GetNextResetTime() {
            DateTime now = DateTime.UtcNow.AddHours(8); 
            DateTime resetToday = new DateTime(now.Year, now.Month, now.Day, 15, 0, 0);
            return now < resetToday ? resetToday : resetToday.AddDays(1);
        }
        public static int GetAndIncrementCount(out bool isOverLimit) {
            lock (_lock) {
                if (DateTime.UtcNow.AddHours(8) >= _nextResetTime) { _todayCount = 0; _nextResetTime = GetNextResetTime(); }
                isOverLimit = _todayCount >= 500;
                if (!isOverLimit) _todayCount++;
                return _todayCount;
            }
        }
    }

    // --- 2. 對話歷史管理員 (記憶 5 輪) ---
    public static class ChatHistoryManager
    {
        private static readonly ConcurrentDictionary<string, List<object>> _history = new ConcurrentDictionary<string, List<object>>();
        private const int MaxHistory = 5; 
        public static List<object> GetHistory(string userId) => _history.GetOrAdd(userId, _ => new List<object>());
        public static void AddMessage(string userId, string role, string content) {
            var userHistory = GetHistory(userId);
            string geminiRole = role.ToLower() == "assistant" ? "model" : "user";
            userHistory.Add(new { role = geminiRole, parts = new[] { new { text = content } } });
            if (userHistory.Count > (MaxHistory * 2)) userHistory.RemoveAt(0);
        }
    }

    // --- 3. 搜尋快取管理員 ---
    public static class SearchCacheManager
    {
        private class CacheEntry { public string Result { get; set; } = ""; public DateTime ExpireTime { get; set; } }
        private static readonly ConcurrentDictionary<string, CacheEntry> _searchCache = new ConcurrentDictionary<string, CacheEntry>();
        public static bool TryGetCache(string query, out string result) {
            if (_searchCache.TryGetValue(query, out var entry)) {
                if (DateTime.Now < entry.ExpireTime) { result = entry.Result; return true; }
                _searchCache.TryRemove(query, out _);
            }
            result = ""; return false;
        }
        public static void SetCache(string query, string result) => _searchCache[query] = new CacheEntry { Result = result, ExpireTime = DateTime.Now.AddMinutes(30) };
    }

    // --- 4. Gemini 服務 (強化建議按鈕指令) ---
    public static class GeminiLLM
    {
        private static string ApiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        private const string ModelName = "gemini-3.1-flash-lite-preview";
        public static async Task<(string text, int tokens)> GetResponseAsync(string userId, string userQuery) {
            if (SearchCacheManager.TryGetCache(userQuery, out string cachedResult)) return (cachedResult, 0);
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:generateContent?key={ApiKey}";
            string time = DateTime.UtcNow.AddHours(8).ToString("yyyy/MM/dd dddd HH:mm");
            var requestBody = new {
                contents = ChatHistoryManager.GetHistory(userId),
                systemInstruction = new { parts = new[] { new { text = $"你是一位資深的華德福導師。現在是台灣時間 {time}。回覆結束後請換行輸入 '|' 符號，並提供 3 個建議追問標題（每個 10 字內），用逗號隔開。" } } },
                generationConfig = new { maxOutputTokens = 1500, temperature = 0.7 }
            };
            using var client = new HttpClient();
            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);
            var jsonRes = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) return ("導師正在沉思...", 0);
            dynamic? res = JsonConvert.DeserializeObject(jsonRes);
            string text = res?.candidates?[0]?.content?.parts?[0]?.text ?? "我看見了光。";
            int tokens = res?.usageMetadata?.totalTokenCount ?? 0;
            SearchCacheManager.SetCache(userQuery, text);
            return (text, tokens);
        }
    }

    // --- 5. LINE WebHook 控制器 (終極穩定版：繞過 SDK 型別檢查) ---
    public class LineBotOpenAIWebHookController : isRock.LineBot.LineWebHookControllerBase
    {
        private string ChannelToken => Environment.GetEnvironmentVariable("LINE_CHANNEL_TOKEN") ?? "";

        [HttpHead] [HttpGet] [Route("api/LineBotOpenAIWebHook")]
        public IActionResult Get() => Ok("Bot is Alive!");

        [Route("api/LineBotOpenAIWebHook")]
        [HttpPost]
        public async Task<IActionResult> POST()
        {
            try {
                this.ChannelAccessToken = ChannelToken;
                var lineEvent = this.ReceivedMessage?.events?.FirstOrDefault();
                if (lineEvent == null || string.IsNullOrEmpty(lineEvent.replyToken)) return Ok();

                if (lineEvent.type?.ToLower() == "message" && lineEvent.message?.type == "text") {
                    string userId = lineEvent.source?.userId ?? "unknown";
                    string userText = lineEvent.message?.text ?? "";

                    // A. 動畫效果
                    _ = StartLoadingAnimation(userId, 5);

                    int count = UsageManager.GetAndIncrementCount(out bool isOverLimit);
                    if (isOverLimit) { this.ReplyMessage(lineEvent.replyToken, "🌟 今日配額已滿。"); return Ok(); }

                    ChatHistoryManager.AddMessage(userId, "user", userText);
                    var (raw, tokens) = await GeminiLLM.GetResponseAsync(userId, userText);
                    ChatHistoryManager.AddMessage(userId, "assistant", raw);

                    // B. 解析按鈕 (改用 dynamic 繞過 SDK 限制)
                    string displayMsg = raw;
                    var quickReplyItems = new List<object>();

                    if (raw.Contains("|")) {
                        var parts = raw.Split('|');
                        displayMsg = parts[0].Trim(); 
                        var suggs = parts[1].Split(new[] { ',', '，' });
                        foreach (var s in suggs) {
                            if (!string.IsNullOrWhiteSpace(s) && quickReplyItems.Count < 5)
                                quickReplyItems.Add(new { type = "action", action = new { type = "message", label = s.Trim(), text = s.Trim() } });
                        }
                    }

                    if (quickReplyItems.Count == 0) {
                        string[] defs = { "更短版本", "三年級難度", "活動延伸" };
                        foreach (var d in defs)
                            quickReplyItems.Add(new { type = "action", action = new { type = "message", label = d, text = d } });
                    }

                    string tokenInfo = tokens > 0 ? $"消耗：{tokens} tokens" : "（快取）";
                    string finalText = $"{displayMsg}\n\n次數：{count}/500 | {tokenInfo}";

                    // C. 關鍵：直接用 dynamic 賦值，不觸發 SDK 的型別檢查
                    dynamic replyObj = new isRock.LineBot.TextMessage(finalText);
                    replyObj.quickReply = new { items = quickReplyItems };

                    // 發送物件
                    this.ReplyMessage(lineEvent.replyToken, (isRock.LineBot.TextMessage)replyObj);
                }
                return Ok();
            } catch (Exception ex) { Console.WriteLine(ex.ToString()); return Ok(); }
        }

        private async Task StartLoadingAnimation(string userId, int seconds) {
            if (string.IsNullOrEmpty(userId) || userId == "unknown") return;
            try {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ChannelToken);
                var body = new { chatId = userId, loadingSeconds = seconds };
                await client.PostAsync("https://api.line.me/v2/bot/chat/loading/start", 
                    new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"));
            } catch { }
        }
    }
}
