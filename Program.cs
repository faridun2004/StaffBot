using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// =====================================================================
// CONFIGURATION
// =====================================================================

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var token = Environment.GetEnvironmentVariable("BOT_TOKEN");

if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("BOT_TOKEN is not configured.");

var admins = (Environment.GetEnvironmentVariable("ADMIN_IDS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(x => long.TryParse(x, out _))
    .Select(long.Parse)
    .ToHashSet();

long? envGroup = long.TryParse(
    Environment.GetEnvironmentVariable("GROUP_ID"),
    out var groupId)
        ? groupId
        : null;

var dbPath = Environment.GetEnvironmentVariable("DB_PATH")
             ?? "data/staff.db";

builder.Services.AddSingleton(new Settings(token, admins, envGroup));

builder.Services.AddSingleton(new Db(dbPath));

builder.Services.AddSingleton(new Tg(token));

builder.Services.AddHostedService<BotService>();

var app = builder.Build();

app.MapGet("/", () => "StaffBot is running");

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

// =====================================================================
// SETTINGS
// =====================================================================

record Settings(
    string Token,
    HashSet<long> Admins,
    long? EnvGroup);

// =====================================================================
// STATUS MODELS
// =====================================================================

record Dur(
    string Code,
    string Label,
    string? Phrase,
    string? Ask);

record Status(
    string Key,
    string Emoji,
    string Action,
    string Button,
    Dur[] Durs);

// =====================================================================
// STATUS DATA
// =====================================================================

static class Data
{
    public static readonly Status[] All =
    {
        new("remote", "🏠", "работает удалённо",
            "🏠 Работаю удалённо",
            new Dur[]
            {
                new("lunch", "До обеда", "до обеда", null),
                new("today", "Сегодня", "сегодня", null),
                new("period", "Период", null,
                    "Напишите период, например: 27.11 – 30.11")
            }),

        new("late", "⏰", "задерживается",
            "⏰ Задерживаюсь",
            new Dur[]
            {
                new("15", "15 минут", "на 15 минут", null),
                new("30", "30 минут", "на 30 минут", null),
                new("60", "1 час", "на 1 час", null),
                new("custom", "Указать время", null,
                    "Напишите время, например: до 10:30")
            }),

        new("leave", "🚪", "отпросился",
            "🚪 Отпросился",
            new Dur[]
            {
                new("lunch", "До обеда", "до обеда", null),
                new("afterlunch", "После обеда", "после обеда", null),
                new("today", "На весь день", "на весь день", null),
                new("custom", "Указать время", null,
                    "Напишите время, например: с 15:00 до 17:00")
            }),

        new("away", "🚶", "отошёл",
            "🚶 Отошёл",
            new Dur[]
            {
                new("15", "15 минут", "на 15 минут", null),
                new("30", "30 минут", "на 30 минут", null),
                new("60", "1 час", "на 1 час", null),
                new("custom", "Указать время", null,
                    "Напишите, на сколько отошли, например: на 20 минут")
            }),

        new("sick", "🤒", "болеет",
            "🤒 Болезнь",
            new Dur[]
            {
                new("today", "Сегодня", "сегодня", null),
                new("period", "Период", null,
                    "Напишите период болезни, например: 27.11 – 30.11")
            }),

        new("vacation", "🏖", "в отпуске / на отгуле",
            "🏖 Отпуск / отгул",
            new Dur[]
            {
                new("today", "Сегодня", "сегодня", null),
                new("period", "Период", null,
                    "Напишите период, например: 01.12 – 14.12")
            }),

        new("trip", "🚗", "в командировке / на встрече",
            "🚗 Командировка / встреча",
            new Dur[]
            {
                new("today", "Сегодня", "сегодня", null),
                new("custom", "Указать детали", null,
                    "Напишите детали, например: встреча с клиентом до 14:00")
            }),

        new("back", "✅", "на месте",
            "✅ Я на месте / вернулся",
            Array.Empty<Dur>()),

        new("other", "📝", "",
            "📝 Другое",
            Array.Empty<Dur>())
    };

    public static Status? Find(string key) =>
        All.FirstOrDefault(x => x.Key == key);
}

// =====================================================================
// DATABASE
// =====================================================================

class Db
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    private static long UnixNow =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public Db(string path)
    {
        var fullPath = Path.GetFullPath(path);

        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(
            $"Data Source={fullPath}");

        _connection.Open();

        Execute("""
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
                key TEXT,
                updated_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_log_tg_id
            ON log(tg_id);

            """);
    }

    private static string Now() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

    private int Execute(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();

            cmd.CommandText = sql;

            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(
                    p.Name,
                    p.Value ?? DBNull.Value);
            }

            return cmd.ExecuteNonQuery();
        }
    }

    private List<object?[]> Query(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();

            cmd.CommandText = sql;

            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(
                    p.Name,
                    p.Value ?? DBNull.Value);
            }

            using var reader = cmd.ExecuteReader();

            var result = new List<object?[]>();

            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = reader.IsDBNull(i)
                        ? null
                        : reader.GetValue(i);
                }

                result.Add(row);
            }

            return result;
        }
    }

    // -------------------------------------------------------------
    // USERS
    // -------------------------------------------------------------

    public string? GetName(long id)
    {
        return Query(
            "SELECT full_name FROM users WHERE tg_id=$id",
            ("$id", id))
            .FirstOrDefault()?[0] as string;
    }

    public void SaveUser(
        long id,
        string name,
        string? username)
    {
        Execute("""
            INSERT INTO users(
                tg_id,
                full_name,
                username,
                created_at)
            VALUES($id,$name,$username,$created)

            ON CONFLICT(tg_id)
            DO UPDATE SET
                full_name=excluded.full_name,
                username=COALESCE(
                    excluded.username,
                    users.username)
            """,
            ("$id", id),
            ("$name", name),
            ("$username", username),
            ("$created", Now()));
    }

    public bool Rename(long id, string name)
    {
        return Execute(
            "UPDATE users SET full_name=$name WHERE tg_id=$id",
            ("$name", name),
            ("$id", id)) > 0;
    }

    public void DeleteUser(long id)
    {
        Execute(
            "DELETE FROM users WHERE tg_id=$id",
            ("$id", id));

        ClearState(id);
    }

    public List<(long Id, string Name, string? Username)> Users()
    {
        return Query("""
            SELECT tg_id, full_name, username
            FROM users
            ORDER BY full_name
            """)
            .Select(x =>
                ((long)x[0]!,
                 (string)x[1]!,
                 x[2] as string))
            .ToList();
    }

    // -------------------------------------------------------------
    // GROUP
    // -------------------------------------------------------------

    public long? GetGroupId()
    {
        var value = Query("""
            SELECT value FROM settings
            WHERE key='group_id'
            """).FirstOrDefault()?[0] as string;

        return long.TryParse(value, out var id)
            ? id
            : null;
    }

    public void SetGroupId(long id)
    {
        Execute("""
            INSERT INTO settings(key,value)
            VALUES('group_id',$value)

            ON CONFLICT(key)
            DO UPDATE SET value=excluded.value
            """,
            ("$value", id.ToString()));
    }

    // -------------------------------------------------------------
    // LOG
    // -------------------------------------------------------------

    public void Log(
        long id,
        string status,
        string details)
    {
        Execute("""
            INSERT INTO log(
                tg_id,
                status,
                details,
                created_at)
            VALUES($id,$status,$details,$created)
            """,
            ("$id", id),
            ("$status", status),
            ("$details", details),
            ("$created", Now()));
    }

    // -------------------------------------------------------------
    // STATES
    // -------------------------------------------------------------

    public void SetState(
        long id,
        string state,
        string? key = null)
    {
        Execute("""
            INSERT INTO states(
                tg_id,
                state,
                key,
                updated_at)
            VALUES($id,$state,$key,$updated)

            ON CONFLICT(tg_id)
            DO UPDATE SET
                state=excluded.state,
                key=excluded.key,
                updated_at=excluded.updated_at
            """,
            ("$id", id),
            ("$state", state),
            ("$key", key),
            ("$updated", UnixNow));
    }

    // Создаём состояние только если его ещё нет.
    // Защищает от повторного выбора статуса.

    public bool TryStartState(
        long id,
        string state,
        string? key)
    {
        return Execute("""
            INSERT INTO states(
                tg_id,
                state,
                key,
                updated_at)
            VALUES($id,$state,$key,$updated)

            ON CONFLICT(tg_id) DO NOTHING
            """,
            ("$id", id),
            ("$state", state),
            ("$key", key),
            ("$updated", UnixNow)) > 0;
    }

    // Атомарная смена состояния.

    public bool TryTransitionState(
        long id,
        string expectedState,
        string? expectedKey,
        string newState,
        string? newKey = null)
    {
        return Execute("""
            UPDATE states
            SET state=$newState,
                key=$newKey,
                updated_at=$updated

            WHERE tg_id=$id
              AND state=$expectedState
              AND key IS $expectedKey
            """,
            ("$id", id),
            ("$expectedState", expectedState),
            ("$expectedKey", expectedKey),
            ("$newState", newState),
            ("$newKey", newKey),
            ("$updated", UnixNow)) > 0;
    }

    public (string State, string? Key)? GetState(long id)
    {
        var row = Query("""
            SELECT state, key, updated_at
            FROM states
            WHERE tg_id=$id
            """,
            ("$id", id)).FirstOrDefault();

        if (row == null)
            return null;

        var updated = (long)row[2]!;

        // Автоматическая отмена через 5 минут.

        if (UnixNow - updated > 300)
        {
            ClearState(id);
            return null;
        }

        return (
            (string)row[0]!,
            row[1] as string);
    }

    public void ClearState(long id)
    {
        Execute(
            "DELETE FROM states WHERE tg_id=$id",
            ("$id", id));
    }
}

