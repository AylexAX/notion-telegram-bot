using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// ============================================================
//  الإعدادات: عدّل الأرقام دي براحتك
// ============================================================
static class Cfg
{
    public const string TimeZoneId = "Africa/Cairo";
    public const int DayStartHour = 5;          // اليوم بيخلص الساعة 5 الفجر
    public const double MorningAt = 12;         // رسالة الصبح: 12 = 12 ظهرًا
    public const double NightAt = 24;           // رسالة الليل: 24 = 12 بالليل
    public const double ReminderEveryHours = 4; // "وصلت فين؟" كل كام ساعة

    // أسماء الـ properties في نوشن (لازم تتطابق بالحرف)
    public const string PropDate = "Date";
    public const string PropDone = "DONE";
    public const string PropNotes = "NOTES";
}

// ============================================================
//  نصوص الرسائل (HTML)
// ============================================================
static class Msg
{
    public const string Line = "━━━━━━━━━━━━━━";

    public static string Morning(int n) =>
        $"🌅 <b>صباح الخير يا فنان!</b>\nمتنساش مهام النهارده 💪\n{Line}\n📋 عندك <b>{n}</b> {(n == 1 ? "مهمة" : "مهام")} النهارده";

    public const string NoTasks =
        "☀️ <b>صباح الخير يا فنان!</b>\nالنهارده مفيش مهام اتضافت.\nاتأكد لو عايز تضيف مهام في نوشن 📝";

    public const string NewTask = "🆕 <b>في مهمة جديدة اتضافت!</b>";

    public static string Reminder(int done, int total) =>
        $"⏰ <b>ها وصلت فين؟</b>\n{Bar(done, total)} {done}/{total}\nالمتبقي 👇";

    public static string AllDone() =>
        "🎉 <b>عاش يا فنان!</b>\nخلصت كل مهام النهارده 🙌\nروح ارتاح أو اتفرج على فيلم 🍿";

    public static string Night(int done, int total) =>
        $"🌙 <b>لسه ماخلصتش المهام ليه؟</b>\n{Bar(done, total)} {done}/{total}\nروح انجز بسرعة قبل اليوم ما يخلص 🔥";

    static string Bar(int done, int total)
    {
        int filled = total == 0 ? 0 : done * 10 / total;
        return new string('▓', filled) + new string('░', 10 - filled);
    }
}

// ============================================================
//  الموديلات
// ============================================================
record TaskItem(string Id, string Title, string Icon, bool Done,
                DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Notes);

class CardInfo
{
    public long MessageId { get; set; }
    public bool Done { get; set; }
}

class State
{
    public string Day { get; set; } = "";
    public bool MorningSent { get; set; }
    public bool AllDoneSent { get; set; }
    public bool NightSent { get; set; }
    public DateTimeOffset LastReminder { get; set; }
    public Dictionary<string, CardInfo> Cards { get; set; } = new();
}

// ============================================================
//  البرنامج
// ============================================================
class Program
{
    static readonly HttpClient Http = new();
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string NotionToken = Env("NOTION_TOKEN");
    static readonly string DbId = Env("NOTION_DB_ID");
    static readonly string TgToken = Env("TELEGRAM_TOKEN");
    static readonly string ChatId = Env("TELEGRAM_CHAT_ID");
    static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById(Cfg.TimeZoneId);
    const string StatePath = "state.json";

    static string Env(string k) =>
        Environment.GetEnvironmentVariable(k) ?? throw new Exception($"Missing env var: {k}");

