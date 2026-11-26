// =====================================================================
//  StaffBot — Telegram-бот статусов сотрудников (ASP.NET Core 8, один файл)
//  Сотрудник выбирает статус в личке с ботом, бот пишет его в рабочую группу.
//  Работает через long polling, без внешних Telegram-библиотек (чистый HTTP API).
//  Хранение: SQLite (/data/staff.db на Railway — подключите Volume).
// =====================================================================
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("Не задана переменная окружения BOT_TOKEN");

var admins = (Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(s => long.TryParse(s, out _))
    .Select(long.Parse)
    .ToHashSet();

long? envGroup = long.TryParse(Environment.GetEnvironmentVariable("GROUP_ID"), out var g) ? g : null;
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "staff.db";

builder.Services.AddSingleton(new Settings(token, admins, envGroup));
builder.Services.AddSingleton(new Db(dbPath));
builder.Services.AddSingleton(new Tg(token));
builder.Services.AddHostedService<BotService>();

var app = builder.Build();
app.MapGet("/", () => "StaffBot is running");
app.MapGet("/health", () => Results.Ok("ok"));
app.Run();

// =====================================================================
//  Настройки и справочник статусов
// =====================================================================
record Settings(string Token, HashSet<long> Admins, long? EnvGroup);

// Dur: Phrase — готовая фраза для группы; если Phrase == null, бот спросит текст (Ask)
record Dur(string Code, string Label, string? Phrase, string? Ask);
record Status(string Key, string Emoji, string Action, string Button, Dur[] Durs);

static class Data
{
    public static readonly Status[] All =
    {
        new("remote", "🏠", "работает удалённо", "🏠 Работаю удалённо", new Dur[]
        {
            new("lunch", "До обеда", "до обеда", null),
            new("today", "Сегодня", "сегодня", null),
            new("period", "Период", null, "Напишите период, например: 27.11 – 30.11"),
        }),
        new("late", "⏰", "задерживается", "⏰ Задерживаюсь", new Dur[]
        {
            new("15", "15 минут", "на 15 минут", null),
            new("30", "30 минут", "на 30 минут", null),
            new("60", "1 час", "на 1 час", null),
            new("custom", "Указать время", null, "Напишите, насколько задержитесь, например: до 10:30"),
        }),
        new("leave", "🚪", "отпросился", "🚪 Отпросился", new Dur[]
        {
            new("lunch", "До обеда", "до обеда", null),
            new("afterlunch", "После обеда", "после обеда", null),
            new("today", "На весь день", "на весь день", null),
            new("custom", "Указать время", null, "Напишите время, например: с 15:00 до 17:00"),
        }),
        new("away", "🚶", "отошёл", "🚶 Отошёл", new Dur[]
        {
            new("15", "15 минут", "на 15 минут", null),
            new("30", "30 минут", "на 30 минут", null),
            new("60", "1 час", "на 1 час", null),
            new("custom", "Указать время", null, "Напишите, на сколько отошли, например: на 20 минут"),
        }),
        new("sick", "🤒", "болеет", "🤒 Болезнь", new Dur[]
        {
            new("today", "Сегодня", "сегодня", null),
            new("period", "Период", null, "Напишите период болезни, например: 27.11 – 30.11"),
        }),
        new("vacation", "🏖", "в отпуске / на отгуле", "🏖 Отпуск / отгул", new Dur[]
        {
            new("today", "Сегодня", "сегодня", null),
            new("period", "Период", null, "Напишите период, например: 01.12 – 14.12"),
        }),
        new("trip", "🚗", "в командировке / на встрече", "🚗 Командировка / встреча", new Dur[]
        {
            new("today", "Сегодня", "сегодня", null),
            new("custom", "Указать детали", null, "Напишите детали, например: встреча с клиентом до 14:00"),
        }),
        new("back", "✅", "на месте", "✅ Я на месте / вернулся", Array.Empty<Dur>()),
        new("other", "📝", "", "📝 Другое", Array.Empty<Dur>()),
    };

    public static Status? Find(string key) => All.FirstOrDefault(s => s.Key == key);
}

// =====================================================================
//  База данных (SQLite)
// =====================================================================
class Db
{
    private readonly SqliteConnection _c;
    private readonly object _l = new();

    public Db(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _c = new SqliteConnection($"Data Source={path}");
        _c.Open();
        Exec(@"
            CREATE TABLE IF NOT EXISTS users (
                tg_id INTEGER PRIMARY KEY,
                full_name TEXT NOT NULL,
                username TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tg_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                details TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS states (
                tg_id INTEGER PRIMARY KEY,
                state TEXT NOT NULL,
                key TEXT
            );");
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private int Exec(string sql, params (string, object?)[] p)
    {
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            return cmd.ExecuteNonQuery();
        }
    }

    private List<object?[]> Query(string sql, params (string, object?)[] p)
    {
        lock (_l)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
    }

    // --- сотрудники ---
    public string? GetName(long id) =>
        Query("SELECT full_name FROM users WHERE tg_id=$id", ("$id", id)).FirstOrDefault()?[0] as string;

    public void SaveUser(long id, string name, string? username) =>
        Exec(@"INSERT INTO users(tg_id, full_name, username, created_at) VALUES($id,$n,$u,$t)
               ON CONFLICT(tg_id) DO UPDATE SET full_name=excluded.full_name,
               username=COALESCE(excluded.username, users.username)",
            ("$id", id), ("$n", name), ("$u", username), ("$t", Now()));

    public bool Rename(long id, string name) =>
        Exec("UPDATE users SET full_name=$n WHERE tg_id=$id", ("$n", name), ("$id", id)) > 0;

    public void DeleteUser(long id)
    {
        Exec("DELETE FROM users WHERE tg_id=$id", ("$id", id));
        ClearState(id);
    }

    public List<(long Id, string Name, string? Username)> Users() =>
        Query("SELECT tg_id, full_name, username FROM users ORDER BY full_name")
            .Select(r => ((long)r[0]!, (string)r[1]!, r[2] as string)).ToList();

    // --- группа ---
    public long? GetGroupId()
    {
        var v = Query("SELECT value FROM settings WHERE key='group_id'").FirstOrDefault()?[0] as string;
        return long.TryParse(v, out var id) ? id : null;
    }

    public void SetGroupId(long id) =>
        Exec(@"INSERT INTO settings(key, value) VALUES('group_id', $v)
               ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$v", id.ToString()));

    // --- журнал ---
    public void Log(long id, string status, string details) =>
        Exec("INSERT INTO log(tg_id, status, details, created_at) VALUES($id,$s,$d,$t)",
            ("$id", id), ("$s", status), ("$d", details), ("$t", Now()));

    // --- состояние диалога ---
    public void SetState(long id, string state, string? key = null) =>
        Exec(@"INSERT INTO states(tg_id, state, key) VALUES($id,$s,$k)
               ON CONFLICT(tg_id) DO UPDATE SET state=excluded.state, key=excluded.key",
            ("$id", id), ("$s", state), ("$k", key));

    public (string State, string? Key)? GetState(long id)
    {
        var r = Query("SELECT state, key FROM states WHERE tg_id=$id", ("$id", id)).FirstOrDefault();
        return r == null ? null : ((string)r[0]!, r[1] as string);
    }

    public void ClearState(long id) => Exec("DELETE FROM states WHERE tg_id=$id", ("$id", id));
}

// =====================================================================
//  Клиент Telegram Bot API (чистый HTTP)
// =====================================================================
class Tg
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(70) };
    private readonly string _base;

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public Tg(string token) => _base = $"https://api.telegram.org/bot{token}/";

    public async Task<JsonNode?> Call(string method, object payload, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload, Opts), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_base + method, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(body)!;
        if (json["ok"]?.GetValue<bool>() != true)
            throw new Exception($"Telegram {method}: {json["description"]}");
        return json["result"];
    }

    public Task Send(long chatId, string text, object? markup = null) =>
        Call("sendMessage", new { chat_id = chatId, text, parse_mode = "HTML", reply_markup = markup });

    public async Task Edit(long chatId, long messageId, string text, object? markup = null)
    {
        try
        {
            await Call("editMessageText", new
            {
                chat_id = chatId,
                message_id = messageId,
                text,
                parse_mode = "HTML",
                reply_markup = markup
            });
        }
        catch (Exception e) when (e.Message.Contains("not modified")) { /* ничего страшного */ }
    }

    public Task Answer(string callbackId, string? text = null, bool alert = false) =>
        Call("answerCallbackQuery", new { callback_query_id = callbackId, text, show_alert = alert });

    public static object Keyboard(IEnumerable<IEnumerable<(string Text, string Data)>> rows) => new
    {
        inline_keyboard = rows.Select(r => r.Select(b => new { text = b.Text, callback_data = b.Data }).ToArray()).ToArray()
    };
}

// =====================================================================
//  Логика бота (фоновый сервис, long polling)
// =====================================================================
class BotService : BackgroundService
{
    private readonly Settings _s;
    private readonly Db _db;
    private readonly Tg _tg;
    private readonly ILogger<BotService> _log;

    public BotService(Settings s, Db db, Tg tg, ILogger<BotService> log)
    {
        _s = s; _db = db; _tg = tg; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // на случай, если ранее был установлен webhook
        try { await _tg.Call("deleteWebhook", new { drop_pending_updates = false }, ct); } catch { }

        _log.LogInformation("Бот запущен, ожидаю обновления...");
        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await _tg.Call("getUpdates", new
                {
                    offset,
                    timeout = 30,
                    allowed_updates = new[] { "message", "callback_query" }
                }, ct);

                foreach (var u in res!.AsArray())
                {
                    offset = u!["update_id"]!.GetValue<long>() + 1;
                    try
                    {
                        if (u["message"] is JsonNode m) await OnMessage(m);
                        else if (u["callback_query"] is JsonNode cb) await OnCallback(cb);
                    }
                    catch (Exception e)
                    {
                        _log.LogError(e, "Ошибка обработки обновления");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _log.LogError(e, "Ошибка получения обновлений");
                await Task.Delay(3000, ct);
            }
        }
    }

    // ---------- утилиты ----------
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static bool ValidName(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && text.Length <= 80 && parts.All(p => !p.All(char.IsDigit));
    }

    private static string NormalizeName(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private bool IsAdmin(long id) => _s.Admins.Contains(id);

    private static object MainMenu() =>
        Tg.Keyboard(Data.All.Select(s => new[] { (s.Button, $"st:{s.Key}") }));

    private static object DurationsMenu(Status st)
    {
        var rows = st.Durs.Select(d => (d.Label, $"du:{st.Key}:{d.Code}")).Chunk(2)
            .Select(c => c.AsEnumerable()).ToList();
        rows.Add(new[] { ("⬅️ Назад", "menu") });
        return Tg.Keyboard(rows);
    }

    // ---------- сообщения ----------
    private async Task OnMessage(JsonNode m)
    {
        var chat = m["chat"]!;
        var chatId = chat["id"]!.GetValue<long>();
        var type = chat["type"]!.GetValue<string>();
        var from = m["from"];
        if (from == null) return;
        var uid = from["id"]!.GetValue<long>();
        var username = from["username"]?.GetValue<string>();
        var text = m["text"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        string? cmd = null;
        var args = "";
        if (text.StartsWith('/'))
        {
            var sp = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            cmd = sp[0][1..].Split('@')[0].ToLowerInvariant();
            args = sp.Length > 1 ? sp[1].Trim() : "";
        }

        // --- группа: только /setgroup ---
        if (type is "group" or "supergroup")
        {
            if (cmd == "setgroup")
            {
                if (!IsAdmin(uid))
                {
                    await _tg.Send(chatId, "Только администратор бота может подключить группу.");
                    return;
                }
                _db.SetGroupId(chatId);
                await _tg.Send(chatId, "✅ Группа подключена. Теперь статусы сотрудников будут приходить сюда.");
            }
            return;
        }
        if (type != "private") return;

        // --- команды в личке ---
        switch (cmd)
        {
            case "start":
                _db.ClearState(uid);
                var name = _db.GetName(uid);
                if (name != null)
                {
                    await _tg.Send(chatId, $"Привет, <b>{Esc(name)}</b>! Выберите статус:", MainMenu());
                }
                else
                {
                    _db.SetState(uid, "reg");
                    await _tg.Send(chatId, "👋 Добро пожаловать!\n\nНапишите ваши <b>Имя и Фамилию</b> (например: Фаридун Икромзода):");
                }
                return;

            case "name":
                _db.SetState(uid, "reg");
                await _tg.Send(chatId, "Напишите новые <b>Имя и Фамилию</b>:");
                return;

            case "menu":
                _db.ClearState(uid);
                if (_db.GetName(uid) == null) { await _tg.Send(chatId, "Сначала зарегистрируйтесь: /start"); return; }
                await _tg.Send(chatId, "Выберите статус:", MainMenu());
                return;

            case "users":
                if (!IsAdmin(uid)) return;
                var users = _db.Users();
                if (users.Count == 0) { await _tg.Send(chatId, "Пользователей пока нет."); return; }
                await _tg.Send(chatId, string.Join("\n", users.Select(u =>
                    $"<code>{u.Id}</code> — {Esc(u.Name)}" + (u.Username != null ? $" (@{Esc(u.Username)})" : ""))));
                return;

            case "rename":
                if (!IsAdmin(uid)) return;
                var rp = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (rp.Length < 2 || !long.TryParse(rp[0], out var rid) || !ValidName(rp[1]))
                {
                    await _tg.Send(chatId, "Использование: /rename &lt;id&gt; Имя Фамилия");
                    return;
                }
                await _tg.Send(chatId, _db.Rename(rid, NormalizeName(rp[1]))
                    ? "✅ Имя изменено."
                    : "Пользователь с таким id не найден (/users).");
                return;

            case "add":
                if (!IsAdmin(uid)) return;
                var ap = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (ap.Length < 2 || !long.TryParse(ap[0], out var aid) || !ValidName(ap[1]))
                {
                    await _tg.Send(chatId, "Использование: /add &lt;telegram_id&gt; Имя Фамилия");
                    return;
                }
                _db.SaveUser(aid, NormalizeName(ap[1]), null);
                await _tg.Send(chatId, "✅ Сотрудник добавлен.");
                return;

            case "delete":
                if (!IsAdmin(uid)) return;
                if (!long.TryParse(args, out var did))
                {
                    await _tg.Send(chatId, "Использование: /delete &lt;id&gt;");
                    return;
                }
                _db.DeleteUser(did);
                await _tg.Send(chatId, "🗑 Пользователь удалён.");
                return;
        }

        if (cmd != null) return; // неизвестная команда

        // --- обычный текст: смотрим состояние диалога ---
        var state = _db.GetState(uid);
        if (state == null)
        {
            if (_db.GetName(uid) == null)
            {
                _db.SetState(uid, "reg");
                await _tg.Send(chatId, "Напишите ваши <b>Имя и Фамилию</b> (например: Фаридун Икромзода):");
            }
            else
            {
                await _tg.Send(chatId, "Выберите статус:", MainMenu());
            }
            return;
        }

        if (state.Value.State == "reg")
        {
            var full = NormalizeName(text);
            if (!ValidName(full))
            {
                await _tg.Send(chatId, "Пожалуйста, напишите имя и фамилию через пробел, например: <b>Фаридун Икромзода</b>");
                return;
            }
            _db.SaveUser(uid, full, username);
            _db.ClearState(uid);
            await _tg.Send(chatId, $"✅ Сохранено: <b>{Esc(full)}</b>\n\nВыберите статус:", MainMenu());
            return;
        }

        if (state.Value.State == "details" && state.Value.Key != null)
        {
            _db.ClearState(uid);
            var detail = text.Length > 200 ? text[..200] : text;
            await Publish(uid, chatId, state.Value.Key, detail);
        }
    }

    // ---------- нажатия на кнопки ----------
    private async Task OnCallback(JsonNode cb)
    {
        var cbId = cb["id"]!.GetValue<string>();
        var uid = cb["from"]!["id"]!.GetValue<long>();
        var data = cb["data"]?.GetValue<string>() ?? "";
        var msg = cb["message"];
        if (msg == null) { await _tg.Answer(cbId); return; }
        var chatId = msg["chat"]!["id"]!.GetValue<long>();
        var msgId = msg["message_id"]!.GetValue<long>();

        if (_db.GetName(uid) == null)
        {
            await _tg.Answer(cbId, "Сначала отправьте /start", true);
            return;
        }

        if (data == "menu")
        {
            _db.ClearState(uid);
            await _tg.Edit(chatId, msgId, "Выберите статус:", MainMenu());
            await _tg.Answer(cbId);
            return;
        }

        var parts = data.Split(':');

        // выбор статуса
        if (parts[0] == "st" && parts.Length == 2 && Data.Find(parts[1]) is Status st)
        {
            await _tg.Answer(cbId);
            if (st.Key == "back")
            {
                await Publish(uid, chatId, st.Key, "");
            }
            else if (st.Key == "other")
            {
                _db.SetState(uid, "details", st.Key);
                await _tg.Edit(chatId, msgId, "Напишите ваше сообщение для группы (например: на обучении до 16:00):");
            }
            else
            {
                await _tg.Edit(chatId, msgId,
                    $"{st.Emoji} <b>{char.ToUpper(st.Action[0]) + st.Action[1..]}</b>\nВыберите срок:", DurationsMenu(st));
            }
            return;
        }

        // выбор срока
        if (parts[0] == "du" && parts.Length == 3 && Data.Find(parts[1]) is Status st2)
        {
            var d = st2.Durs.FirstOrDefault(x => x.Code == parts[2]);
            if (d == null) { await _tg.Answer(cbId, "Неизвестный вариант", true); return; }
            await _tg.Answer(cbId);
            if (d.Phrase != null)
            {
                await Publish(uid, chatId, st2.Key, d.Phrase);
            }
            else
            {
                _db.SetState(uid, "details", st2.Key);
                await _tg.Edit(chatId, msgId, d.Ask ?? "Напишите детали:");
            }
            return;
        }

        await _tg.Answer(cbId);
    }

    // ---------- отправка статуса в группу ----------
    private async Task Publish(long uid, long chatId, string key, string detail)
    {
        var name = _db.GetName(uid);
        if (name == null) { await _tg.Send(chatId, "Сначала зарегистрируйтесь: /start"); return; }

        var group = _db.GetGroupId() ?? _s.EnvGroup;
        if (group == null)
        {
            await _tg.Send(chatId,
                "⚠️ Группа ещё не подключена. Администратор должен добавить бота в группу и отправить там команду /setgroup");
            return;
        }

        var st = Data.Find(key)!;
        var parts = new List<string> { $"<b>{Esc(name)}</b>", st.Emoji };
        if (st.Action != "") parts.Add(st.Action);
        if (detail != "") parts.Add(Esc(detail));
        var text = string.Join(' ', parts);

        try
        {
            await _tg.Send(group.Value, text);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Не удалось отправить в группу");
            await _tg.Send(chatId, "❌ Не удалось отправить в группу: " + Esc(e.Message));
            return;
        }

        _db.Log(uid, key, detail);
        await _tg.Send(chatId, "✅ Отправлено в группу:\n\n" + text, MainMenu());
    }
}