// =====================================================================
// TELEGRAM HTTP CLIENT
// =====================================================================

class Tg
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(70)
    };

    private readonly string _base;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder
            .UnsafeRelaxedJsonEscaping
    };

    public Tg(string token)
    {
        _base = $"https://api.telegram.org/bot{token}/";
    }

    public async Task<JsonNode?> Call(
        string method,
        object payload,
        CancellationToken ct = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, Options),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.PostAsync(
            _base + method,
            content,
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        var json = JsonNode.Parse(body);

        if (json?["ok"]?.GetValue<bool>() != true)
        {
            throw new Exception(
                $"Telegram {method}: {json?["description"]}");
        }

        return json["result"];
    }

    public Task Send(
        long chatId,
        string text,
        object? markup = null)
    {
        return Call("sendMessage", new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            reply_markup = markup
        });
    }

    public async Task Edit(
        long chatId,
        long messageId,
        string text,
        object? markup = null)
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
        catch (Exception e) when (
            e.Message.Contains("not modified",
                StringComparison.OrdinalIgnoreCase))
        {
            // Сообщение не изменилось.
        }
    }

    public Task Answer(
        string callbackId,
        string? text = null,
        bool alert = false)
    {
        return Call("answerCallbackQuery", new
        {
            callback_query_id = callbackId,
            text,
            show_alert = alert
        });
    }

    public static object Keyboard(
        IEnumerable<IEnumerable<(string Text, string Data)>> rows)
    {
        return new
        {
            inline_keyboard = rows
                .Select(row => row.Select(button => new
                {
                    text = button.Text,
                    callback_data = button.Data
                }).ToArray())
                .ToArray()
        };
    }
}