    static async Task Main()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz);
        var dayDate = now.AddHours(-Cfg.DayStartHour).Date;   // تاريخ "يوم البوت"
        var dayKey = dayDate.ToString("yyyy-MM-dd");

        var st = LoadState();
        if (st.Day != dayKey) st = new State { Day = dayKey };

        try
        {
            // 1) ضغطات الأزرار اللي جت من آخر تشغيلة
            await ProcessPresses();

            // 2) مهام النهارده من نوشن
            var winStart = At(dayDate, Cfg.DayStartHour);
            var winEnd = winStart.AddDays(1);
            var tasks = await FetchTasks(winStart, winEnd);
            int total = tasks.Count;

            // 3) لو غيّرت DONE من نوشن مباشرة، حدّث شكل الزرار
            await SyncButtons(st, tasks);

            var pending = tasks.Where(t => !t.Done).ToList();
            int done = total - pending.Count;
            var morningTime = At(dayDate, Cfg.MorningAt);
            var nightTime = At(dayDate, Cfg.NightAt);

            // 4) رسالة الصبح
            if (!st.MorningSent && now >= morningTime)
            {
                if (total == 0)
                {
                    await SendText(Msg.NoTasks);
                }
                else
                {
                    await SendText(Msg.Morning(total));
                    for (int i = 0; i < total; i++) await SendCard(st, tasks[i], i + 1, total);
                }
                st.MorningSent = true;
                st.LastReminder = now;
                st.AllDoneSent = pending.Count == 0;
            }
            // 5) مهام جديدة بعد رسالة الصبح
            else if (st.MorningSent)
            {
                foreach (var t in tasks.Where(t => !st.Cards.ContainsKey(t.Id)).ToList())
                {
                    await SendText(Msg.NewTask);
                    await SendCard(st, t, tasks.IndexOf(t) + 1, total);
                    st.AllDoneSent = false;
                }
            }

            // 6) خلصت كل المهام
            if (st.MorningSent && total > 0)
            {
                if (pending.Count == 0 && !st.AllDoneSent)
                {
                    await SendText(Msg.AllDone());
                    st.AllDoneSent = true;
                }
                else if (pending.Count > 0)
                {
                    st.AllDoneSent = false;
                }
            }

            // 7) رسالة آخر الليل
            bool sentNight = false;
            if (st.MorningSent && !st.NightSent && now >= nightTime)
            {
                st.NightSent = true;
                if (pending.Count > 0)
                {
                    await SendText(Msg.Night(done, total));
                    await ResendPending(st, tasks, pending);
                    st.LastReminder = now;
                    sentNight = true;
                }
            }

            // 8) "وصلت فين؟" كل كام ساعة
            if (!sentNight && st.MorningSent && pending.Count > 0 &&
                now >= st.LastReminder.AddHours(Cfg.ReminderEveryHours))
            {
                await SendText(Msg.Reminder(done, total));
                await ResendPending(st, tasks, pending);
                st.LastReminder = now;
            }
        }
        finally
        {
            SaveState(st);
        }
    }

    // ---------------- الأزرار ----------------
    static async Task ProcessPresses()
    {
        var updates = (await Tg("getUpdates", new JsonObject
        {
            ["timeout"] = 0,
            ["allowed_updates"] = new JsonArray("callback_query")
        })).AsArray();
        if (updates.Count == 0) return;

        var counts = new Dictionary<string, int>();
        var queryIds = new List<string>();
        long last = 0;

        foreach (var u in updates)
        {
            last = Math.Max(last, u!["update_id"]!.GetValue<long>());
            var cb = u["callback_query"];
            if (cb == null) continue;
            if (cb["message"]?["chat"]?["id"]?.GetValue<long>().ToString() != ChatId) continue; // أمان
            var data = cb["data"]?.GetValue<string>() ?? "";
            if (!data.StartsWith("t|")) continue;
            var id = data[2..];
            counts[id] = counts.GetValueOrDefault(id) + 1;
            queryIds.Add(cb["id"]!.GetValue<string>());
        }

        // عدد فردي من الضغطات = تغيير. عدد زوجي = ضغطت بالغلط ورجعت (مفيش تغيير)
        foreach (var (id, c) in counts.Where(kv => kv.Value % 2 == 1))
        {
            var page = await Notion(HttpMethod.Get, $"pages/{id}");
            bool cur = page["properties"]?[Cfg.PropDone]?["checkbox"]?.GetValue<bool>() ?? false;
            await Notion(new HttpMethod("PATCH"), $"pages/{id}", new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    [Cfg.PropDone] = new JsonObject { ["checkbox"] = !cur }
                }
            });
        }

        // تأكيد استلام التحديثات (عشان ماتتعالجش مرتين)
        await Tg("getUpdates", new JsonObject { ["offset"] = last + 1, ["timeout"] = 0, ["limit"] = 1 });

        foreach (var q in queryIds)
            await TgSafe("answerCallbackQuery", new JsonObject { ["callback_query_id"] = q });
    }

    static async Task SyncButtons(State st, List<TaskItem> tasks)
    {
        foreach (var t in tasks)
        {
            if (st.Cards.TryGetValue(t.Id, out var card) && card.Done != t.Done)
            {
                await TgSafe("editMessageReplyMarkup", new JsonObject
                {
                    ["chat_id"] = ChatId,
                    ["message_id"] = card.MessageId,
                    ["reply_markup"] = Kb(t)
                });
                card.Done = t.Done;
            }
        }
    }

    static JsonObject Kb(TaskItem t) => new()
    {
        ["inline_keyboard"] = new JsonArray(new JsonArray(new JsonObject
        {
            ["text"] = t.Done ? "✅ تمت" : "⬜ خلصتها",
            ["callback_data"] = "t|" + t.Id
        }))
    };

    // ---------------- إرسال الكروت ----------------
    static async Task ResendPending(State st, List<TaskItem> all, List<TaskItem> pending)
    {
        foreach (var t in pending) await SendCard(st, t, all.IndexOf(t) + 1, all.Count);
    }

    static async Task SendCard(State st, TaskItem t, int i, int n)
    {
        // شيل الزرار من الكارت القديم عشان مايبقاش في نسختين شغالين
        if (st.Cards.TryGetValue(t.Id, out var old))
        {
            await TgSafe("editMessageReplyMarkup", new JsonObject
            {
                ["chat_id"] = ChatId,
                ["message_id"] = old.MessageId,
                ["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() }
            });
        }

        var content = await GetContent(t.Id, t.Notes);
        var time = t.AllDay ? "طوال اليوم"
            : $"{t.Start.ToString("h:mm tt", Inv)} → {t.End.ToString("h:mm tt", Inv)}";
        var icon = string.IsNullOrEmpty(t.Icon) ? "📝" : t.Icon;

        var text =
            $"📌 <b>المهمة {i} من {n}</b>\n{Msg.Line}\n" +
            $"{icon} <b>{Esc(t.Title)}</b>\n🕔 {time}\n{Msg.Line}\n" +
            (content == "" ? "—" : content);

        var res = await Tg("sendMessage", new JsonObject
        {
            ["chat_id"] = ChatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = Kb(t)
        });
        st.Cards[t.Id] = new CardInfo { MessageId = res["message_id"]!.GetValue<long>(), Done = t.Done };
    }

    static Task SendText(string html) =>
        Tg("sendMessage", new JsonObject { ["chat_id"] = ChatId, ["text"] = html, ["parse_mode"] = "HTML" });

    // ---------------- نوشن ----------------
    static async Task<List<TaskItem>> FetchTasks(DateTimeOffset winStart, DateTimeOffset winEnd)
    {
        var result = new List<TaskItem>();
        string? cursor = null;
        do
        {
            var body = new JsonObject
            {
                ["page_size"] = 100,
                ["filter"] = new JsonObject
                {
                    ["property"] = Cfg.PropDate,
                    ["date"] = new JsonObject { ["on_or_after"] = winStart.AddDays(-2).ToString("yyyy-MM-dd") }
                }
            };
            if (cursor != null) body["start_cursor"] = cursor;

            var res = await Notion(HttpMethod.Post, $"databases/{DbId}/query", body);
            foreach (var page in res["results"]!.AsArray())
            {
                var t = ParseTask(page!);
                if (t != null && t.Start < winEnd && t.End >= winStart) result.Add(t);
            }
            cursor = res["has_more"]!.GetValue<bool>() ? res["next_cursor"]!.GetValue<string>() : null;
        } while (cursor != null);

        return result.OrderBy(t => t.Start).ToList();
    }

    static TaskItem? ParseTask(JsonNode page)
    {
        var props = page["properties"]!;
        var date = props[Cfg.PropDate]?["date"];
        if (date == null) return null;

        var tzName = date["time_zone"]?.GetValue<string>();
        var s = date["start"]!.GetValue<string>();
        var e = date["end"]?.GetValue<string>();
        bool allDay = s.Length <= 10;

        var start = ParseDate(s, tzName);
        var end = e != null ? ParseDate(e, tzName) : start;
        if (allDay) end = end.AddDays(1);

        string title = "";
        foreach (var kv in props.AsObject())
        {
            if (kv.Value!["type"]!.GetValue<string>() == "title")
            {
                title = PlainText(kv.Value["title"]);
                break;
            }
        }

        bool done = props[Cfg.PropDone]?["checkbox"]?.GetValue<bool>() ?? false;
        string notes = "";
        var notesProp = props[Cfg.PropNotes];
        if (notesProp?["type"]?.GetValue<string>() == "rich_text") notes = PlainText(notesProp["rich_text"]);

        var icon = page["icon"]?["emoji"]?.GetValue<string>() ?? "";
        return new TaskItem(page["id"]!.GetValue<string>(), title, icon, done, start, end, allDay, notes);
    }

    static DateTimeOffset ParseDate(string s, string? tzName)
    {
        if (s.Length <= 10)
            return At(DateTime.ParseExact(s, "yyyy-MM-dd", Inv), 0);

        if (Regex.IsMatch(s, @"(Z|[+-]\d\d:\d\d)$"))
            return TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(s, Inv), Tz);

        var zone = tzName != null ? TimeZoneInfo.FindSystemTimeZoneById(tzName) : Tz;
        var dt = DateTime.SpecifyKind(DateTime.Parse(s, Inv), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(dt, zone.GetUtcOffset(dt)), Tz);
    }

    static async Task<string> GetContent(string pageId, string notes)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(notes)) lines.Add(Esc(notes.Trim()));

        var res = await Notion(HttpMethod.Get, $"blocks/{pageId}/children?page_size=100");
        foreach (var b in res["results"]!.AsArray())
        {
            var type = b!["type"]!.GetValue<string>();
            var rt = b[type]?["rich_text"];
            if (rt == null) continue;
            var text = PlainText(rt);
            if (text == "") continue;

            lines.Add(type switch
            {
                "bulleted_list_item" => "• " + Esc(text),
                "numbered_list_item" => "▫️ " + Esc(text),
                "to_do" => (b[type]!["checked"]?.GetValue<bool>() == true ? "☑️ " : "☐ ") + Esc(text),
                "heading_1" or "heading_2" or "heading_3" => "<b>" + Esc(text) + "</b>",
                _ => Esc(text)
            });
        }

        // حد تليجرام 4096 حرف، فبنقص المحتوى لو طويل
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            if (sb.Length + l.Length > 2800) { sb.Append("…"); break; }
            sb.AppendLine(l);
        }
        return sb.ToString().Trim();
    }

    static string PlainText(JsonNode? richText) =>
        richText == null ? "" :
        string.Concat(richText.AsArray().Select(x => x!["plain_text"]?.GetValue<string>() ?? ""));

    // ---------------- مساعدات HTTP ----------------
    static async Task<JsonNode> Notion(HttpMethod method, string path, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, "https://api.notion.com/v1/" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NotionToken);
        req.Headers.Add("Notion-Version", "2022-06-28");
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"Notion {(int)resp.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    static async Task<JsonNode> Tg(string method, JsonNode body)
    {
        var resp = await Http.PostAsync($"https://api.telegram.org/bot{TgToken}/{method}",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(text)!;
        if (json["ok"]?.GetValue<bool>() != true) throw new Exception($"Telegram {method}: {text}");
        return json["result"]!;
    }

    // نسخة بتتجاهل الأخطاء (مثلاً: "message is not modified" أو ضغطة قديمة)
    static async Task TgSafe(string method, JsonNode body)
    {
        try { await Tg(method, body); }
        catch (Exception ex) { Console.WriteLine($"(ignored) {ex.Message}"); }
    }

    // ---------------- أدوات صغيرة ----------------
    static DateTimeOffset At(DateTime day, double hours)
    {
        var dt = DateTime.SpecifyKind(day.Date.AddHours(hours), DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, Tz.GetUtcOffset(dt));
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static State LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
        }
        catch (Exception ex) { Console.WriteLine($"State load failed: {ex.Message}"); }
        return new State();
    }

    static void SaveState(State st) =>
        File.WriteAllText(StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
}
