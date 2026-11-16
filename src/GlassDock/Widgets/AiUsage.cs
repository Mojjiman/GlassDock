using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GlassDock.Config;
using GlassDock.Logging;

namespace GlassDock.Widgets;

/// <summary>One window of a plan limit: the share used (0-100) and when it starts over.</summary>
public sealed record LimitWindow(double UsedPercent, DateTimeOffset? ResetsAt)
{
    /// <summary>Past its reset time the window has started over, whatever was last read.</summary>
    public double Current => ResetsAt is { } r && r <= DateTimeOffset.Now ? 0 : UsedPercent;
    public bool Expired => ResetsAt is { } r && r <= DateTimeOffset.Now;
}

public sealed record AiUsageSnapshot(
    LimitWindow? ClaudeSession, LimitWindow? ClaudeWeek, string? ClaudeNote,
    long ClaudeTokensSession, long ClaudeTokensToday,
    LimitWindow? CodexSession, LimitWindow? CodexWeek, DateTimeOffset? CodexSeen);

/// <summary>
/// Reads AI usage for every AI widget tile (one shared instance, since tiles are rebuilt with the dock):
/// <list type="bullet">
/// <item>Claude plan limits from the OAuth usage endpoint, with the token Claude Code's CLI keeps in
/// ~/.claude/.credentials.json (read only, never refreshed or written here).</item>
/// <item>Claude Code tokens from its transcripts (~/.claude/projects/**/*.jsonl), read incrementally.</item>
/// <item>Codex plan limits from the rate_limits Codex writes into its session logs.</item>
/// </list>
/// </summary>
public sealed class AiUsageTracker
{
    private static readonly object SharedGate = new();
    private static AiUsageTracker? _shared;
    private static int _users;
    private static Timer? _dropTimer;

    /// <summary>The live tracker; only read by holders of a lease from <see cref="Acquire"/>.</summary>
    public static AiUsageTracker Shared
    {
        get { lock (SharedGate) return _shared ??= new(); }
    }

    /// <summary>
    /// Each AI widget and its flyout hold a lease while they exist. Nothing here runs on its own: without a
    /// widget there are no file scans and no calls to the usage endpoint. Once the last lease is gone for
    /// <c>AiUsage.ReleaseDelayMs</c> the tracker and its caches are dropped; the delay rides out dock
    /// rebuilds, which dispose a widget and create it again moments later.
    /// </summary>
    public static IDisposable Acquire()
    {
        lock (SharedGate)
        {
            _users++;
            _dropTimer?.Dispose();
            _dropTimer = null;
        }
        return new Lease();
    }

    private static void Release()
    {
        lock (SharedGate)
        {
            if (--_users > 0) return;
            _dropTimer?.Dispose();
            _dropTimer = new Timer(_ => Drop(), null, AppConstants.AiUsage.ReleaseDelayMs, Timeout.Infinite);
        }
    }

    private static void Drop()
    {
        lock (SharedGate)
        {
            if (_users > 0 || _shared == null) return;
            _shared = null;
            _dropTimer?.Dispose();
            _dropTimer = null;
        }
        Log.Info("AI usage tracking stopped: no AI usage widget in the dock");
    }