// =====================================================================
// BOT SERVICE
// =====================================================================

class BotService : BackgroundService
{
    private readonly Settings _settings;
    private readonly Db _db;
    private readonly Tg _tg;
    private readonly ILogger<BotService> _log;

    public BotService(
        Settings settings,
        Db db,
        Tg tg,
        ILogger<BotService> log)
    {
        _settings = settings;
        _db = db;
        _tg = tg;
        _log = log;
    }

    // -------------------------------------------------------------
    // START
    // -------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await _tg.Call(
                "deleteWebhook",
                new { drop_pending_updates = false },
                ct);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not delete webhook.");
        }

        _log.LogInformation("StaffBot started.");

        long offset = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _tg.Call(
                    "getUpdates",
                    new
                    {
                        offset,
                        timeout = 30,
                        allowed_updates = new[]
                        {
                            "message",
                            "callback_query"
                        }
                    },
                    ct);

                if (result is not JsonArray updates)
                    continue;

                foreach (var update in updates)
                {
                    if (update == null)
                        continue;

                    offset = update["update_id"]!.GetValue<long>() + 1;

                    try
                    {
                        if (update["message"] is JsonNode message)
                            await OnMessage(message);

                        else if (update["callback_query"] is JsonNode callback)
                            await OnCallback(callback);
                    }
                    catch (Exception e)
                    {
                        _log.LogError(e, "Update processing error.");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Polling error.");

                await Task.Delay(3000, ct);
            }
        }
    }

    // -------------------------------------------------------------
    // HELPERS
    // -------------------------------------------------------------

    private static string Esc(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private static string NormalizeName(string text)
    {
        return string.Join(
            ' ',
            text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }

    private static bool ValidName(string text)
    {
        var parts = text.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 &&
               text.Length <= 80 &&
               parts.All(x => !x.All(char.IsDigit));
    }

    private bool IsAdmin(long id)
    {
        return _settings.Admins.Contains(id);
    }

    private static object MainMenu()
    {
        return Tg.Keyboard(
            Data.All.Select(x =>
                new[] { (x.Button, $"st:{x.Key}") }));
    }

    private static object DurationsMenu(Status status)
    {
        var rows = status.Durs
            .Select(x => (x.Label, $"du:{status.Key}:{x.Code}"))
            .Chunk(2)
            .Select(x => x.AsEnumerable())
            .ToList();

        rows.Add(new[] { ("⬅️ Отмена", "cancel") });

        return Tg.Keyboard(rows);
    }

    // -------------------------------------------------------------
    // VALIDATION: PERIOD
    // -------------------------------------------------------------

    private static bool ValidatePeriod(
        string text,
        out string error)
    {
        error = "";

        var parts = Regex.Split(text, @"\s*[–—-]\s*");

        if (parts.Length != 2)
        {
            error = "❌ Укажите период: 27.11 – 30.11";
            return false;
        }

        var formats = new[]
        {
            "dd.MM",
            "d.M",
            "dd.MM.yyyy",
            "d.M.yyyy"
        };

        if (!DateOnly.TryParseExact(
                parts[0],
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start))
        {
            error = "❌ Неправильная дата начала.";
            return false;
        }

        if (!DateOnly.TryParseExact(
                parts[1],
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            error = "❌ Неправильная дата окончания.";
            return false;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);

        // Если год не указан, используем текущий год.

        if (start.Year == 1)
            start = new DateOnly(today.Year, start.Month, start.Day);

        if (end.Year == 1)
            end = new DateOnly(start.Year, end.Month, end.Day);

        // Переход через Новый год.

        if (end < start &&
            parts[0].Length <= 5 &&
            parts[1].Length <= 5 &&
            start.Month > end.Month)
        {
            end = new DateOnly(
                start.Year + 1,
                end.Month,
                end.Day);
        }

        if (start < today)
        {
            error = "❌ Дата начала не может быть в прошлом.";
            return false;
        }

        if (end < start)
        {
            error = "❌ Дата окончания раньше даты начала.";
            return false;
        }

        if (end.DayNumber - start.DayNumber > 365)
        {
            error = "❌ Максимальный период — 365 дней.";
            return false;
        }

        return true;
    }

    // -------------------------------------------------------------
    // VALIDATION: TIME
    // -------------------------------------------------------------

    private static bool ValidTime(string value)
    {
        return TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }

    // -------------------------------------------------------------
    // VALIDATION: DETAILS
    // -------------------------------------------------------------

    private static bool ValidateDetails(
        string key,
        string code,
        string text,
        out string error)
    {
        error = "";

        text = text.Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "❌ Сообщение не может быть пустым.";
            return false;
        }

        if (text.Length > 200)
        {
            error = "❌ Максимальная длина — 200 символов.";
            return false;
        }

        if (code == "period")
        {
            return ValidatePeriod(text, out error);
        }

        if (key == "late" && code == "custom")
        {
            var match = Regex.Match(
                text,
                @"^до\s+(\d{1,2}:\d{2})$",
                RegexOptions.IgnoreCase);

            if (!match.Success || !ValidTime(match.Groups[1].Value))
            {
                error = "❌ Укажите время в формате: до 10:30";
                return false;
            }
        }

        if (key == "leave" && code == "custom")
        {
            var match = Regex.Match(
                text,
                @"^с\s+(\d{1,2}:\d{2})\s+до\s+(\d{1,2}:\d{2})$",
                RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                error = "❌ Используйте формат: с 15:00 до 17:00";
                return false;
            }

            var start = match.Groups[1].Value;
            var end = match.Groups[2].Value;

            if (!ValidTime(start) || !ValidTime(end))
            {
                error = "❌ Некорректное время.";
                return false;
            }

            if (TimeOnly.Parse(start) >= TimeOnly.Parse(end))
            {
                error = "❌ Время окончания должно быть позже начала.";
                return false;
            }
        }

        if (key == "away" && code == "custom")
        {
            var match = Regex.Match(
                text,
                @"^на\s+(\d+)\s+(минут[уы]?|час(?:а|ов)?)$",
                RegexOptions.IgnoreCase);

            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out var amount) ||
                amount <= 0 ||
                amount > 1440)
            {
                error = "❌ Например: на 20 минут или на 1 час.";
                return false;
            }
        }

        return true;
    }

    // -------------------------------------------------------------
    // MESSAGE HANDLER
    // -------------------------------------------------------------

    private async Task OnMessage(JsonNode message)
    {
        var chat = message["chat"]!;

        var chatId = chat["id"]!.GetValue<long>();

        var type = chat["type"]!.GetValue<string>();

        var from = message["from"];

        if (from == null)
            return;

        var uid = from["id"]!.GetValue<long>();

        var username = from["username"]?.GetValue<string>();

        var text = message["text"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrEmpty(text))
            return;

        string? command = null;

        var args = "";

        if (text.StartsWith('/'))
        {
            var split = text.Split(
                ' ',
                2,
                StringSplitOptions.RemoveEmptyEntries);

            command = split[0][1..]
                .Split('@')[0]
                .ToLowerInvariant();

            args = split.Length > 1
                ? split[1].Trim()
                : "";
        }

        // ---------------------------------------------------------
        // GROUP
        // ---------------------------------------------------------

        if (type is "group" or "supergroup")
        {
            if (command == "setgroup")
            {
                if (!IsAdmin(uid))
                {
                    await _tg.Send(
                        chatId,
                        "Только администратор может подключить группу.");

                    return;
                }

                _db.SetGroupId(chatId);

                await _tg.Send(
                    chatId,
                    "✅ Группа успешно подключена.");

            }

            return;
        }

        if (type != "private")
            return;

        // ---------------------------------------------------------
        // COMMANDS
        // ---------------------------------------------------------

        switch (command)
        {
            case "start":
                _db.ClearState(uid);

                var existingName = _db.GetName(uid);

                if (existingName != null)
                {
                    await _tg.Send(
                        chatId,
                        $"Привет, <b>{Esc(existingName)}</b>!\n\nВыберите статус:",
                        MainMenu());
                }
                else
                {
                    _db.SetState(uid, "reg");

                    await _tg.Send(
                        chatId,
                        "👋 Добро пожаловать!\n\nНапишите ваши <b>Имя и Фамилию</b>:");
                }

                return;

            case "cancel":
                _db.ClearState(uid);

                await _tg.Send(
                    chatId,
                    "❌ Действие отменено.\nВыберите статус:",
                    MainMenu());

                return;

            case "menu":
                _db.ClearState(uid);

                if (_db.GetName(uid) == null)
                {
                    await _tg.Send(
                        chatId,
                        "Сначала зарегистрируйтесь: /start");

                    return;
                }

                await _tg.Send(
                    chatId,
                    "Выберите статус:",
                    MainMenu());

                return;

            case "name":
                _db.SetState(uid, "reg");

                await _tg.Send(
                    chatId,
                    "Напишите новые <b>Имя и Фамилию</b>:");

                return;

            case "users":
                if (!IsAdmin(uid))
                    return;

                var users = _db.Users();

                if (users.Count == 0)
                {
                    await _tg.Send(chatId, "Пользователей пока нет.");
                    return;
                }

                await _tg.Send(
                    chatId,
                    string.Join("\n", users.Select(x =>
                        $"<code>{x.Id}</code> — {Esc(x.Name)}" +
                        (x.Username != null
                            ? $" (@{Esc(x.Username)})"
                            : ""))));

                return;

            case "rename":
                if (!IsAdmin(uid))
                    return;

                var renameParts = args.Split(
                    ' ',
                    2,
                    StringSplitOptions.RemoveEmptyEntries);

                if (renameParts.Length < 2 ||
                    !long.TryParse(renameParts[0], out var renameId) ||
                    !ValidName(renameParts[1]))
                {
                    await _tg.Send(
                        chatId,
                        "Использование: /rename <code>id Имя Фамилия</code>");

                    return;
                }

                await _tg.Send(
                    chatId,
                    _db.Rename(renameId, NormalizeName(renameParts[1]))
                        ? "✅ Имя изменено."
                        : "Пользователь не найден.");

                return;

            case "add":
                if (!IsAdmin(uid))
                    return;

                var addParts = args.Split(
                    ' ',
                    2,
                    StringSplitOptions.RemoveEmptyEntries);

                if (addParts.Length < 2 ||
                    !long.TryParse(addParts[0], out var addId) ||
                    !ValidName(addParts[1]))
                {
                    await _tg.Send(
                        chatId,
                        "Использование: /add <code>telegram_id Имя Фамилия</code>");

                    return;
                }

                _db.SaveUser(
                    addId,
                    NormalizeName(addParts[1]),
                    null);

                await _tg.Send(chatId, "✅ Сотрудник добавлен.");

                return;

            case "delete":
                if (!IsAdmin(uid))
                    return;

                if (!long.TryParse(args, out var deleteId))
                {
                    await _tg.Send(
                        chatId,
                        "Использование: /delete <code>id</code>");

                    return;
                }

                _db.DeleteUser(deleteId);

                await _tg.Send(chatId, "🗑 Пользователь удалён.");

                return;
        }

        if (command != null)
            return;

        // ---------------------------------------------------------
        // USER REGISTRATION
        // ---------------------------------------------------------

        var state = _db.GetState(uid);

        if (state == null)
        {
            if (_db.GetName(uid) == null)
            {
                _db.SetState(uid, "reg");

                await _tg.Send(
                    chatId,
                    "Напишите ваши <b>Имя и Фамилию</b>.");

                return;
            }

            await _tg.Send(
                chatId,
                "Сначала выберите статус:",
                MainMenu());

            return;
        }

        if (state.Value.State == "reg")
        {
            var fullName = NormalizeName(text);

            if (!ValidName(fullName))
            {
                await _tg.Send(
                    chatId,
                    "❌ Напишите имя и фамилию через пробел.");

                return;
            }

            _db.SaveUser(uid, fullName, username);

            _db.ClearState(uid);

            await _tg.Send(
                chatId,
                $"✅ Сохранено: <b>{Esc(fullName)}</b>\n\nВыберите статус:",
                MainMenu());

            return;
        }

        // ---------------------------------------------------------
        // WAITING FOR DETAILS
        // ---------------------------------------------------------

        if (state.Value.State == "details" &&
            state.Value.Key != null)
        {
            var parts = state.Value.Key.Split(':', 2);

            var statusKey = parts[0];

            var durationCode = parts.Length > 1
                ? parts[1]
                : "";

            if (!ValidateDetails(
                    statusKey,
                    durationCode,
                    text,
                    out var error))
            {
                await _tg.Send(
                    chatId,
                    error + "\n\nПопробуйте ещё раз.");

                return;
            }

            if (!_db.TryTransitionState(
                    uid,
                    "details",
                    state.Value.Key,
                    "sending"))
            {
                await _tg.Send(
                    chatId,
                    "Действие уже обрабатывается.");

                return;
            }

            await Publish(
                uid,
                chatId,
                statusKey,
                text);

            return;
        }

        if (state.Value.State == "duration")
        {
            await _tg.Send(
                chatId,
                "Выберите продолжительность с помощью кнопок.");

            return;
        }

        if (state.Value.State == "sending")
        {
            await _tg.Send(
                chatId,
                "⏳ Ваш статус отправляется. Подождите.");

            return;
        }
    }

    // -------------------------------------------------------------
    // CALLBACK HANDLER
    // -------------------------------------------------------------

    private async Task OnCallback(JsonNode callback)
    {
        var callbackId = callback["id"]!.GetValue<string>();

        var uid = callback["from"]!["id"]!.GetValue<long>();

        var data = callback["data"]?.GetValue<string>() ?? "";

        var message = callback["message"];

        if (message == null)
        {
            await _tg.Answer(callbackId);
            return;
        }

        var chatId = message["chat"]!["id"]!.GetValue<long>();

        var messageId = message["message_id"]!.GetValue<long>();

        if (_db.GetName(uid) == null)
        {
            await _tg.Answer(
                callbackId,
                "Сначала зарегистрируйтесь: /start",
                true);

            return;
        }

        var state = _db.GetState(uid);

        // ---------------------------------------------------------
        // CANCEL
        // ---------------------------------------------------------

        if (data == "cancel")
        {
            _db.ClearState(uid);

            await _tg.Edit(
                chatId,
                messageId,
                "❌ Действие отменено.\n\nВыберите статус:",
                MainMenu());

            await _tg.Answer(callbackId);

            return;
        }

        var parts = data.Split(':');

        // ---------------------------------------------------------
        // SELECT STATUS
        // ---------------------------------------------------------

        if (parts[0] == "st" &&
            parts.Length == 2 &&
            Data.Find(parts[1]) is Status status)
        {
            // Нельзя выбрать второй статус,
            // если предыдущий ещё не завершён.

            if (state != null)
            {
                await _tg.Answer(
                    callbackId,
                    "Сначала завершите или отмените предыдущее действие.",
                    true);

                return;
            }

            await _tg.Answer(callbackId);

            // RETURN TO WORK

            if (status.Key == "back")
            {
                if (!_db.TryStartState(uid, "sending", status.Key))
                    return;

                await Publish(
                    uid,
                    chatId,
                    status.Key,
                    "");

                return;
            }

            // OTHER

            if (status.Key == "other")
            {
                if (!_db.TryStartState(uid, "details", "other:custom"))
                    return;

                await _tg.Edit(
                    chatId,
                    messageId,
                    "Напишите ваше сообщение для группы:");

                return;
            }

            // NORMAL STATUS

            if (!_db.TryStartState(
                    uid,
                    "duration",
                    status.Key))
            {
                return;
            }

            await _tg.Edit(
                chatId,
                messageId,
                $"{status.Emoji} <b>{char.ToUpper(status.Action[0]) + status.Action[1..]}</b>\n\nВыберите срок:",
                DurationsMenu(status));

            return;
        }

        // ---------------------------------------------------------
        // SELECT DURATION
        // ---------------------------------------------------------

        if (parts[0] == "du" &&
            parts.Length == 3 &&
            Data.Find(parts[1]) is Status selectedStatus)
        {
            if (state == null ||
                state.Value.State != "duration" ||
                state.Value.Key != selectedStatus.Key)
            {
                await _tg.Answer(
                    callbackId,
                    "Эта кнопка уже недействительна.",
                    true);

                return;
            }

            var duration = selectedStatus.Durs
                .FirstOrDefault(x => x.Code == parts[2]);

            if (duration == null)
            {
                await _tg.Answer(
                    callbackId,
                    "Неизвестный вариант.",
                    true);

                return;
            }

            await _tg.Answer(callbackId);

            // DIRECT PHRASE

            if (duration.Phrase != null)
            {
                if (!_db.TryTransitionState(
                        uid,
                        "duration",
                        selectedStatus.Key,
                        "sending"))
                {
                    return;
                }

                await Publish(
                    uid,
                    chatId,
                    selectedStatus.Key,
                    duration.Phrase);

                return;
            }

            // CUSTOM DETAILS

            var detailsKey = $"{selectedStatus.Key}:{duration.Code}";

            if (!_db.TryTransitionState(
                    uid,
                    "duration",
                    selectedStatus.Key,
                    "details",
                    detailsKey))
            {
                return;
            }

            await _tg.Edit(
                chatId,
                messageId,
                duration.Ask ?? "Напишите детали:");

            return;
        }

        await _tg.Answer(callbackId);
    }

    // -------------------------------------------------------------
    // PUBLISH STATUS
    // -------------------------------------------------------------

    private async Task Publish(
        long uid,
        long chatId,
        string key,
        string detail)
    {
        var name = _db.GetName(uid);

        if (name == null)
        {
            _db.ClearState(uid);

            await _tg.Send(
                chatId,
                "Сначала зарегистрируйтесь: /start");

            return;
        }

        var group = _db.GetGroupId() ?? _settings.EnvGroup;

        if (group == null)
        {
            _db.ClearState(uid);

            await _tg.Send(
                chatId,
                "⚠️ Группа ещё не подключена.\n\n" +
                "Администратор должен добавить бота в группу " +
                "и отправить там команду /setgroup.");

            return;
        }

        var status = Data.Find(key);

        if (status == null)
        {
            _db.ClearState(uid);

            await _tg.Send(chatId, "Неизвестный статус.");

            return;
        }

        var messageParts = new List<string>
        {
            $"<b>{Esc(name)}</b>",
            status.Emoji
        };

        if (status.Action != "")
            messageParts.Add(status.Action);

        if (detail != "")
            messageParts.Add(Esc(detail));

        var text = string.Join(' ', messageParts);

        try
        {
            await _tg.Send(group.Value, text);

            _db.Log(uid, key, detail);

            _db.ClearState(uid);

            await _tg.Send(
                chatId,
                "✅ <b>Отправлено в группу:</b>\n\n" + text,
                MainMenu());
        }
        catch (Exception e)
        {
            _log.LogError(
                e,
                "Failed to publish status for {UserId}",
                uid);

            _db.ClearState(uid);

            await _tg.Send(
                chatId,
                "❌ Не удалось отправить статус.\n\n" +
                Esc(e.Message),
                MainMenu());
        }
    }
}