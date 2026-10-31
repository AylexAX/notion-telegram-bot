using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

    // نصوص الأزرار (لازم الـ Worker يكون فيه نفس نص الحالة المعلّقة)
    public const string BtnDone = "DONE ☑️";
    public const string BtnCompleted = "COMPLETED ✅";
}

// ============================================================
//  نصوص الرسائل (HTML)
// ============================================================
static class Msg
{
    public const string Top = "╭━━━━━━━━━━━━╮";
    public const string Bot = "╰━━━━━━━━━━━━╯";

    // أرقام بخط عريض: 15 -> 𝟭𝟱
    static string B(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c >= '0' && c <= '9') sb.Append(char.ConvertFromUtf32(0x1D7EC + (c - '0')));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // رسالة الصبح: النسبة = اليوم ÷ عدد أيام الشهر
    public static string Morning(DateTime dayDate)
    {
        int dim = DateTime.DaysInMonth(dayDate.Year, dayDate.Month);
        int pct = (int)Math.Round(dayDate.Day * 100.0 / dim, MidpointRounding.AwayFromZero);
        int filled = (int)Math.Round(dayDate.Day * 10.0 / dim, MidpointRounding.AwayFromZero);
        var date = dayDate.ToString("d/M/yyyy", CultureInfo.InvariantCulture);

        return "┏━━━━━━━━━━━━━━━\n" +
               "┃ ☀️ 𝗚𝗢𝗢𝗗 𝗠𝗢𝗥𝗡𝗜𝗡𝗚 𝗔𝗿𝘁𝗶𝘀𝘁\n" +
               "┣━━━━━━━━━━━━━━━\n" +
               $"┃ 🗓 {B(date)}\n" +
               $"┃ ⏳ 𝗗𝗮𝘆 {Blocks(filled)} {B(pct.ToString())}%\n" +
               "┣━━━━━━━━━━━━━━━\n" +
               "┃ 🎯 𝗧𝗼𝗱𝗮𝘆'𝘀 𝗙𝗼𝗰𝘂𝘀 𝗼𝗻 𝗧𝗵𝗶𝘀 𝗧𝗮𝘀𝗸𝘀\n" +
               "┗━━━━━━━━━━━━━━━";
    }

    public const string NoTasks =
        Top + "\n☀️ GOOD MORNING!\n" + Bot + "\nالنهارده مفيش مهام اتضافت 📝\nضيف مهامك في نوشن وهتوصلك هنا";

    // إشعار عام لأي تغيير حصل (title لازم يكون معمول له Esc قبل ما يتبعت هنا)
    public static string Notice(string head, string text) =>
        $"{Top}\n{head}\n{Bot}\n{text}";

    public static string NewTask(string title) =>
        Notice("🆕 NEW TASK ADDED", $"خلي بالك، في مهمة انضافت:\n<b>{title}</b>");

    public static string Edited(string title) =>
        Notice("✏️ TASK UPDATED", $"خلي بالك، في مهمة اتعدّلت في نوشن:\n<b>{title}</b>");

    public static string Removed(string title) =>
        Notice("🗑️ TASK REMOVED", $"خلي بالك، مهمة اتشالت من مهام النهارده:\n<b>{title}</b>");

    public static string DoneInNotion(string title, bool done) =>
        done ? Notice("✅ DONE IN NOTION", $"علّمت المهمة تمت في نوشن:\n<b>{title}</b>")
             : Notice("⏳ REOPENED IN NOTION", $"رجّعت المهمة لسه ماخلصتش في نوشن:\n<b>{title}</b>");

    // المتبقي بعد أي تغيير
    public static string Remaining(int done, int total) =>
        $"{Top}\n📋 REMAINING TASKS\n{Bot}\n{Bar(done, total)} {done}/{total}\nالمتبقي 👇";

    // "عدّت 4 ساعات"
    public static string Reminder(int done, int total) =>
        $"{Top}\n⏰ CHECK-IN TIME\n{Bot}\nعدّت {Cfg.ReminderEveryHours} ساعات، خلصت أي TASK ولا لسا؟ 👀!!\n{Bar(done, total)} {done}/{total}\nالمتبقي 👇";