    private sealed class Lease : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) Release(); }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(AppConstants.AiUsage.HttpTimeoutMs) };
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly object _refreshGate = new();
    private volatile AiUsageSnapshot _snapshot = new(null, null, null, 0, 0, null, null, null);
    private DateTime _nextTranscripts, _nextClaude, _nextCodex;

    public AiUsageSnapshot Snapshot => _snapshot;

    /// <summary>Brings due sources up to date. Runs on a pool thread; concurrent callers wait for one pass.</summary>
    public void Refresh(bool force = false)
    {
        lock (_refreshGate)
        {
            var now = DateTime.UtcNow;
            // Claude first: its reset time tells the transcripts where the current 5-hour window began.
            if (now >= _nextClaude || (force && _claudeFailures == 0)) FetchClaudeLimits();
            if (now >= _nextTranscripts || force)
            {
                ScanTranscripts();
                _nextTranscripts = now.AddMilliseconds(AppConstants.AiUsage.TranscriptScanMs);
            }
            if (now >= _nextCodex || force)
            {
                ReadCodex();
                _nextCodex = now.AddMilliseconds(AppConstants.AiUsage.CodexScanMs);
            }
            _snapshot = new AiUsageSnapshot(_claudeSession, _claudeWeek, _claudeNote, _tokensSession, _tokensToday,
                _codexSession, _codexWeek, _codexSeen);
        }
    }

    // ---- Claude plan limits -----------------------------------------------------------------------

    private LimitWindow? _claudeSession, _claudeWeek;
    private string? _claudeNote;
    private int _claudeFailures;
    private DateTime? _rejectedCredentials;

    private void FetchClaudeLimits()
    {
        _nextClaude = DateTime.UtcNow.AddMilliseconds(AppConstants.AiUsage.ClaudeLimitsPollMs);
        var path = Path.Combine(Home, ".claude", ".credentials.json");
        try
        {
            if (!File.Exists(path)) { _claudeNote = "한도: 터미널에서 claude 로그인 필요"; return; }
            var written = File.GetLastWriteTimeUtc(path);
            // A rejected token stays rejected; wait until Claude Code writes new credentials.
            if (_rejectedCredentials == written) return;
            string? token = null;
            long expiresMs = 0;
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                if (doc.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.ValueKind == JsonValueKind.Object)
                {
                    if (o.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String) token = t.GetString();
                    if (o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number) e.TryGetInt64(out expiresMs);
                }
            }
            if (string.IsNullOrEmpty(token)) { _claudeNote = "한도: 터미널에서 claude 로그인 필요"; return; }
            if (expiresMs > 0 && DateTimeOffset.FromUnixTimeMilliseconds(expiresMs) <= DateTimeOffset.UtcNow)
            {
                _claudeNote = "한도: 로그인 만료, 터미널에서 claude를 한 번 실행하면 갱신";
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, AppConstants.AiUsage.ClaudeUsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", AppConstants.AiUsage.ClaudeOauthBeta);
            using var response = Http.Send(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _claudeFailures++;
                int wait = (int)Math.Min((double)AppConstants.AiUsage.ClaudeLimitsPollMs * (1 << Math.Min(_claudeFailures, 8)), AppConstants.AiUsage.ClaudeLimitsBackoffMaxMs);
                _nextClaude = DateTime.UtcNow.AddMilliseconds(wait);
                Log.Warn($"Claude usage endpoint rate-limited us; next try in {wait / 60000} min");
                return;
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _rejectedCredentials = written;
                _claudeNote = "한도: 로그인 거부됨, 터미널에서 claude 다시 로그인";
                Log.Warn($"Claude usage endpoint refused the stored login ({(int)response.StatusCode})");
                return;
            }
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(response.Content.ReadAsStream());
            _claudeSession = ParseClaudeWindow(body.RootElement, "five_hour");
            _claudeWeek = ParseClaudeWindow(body.RootElement, "seven_day");
            _claudeNote = _claudeSession == null && _claudeWeek == null ? "한도: 이 요금제는 사용량을 알려 주지 않음" : null;
            _claudeFailures = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or TaskCanceledException)
        {
            _claudeFailures++;
            Log.Warn("Reading Claude plan limits failed", ex);
        }
    }

    private static LimitWindow? ParseClaudeWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) return null;
        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r))
        {
            if (r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) resets = at;
            else if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out long secs)) resets = DateTimeOffset.FromUnixTimeSeconds(secs);
        }
        return new LimitWindow(u.GetDouble(), resets);
    }

    // ---- Claude Code transcripts ------------------------------------------------------------------

    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by message id + request id: one reply is written as several lines (one per content block).
    private readonly Dictionary<string, (DateTimeOffset At, long Tokens)> _messages = [];
    private long _tokensSession, _tokensToday;

    private void ScanTranscripts()
    {
        var root = Path.Combine(Home, ".claude", "projects");
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(DateTime.Today);
        var rolling = now.AddHours(-AppConstants.AiUsage.ClaudeSessionHours);
        var keepFrom = today < rolling ? today : rolling;
        try
        {
            if (Directory.Exists(root))
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < keepFrom.UtcDateTime) continue;
                        ReadNewLines(file, keepFrom);
                    }
                    catch (IOException ex) { Log.Debug($"Transcript {Path.GetFileName(file)} skipped: {ex.Message}"); }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Scanning Claude Code transcripts failed", ex);
        }

        foreach (var stale in _messages.Where(kv => kv.Value.At < keepFrom).Select(kv => kv.Key).ToList()) _messages.Remove(stale);
        // The plan's 5-hour window starts at its first message; the reset time pins it down when known.
        var sessionStart = _claudeSession is { ResetsAt: { } reset } && reset > now ? reset.AddHours(-AppConstants.AiUsage.ClaudeSessionHours) : rolling;
        _tokensSession = _messages.Values.Where(m => m.At >= sessionStart).Sum(m => m.Tokens);
        _tokensToday = _messages.Values.Where(m => m.At >= today).Sum(m => m.Tokens);
    }

    /// <summary>Reads the complete lines appended since the last pass; a half-written last line waits for the next.</summary>
    private void ReadNewLines(string file, DateTimeOffset keepFrom)
    {
        long offset = _offsets.GetValueOrDefault(file);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < offset) offset = 0; // rewritten
        if (fs.Length == offset) return;
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[AppConstants.AiUsage.TranscriptBufferBytes];
        long bufferPos = offset;
        int filled = 0;
        while (true)
        {
            if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // a line longer than the buffer
            int n = fs.Read(buffer, filled, buffer.Length - filled);
            if (n == 0) break;
            int end = filled + n, start = 0;
            for (int i = filled; i < end; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                ParseLine(buffer.AsMemory(start, i - start), keepFrom);
                start = i + 1;
            }
            Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
            bufferPos += start;
            filled = end - start;
        }
        _offsets[file] = bufferPos;
    }

    private void ParseLine(ReadOnlyMemory<byte> line, DateTimeOffset keepFrom)
    {
        var span = line.Span;
        if (span.IndexOf("\"usage\""u8) < 0 || span.IndexOf("\"assistant\""u8) < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant") return;
            if (!root.TryGetProperty("timestamp", out var ts) || !ts.TryGetDateTimeOffset(out var at) || at < keepFrom) return;
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var usage)) return;
            // Cache reads are left out: they repeat the whole context on every turn and would drown the rest.
            long tokens = Num(usage, "input_tokens") + Num(usage, "output_tokens") + Num(usage, "cache_creation_input_tokens");
            string key = $"{Str(msg, "id")}:{Str(root, "requestId")}";
            if (!_messages.TryGetValue(key, out var seen) || seen.Tokens < tokens) _messages[key] = (at, tokens);
        }
        catch (JsonException ex) { Log.Debug($"Transcript line skipped: {ex.Message}"); }
    }

    private static long Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x : 0;

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- Codex plan limits ------------------------------------------------------------------------

    private LimitWindow? _codexSession, _codexWeek;
    private DateTimeOffset? _codexSeen;
    private (string Path, DateTime Written) _codexFile;

    private void ReadCodex()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Home, ".codex");
        var dir = Path.Combine(home, "sessions");
        try
        {
            if (!Directory.Exists(dir)) return;
            var newest = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(AppConstants.AiUsage.CodexFilesToScan);
            foreach (var f in newest)
            {
                if (_codexFile == (f.FullName, f.LastWriteTimeUtc)) return; // nothing new since the last read
                if (ReadCodexFile(f.FullName))
                {
                    _codexFile = (f.FullName, f.LastWriteTimeUtc);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Reading Codex session logs failed", ex);
        }
    }

    /// <summary>Finds the last rate_limits record near the end of a session log.</summary>
    private bool ReadCodexFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long from = Math.Max(0, fs.Length - AppConstants.AiUsage.CodexTailBytes);
        fs.Seek(from, SeekOrigin.Begin);
        var bytes = new byte[fs.Length - from];
        fs.ReadExactly(bytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("rate_limits", out var limits)
                    || limits.ValueKind != JsonValueKind.Object) continue;
                DateTimeOffset? at = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var t) ? t : null;
                _codexSession = ParseCodexWindow(limits, "primary", at);
                _codexWeek = ParseCodexWindow(limits, "secondary", at);
                _codexSeen = at;
                return true;
            }
            catch (JsonException ex) { Log.Debug($"Codex log line skipped: {ex.Message}"); }
        }
        return false;
    }

    private static LimitWindow? ParseCodexWindow(JsonElement limits, string name, DateTimeOffset? at)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var u) || u.ValueKind != JsonValueKind.Number) return null;
        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out long epoch))
            resets = DateTimeOffset.FromUnixTimeSeconds(epoch);
        // Older Codex versions wrote a countdown instead of a time.
        else if (at != null && w.TryGetProperty("resets_in_seconds", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long secs))
            resets = at.Value.AddSeconds(secs);
        return new LimitWindow(u.GetDouble(), resets);
    }
}
