using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YouTubeChatter
{
  public class YouTubeChatExtractor
  {
    private readonly HttpClient _http = new();
    private readonly TimeZoneInfo _eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public Action<List<Line>> OnProgress { get; set; }
    public Action OnCaughtUp { get; set; }
    public Action OnLiveStarted { get; set; }
    public Action OnLiveEnded { get; set; }

    public YouTubeChatExtractor()
    {
      _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    }

    /// <summary>
    /// Downloads chat from YouTube (auto-detect live vs replay)
    /// </summary>
    public async Task<List<Line>> DownloadChatAsync(string url, CancellationToken token)
    {
      var videoId = ExtractVideoId(url);
      if (videoId == null)
        throw new ArgumentException("Invalid YouTube URL.");

      var html = await _http.GetStringAsync($"https://www.youtube.com/watch?v={videoId}");

      // Extract API key & client version
      var apiKey = Regex.Match(html, "\"INNERTUBE_API_KEY\":\"(.*?)\"").Groups[1].Value;
      var clientVersion = Regex.Match(html, "\"clientVersion\":\"(.*?)\"").Groups[1].Value;

      var isLive = DetectIsLive(html);

      // Get initial continuation
      var initialContinuation = ExtractInitialContinuation(html, isLive);
      if (string.IsNullOrEmpty(initialContinuation))
        throw new Exception("Could not find the continuation token.");

      var continuationQueue = new Queue<string>();
      continuationQueue.Enqueue(initialContinuation);

      var allMessages = new List<Line>();

      var filePath = GetCacheFilePath(videoId);
      if (isLive)
      {
        // Only load cache for ongoing live streams
        if (File.Exists(filePath))
        {
          var oldLines = GetOldMessages(filePath);
          allMessages.AddRange(oldLines);
          OnProgress?.Invoke(new List<Line>(allMessages));
        }
      }
      else
      {
        // Replay — ignore cache to get full chat
        if (File.Exists(filePath))
          File.Delete(filePath); // optional: clear old partial file
      }

      var caughtUp = false;
      var liveStartedFired = false;

      while (continuationQueue.Count > 0)
      {
        if (token.IsCancellationRequested)
          break;

        var continuation = continuationQueue.Dequeue();

        var endpoint = isLive
            ? $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={apiKey}"
            : $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat_replay?key={apiKey}";

        var payload = new
        {
          context = new
          {
            client = new
            {
              clientName = "WEB",
              clientVersion
            }
          },
          continuation
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        var response = await _http.PostAsync(endpoint, new StringContent(jsonPayload, Encoding.UTF8, "application/json"), token);
        var body = await response.Content.ReadAsStringAsync(token);

        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("continuationContents", out var cc))
          continue;

        var liveChatContinuation = cc.GetProperty(isLive ? "liveChatContinuation" : "liveChatContinuation");

        // Fire live started once
        if (isLive && !liveStartedFired)
        {
          OnLiveStarted?.Invoke();
          liveStartedFired = true;
        }

        var startCount = allMessages.Count;

        // Parse actions
        if (liveChatContinuation.TryGetProperty("actions", out var actions))
        {
          foreach (var action in actions.EnumerateArray())
          {
            ParseAction(action, allMessages, caughtUp);
            foreach (var cont in ExtractContinuations(action))
              continuationQueue.Enqueue(cont);
          }
        }

        // Top-level continuations
        if (liveChatContinuation.TryGetProperty("continuations", out var continuations))
        {
          foreach (var c in continuations.EnumerateArray())
            foreach (var tokenStr in ExtractTokenFromContinuation(c))
              continuationQueue.Enqueue(tokenStr);
        }

        var newCount = allMessages.Count - startCount;

        if (newCount > 0)
        {
          var newMessages = allMessages.GetRange(startCount, newCount);
          OnProgress?.Invoke(newMessages);

          if (isLive)
            AppendToFile(filePath, newMessages);
        }

        // Live: enable caught-up after first batch
        if (isLive && !caughtUp)
        {
          caughtUp = true;
          OnCaughtUp?.Invoke();
        }

        // Detect if live stream has ended
        if (isLive)
        {
          var liveEnded = false;

          if (liveChatContinuation.TryGetProperty("continuations", out var conts))
          {
            foreach (var c in conts.EnumerateArray())
            {
              if (c.TryGetProperty("liveChatEndedContinuationData", out _))
              {
                liveEnded = true;
                break;
              }
            }
          }
          else if (continuationQueue.Count == 0)
          {
            liveEnded = true;
          }

          if (liveEnded)
          {
            OnLiveEnded?.Invoke();
            break;
          }
        }

        await Task.Delay(400, token);
      }

      return allMessages;
    }

    private static List<Line> GetOldMessages(string filePath)
    {
      var oldMessages = File.ReadAllLines(filePath);
      var result = new List<Line>();
      foreach(var message in oldMessages)
      {
        var authorStart = message.IndexOf('@');
        var delimIndx = message.IndexOf(':', authorStart);
        var timeAuthor = message.Substring(0, delimIndx);
        if (!result.Any(m => m.TimeAuthor == timeAuthor))
        {
          var msg = message.Substring(delimIndx + 1);
          result.Add(new Line(timeAuthor, msg.Trim()));
        }
      }

      return result;
    }

    private static string ExtractVideoId(string url)
    {
      var match = Regex.Match(url, @"(?:youtu\.be/|v=)([a-zA-Z0-9_-]{11})");
      return match.Success ? match.Groups[1].Value : null;
    }

    private static bool DetectIsLive(string html)
    {
      var match = Regex.Match(html, @"ytInitialPlayerResponse\s*=\s*(\{.*?\});");
      if (!match.Success)
        return false;

      var json = match.Groups[1].Value;
      using var doc = JsonDocument.Parse(json);

      if (doc.RootElement.TryGetProperty("videoDetails", out var details))
      {
        if (details.TryGetProperty("isLiveContent", out var isLiveProp))
          return isLiveProp.GetBoolean();
      }

      return false;
    }

    private static string ExtractInitialContinuation(string html, bool isLive)
    {
      var match = Regex.Match(html, @"ytInitialData\s*=\s*(\{.*?\});");
      if (!match.Success)
        return null;

      var json = match.Groups[1].Value;
      using var doc = JsonDocument.Parse(json);

      try
      {
        var root = doc.RootElement;
        var cont = root.GetProperty("contents")
                       .GetProperty("twoColumnWatchNextResults")
                       .GetProperty("conversationBar")
                       .GetProperty("liveChatRenderer")
                       .GetProperty("continuations")[0]
                       .GetProperty("reloadContinuationData")
                       .GetProperty("continuation")
                       .GetString();
        return cont;
      }
      catch
      {
        return null;
      }
    }

    private void ParseAction(JsonElement action, List<Line> allMessages, bool caughtUp)
    {
      if (action.TryGetProperty("addChatItemAction", out var add))
      {
        if (add.TryGetProperty("item", out var item))
          ParseMessage(item, allMessages, caughtUp);
      }
      else if (action.TryGetProperty("replayChatItemAction", out var replay))
      {
        if (replay.TryGetProperty("actions", out var replayActions))
        {
          foreach (var a in replayActions.EnumerateArray())
          {
            if (a.TryGetProperty("addChatItemAction", out var add2))
            {
              if (add2.TryGetProperty("item", out var item))
                ParseMessage(item, allMessages, caughtUp);
            }
          }
        }
      }
    }

    private void ParseMessage(JsonElement item, List<Line> allMessages, bool caughtUp)
    {
      if (!item.TryGetProperty("liveChatTextMessageRenderer", out var msg))
        return;

      var author = msg.GetProperty("authorName").GetProperty("simpleText").GetString();
      var tsUsec = long.Parse(msg.GetProperty("timestampUsec").GetString());
      var utc = DateTimeOffset.FromUnixTimeMilliseconds(tsUsec / 1000).UtcDateTime;
      var eastern = TimeZoneInfo.ConvertTimeFromUtc(utc, _eastern);

      var runs = msg.GetProperty("message").GetProperty("runs");
      var sb = new StringBuilder();
      foreach (var run in runs.EnumerateArray())
        if (run.TryGetProperty("text", out var t))
          sb.Append(t.GetString());

      var timeAuthor = $"{eastern:yyyy-MM-dd HH:mm:ss} {author}";
      var add = caughtUp || !allMessages.Any(m => m.TimeAuthor == timeAuthor);
      if (add)
        allMessages.Add(new Line(timeAuthor, sb.ToString()));
    }

    private IEnumerable<string> ExtractContinuations(JsonElement action)
    {
      var results = new List<string>();
      void TryAdd(JsonElement elem, string prop)
      {
        if (elem.TryGetProperty(prop, out var cont))
          if (cont.TryGetProperty("continuation", out var token))
            results.Add(token.GetString());
      }

      TryAdd(action, "liveChatReplayContinuationData");
      TryAdd(action, "replayChatItemAction");
      TryAdd(action, "invalidationContinuationData");
      TryAdd(action, "liveChatContinuationData");
      return results;
    }

    private IEnumerable<string> ExtractTokenFromContinuation(JsonElement cont)
    {
      var results = new List<string>();
      void TryAdd(JsonElement e, string prop)
      {
        if (e.TryGetProperty(prop, out var c))
          if (c.TryGetProperty("continuation", out var token))
            results.Add(token.GetString());
      }

      TryAdd(cont, "liveChatReplayContinuationData");
      TryAdd(cont, "invalidationContinuationData");
      TryAdd(cont, "liveChatContinuationData");
      return results;
    }

    private string GetCacheFilePath(string videoId)
    {
      var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChatCache");
      if (!Directory.Exists(folder))
        Directory.CreateDirectory(folder);

      return Path.Combine(folder, $"{videoId}.txt");
    }

    private void AppendToFile(string path, List<Line> messages)
    {
      var lines = messages.Select(m => m.ToString());
      File.AppendAllLines(path, lines);
    }
  }
}