    public static string AllDone() =>
        $"{Top}\n🎉 ALL DONE!\n{Bot}\nعاش يا فنان، خلصت كل مهام النهارده 🙌\nروح ارتاح أو اتفرج على فيلم 🍿";

    // رسالة 12 بالليل
    public static string Night(int done, int total) =>
        $"{Top}\n🌙 MIDNIGHT ALERT\n{Bot}\nالساعة 12 ولسا في مهام ماخلصتش 😤\nسيب اللي في إيدك وروح خلصها بسرعة قبل اليوم ما يخلص\n{Bar(done, total)} {done}/{total}\nالمتبقي 👇";

    // رسالة بعد ما الضغطة تتنفذ (title لازم يكون معمول له Esc قبل ما يتبعت هنا)
    public static string Updated(string title, bool done) =>
        done ? $"أنا عدّلت المهمة بنجاح\n<b>{title}</b> ← في Notion ✅"
             : $"أنا رجّعت المهمة لسه ماخلصتش\n<b>{title}</b> ← في Notion ⏳";

    // كارت المهمة
    public static string Card(int i, string icon, string title, string time, string desc)
    {
        var descLines = string.Join("\n", desc.Split('\n').Select(l => "┃ " + l));
        return $"┏━━━━━━━━━╮\n┃ 📌 TASK {i:D2}\n┣━━━━━━━━\n" +
               $"┃ {icon} {title}\n┃ 🕔 {time}\n┣━━━━━━━━\n" +
               $"┃ ◈ DESCRIPTION\n{descLines}\n┗━━━━━━━━━╯";
    }

    // كل سطر في الوصف يبدأ بنقطة
    public static string Bulletize(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "• —";
        var res = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            var l = raw.Trim();
            if (l == "") continue;
            bool has = l.StartsWith("•") || l.StartsWith("▫️") || l.StartsWith("☑️") ||
                       l.StartsWith("☐") || l.StartsWith("<b>");
            res.Add(has ? l : "• " + l);
        }
        return string.Join("\n", res);
    }

    static string Blocks(int filled)
    {
        if (filled < 0) filled = 0;
        if (filled > 10) filled = 10;
        return new string('▓', filled) + new string('░', 10 - filled);
    }

    static string Bar(int done, int total) => Blocks(total == 0 ? 0 : done * 10 / total);
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
    public string Title { get; set; } = "";   // لاكتشاف الحذف وكتابة الاسم
    public string Sig { get; set; } = "";     // بصمة المحتوى لاكتشاف التعديل
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
    static readonly string WorkerUrl = Env("WORKER_URL").TrimEnd('/');
    static readonly string WorkerKey = Env("WORKER_API_KEY");
    static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById(Cfg.TimeZoneId);
    const string StatePath = "state.json";

    // المهام اللي اتغيّرت بضغطة زرار من تليجرام في التشغيلة دي
    static readonly HashSet<string> Pressed = new();

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
            // 1) ضغطات الأزرار اللي جت من الـ Worker
            //    (لو الـ Worker وقع، البوت يكمّل شغله عادي)
            try { await ProcessPresses(); }
            catch (Exception ex) { Console.WriteLine($"ProcessPresses failed: {ex.Message}"); }

            // 2) مهام النهارده من نوشن
            var winStart = At(dayDate, Cfg.DayStartHour);
            var winEnd = winStart.AddDays(1);
            var tasks = await FetchTasks(winStart, winEnd);
            int total = tasks.Count;

            var pending = tasks.Where(t => !t.Done).ToList();
            int done = total - pending.Count;
            var morningTime = At(dayDate, Cfg.MorningAt);
            var nightTime = At(dayDate, Cfg.NightAt);

            int changes = 0;                       // عدد التغييرات في التشغيلة دي
            bool morningJustSent = false;
            var fresh = new HashSet<string>();     // كروت اتبعتت في التشغيلة دي

            // 3) تغييرات حصلت في نوشن (DONE / تعديل / حذف) بعد رسالة الصبح
            if (st.MorningSent)
            {
                changes += Pressed.Count;
                changes += await SyncChanges(st, tasks);
            }

            // 4) رسالة الصبح
            if (!st.MorningSent && now >= morningTime)
            {
                if (total == 0)
                {
                    await SendText(Msg.NoTasks);
                }
                else
                {
                    await SendText(Msg.Morning(dayDate));
                    for (int i = 0; i < total; i++) await SendCard(st, tasks[i], i + 1, total);
                }
                st.MorningSent = true;
                st.LastReminder = now;
                st.AllDoneSent = pending.Count == 0;
                morningJustSent = true;
            }
            // 5) مهام جديدة بعد رسالة الصبح
            else if (st.MorningSent)
            {
                foreach (var t in tasks.Where(t => !st.Cards.ContainsKey(t.Id)).ToList())
                {
                    await SendText(Msg.NewTask(Esc(t.Title)));
                    await SendCard(st, t, tasks.IndexOf(t) + 1, total);
                    fresh.Add(t.Id);
                    changes++;
                    st.AllDoneSent = false;
                }
            }

            // 6) بعد أي تغيير: ابعت المتبقي
            bool sentRemaining = false;
            if (st.MorningSent && !morningJustSent && changes > 0 && pending.Count > 0)
            {
                await SendText(Msg.Remaining(done, total));
                await ResendPending(st, tasks, pending, fresh);
                st.LastReminder = now;
                st.AllDoneSent = false;
                sentRemaining = true;
            }

            // 7) خلصت كل المهام
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

            // 8) رسالة آخر الليل
            bool sentNight = false;
            if (st.MorningSent && !st.NightSent && now >= nightTime)
            {
                st.NightSent = true;
                if (pending.Count > 0)
                {
                    await SendText(Msg.Night(done, total));
                    await ResendPending(st, tasks, pending, sentRemaining ? fresh : null);
                    st.LastReminder = now;
                    sentNight = true;
                }
            }

            // 9) "وصلت فين؟" كل كام ساعة
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
        var items = JsonNode.Parse(await Worker(HttpMethod.Get, "/pending"))!.AsArray();
        if (items.Count == 0) return;

        var doneKeys = new List<string>();
        var handled = new HashSet<string>();

        foreach (var it in items)
        {
            var key = it!["key"]!.GetValue<string>();
            var data = it["data"]?.GetValue<string>() ?? "";
            if (!data.StartsWith("t|")) { doneKeys.Add(key); continue; }
            var id = data[2..];

            try
            {
                if (handled.Add(id))
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
                    Pressed.Add(id);

                    string title = "";
                    foreach (var kv in page["properties"]!.AsObject())
                    {
                        if (kv.Value!["type"]!.GetValue<string>() == "title")
                        {
                            title = PlainText(kv.Value["title"]);
                            break;
                        }
                    }
                    await SendText(Msg.Updated(Esc(title), !cur));
                }
                doneKeys.Add(key);
            }
            catch (Exception ex) { Console.WriteLine($"Press failed for {id}: {ex.Message}"); }
        }

        if (doneKeys.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var k in doneKeys) arr.Add(k);
            await Worker(HttpMethod.Post, "/clear", new JsonObject { ["keys"] = arr });
        }
    }

    // بيقارن حالة نوشن بالكروت اللي اتبعتت، وبيبعت إشعار لكل تغيير. بيرجّع عدد التغييرات
    static async Task<int> SyncChanges(State st, List<TaskItem> tasks)
    {
        int changes = 0;

        for (int idx = 0; idx < tasks.Count; idx++)
        {
            var t = tasks[idx];
            if (!st.Cards.TryGetValue(t.Id, out var card)) continue;

            // أ) DONE اتغيّر
            if (card.Done != t.Done)
            {
                await TgSafe("editMessageReplyMarkup", new JsonObject
                {
                    ["chat_id"] = ChatId,
                    ["message_id"] = card.MessageId,
                    ["reply_markup"] = Kb(t)
                });
                card.Done = t.Done;

                // لو التغيير جه من زرار تليجرام، رسالة "أنا عدّلت المهمة" اتبعتت خلاص
                if (!Pressed.Contains(t.Id))
                {
                    await SendText(Msg.DoneInNotion(Esc(t.Title), t.Done));
                    changes++;
                }
            }

            // ب) الاسم أو الوقت أو المحتوى اتعدّل
            var (text, sig) = await BuildCard(t, idx + 1);
            if (string.IsNullOrEmpty(card.Sig))
            {
                // أول مرة بعد التحديث: سجّل البصمة من غير إشعار
                card.Sig = sig;
            }
            else if (card.Sig != sig)
            {
                card.Sig = sig;
                await SendText(Msg.Edited(Esc(t.Title)));
                changes++;

                // المهمة الخلصانة مش هتتبعت تاني، فنحدّث الكارت مكانه
                if (t.Done)
                {
                    await TgSafe("editMessageText", new JsonObject
                    {
                        ["chat_id"] = ChatId,
                        ["message_id"] = card.MessageId,
                        ["text"] = text,
                        ["parse_mode"] = "HTML",
                        ["reply_markup"] = Kb(t)
                    });
                }
            }
            card.Title = t.Title;
        }

        // ج) مهام اتشالت أو اتنقلت ليوم تاني
        var ids = tasks.Select(x => x.Id).ToHashSet();
        foreach (var id in st.Cards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            var c = st.Cards[id];
            await TgSafe("editMessageReplyMarkup", new JsonObject
            {
                ["chat_id"] = ChatId,
                ["message_id"] = c.MessageId,
                ["reply_markup"] = new JsonObject { ["inline_keyboard"] = new JsonArray() }
            });
            await SendText(Msg.Removed(string.IsNullOrEmpty(c.Title) ? "—" : Esc(c.Title)));
            st.Cards.Remove(id);
            changes++;
        }

        return changes;
    }

    static JsonObject Kb(TaskItem t) => new()
    {
        ["inline_keyboard"] = new JsonArray(new JsonArray(new JsonObject
        {
            ["text"] = t.Done ? Cfg.BtnCompleted : Cfg.BtnDone,
            ["callback_data"] = "t|" + t.Id
        }))
    };

    // ---------------- إرسال الكروت ----------------
    static async Task ResendPending(State st, List<TaskItem> all, List<TaskItem> pending,
                                    HashSet<string>? skip = null)
    {
        foreach (var t in pending)
        {
            if (skip != null && skip.Contains(t.Id)) continue;
            await SendCard(st, t, all.IndexOf(t) + 1, all.Count);
        }
    }

    // بيبني نص الكارت + بصمة للمحتوى
    static async Task<(string Text, string Sig)> BuildCard(TaskItem t, int i)
    {
        var content = await GetContent(t.Id, t.Notes);
        var time = t.AllDay ? "ALL DAY"
            : $"{t.Start.ToString("h:mm tt", Inv)} → {t.End.ToString("h:mm tt", Inv)}";
        var icon = string.IsNullOrEmpty(t.Icon) ? "📝" : t.Icon;
        var desc = Msg.Bulletize(content);

        var text = Msg.Card(i, icon, Esc(t.Title), time, desc);
        var sig = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{icon}|{t.Title}|{time}|{desc}")));
        return (text, sig);
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

        var (text, sig) = await BuildCard(t, i);

        var res = await Tg("sendMessage", new JsonObject
        {
            ["chat_id"] = ChatId,
            ["text"] = text,
            ["parse_mode"] = "HTML",
            ["reply_markup"] = Kb(t)
        });
        st.Cards[t.Id] = new CardInfo
        {
            MessageId = res["message_id"]!.GetValue<long>(),
            Done = t.Done,
            Title = t.Title,
            Sig = sig
        };
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

    static async Task<string> Worker(HttpMethod method, string path, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, WorkerUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerKey);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"Worker {(int)resp.StatusCode}: {text}");
        return text;
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
