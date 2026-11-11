using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>Where Cursor (a VS Code fork) keeps its login on Windows and Linux.</summary>
public static class CursorPaths
{
    /// <summary>
    /// VS Code's global state database: <c>%APPDATA%\Cursor\User\globalStorage\state.vscdb</c> on
    /// Windows, <c>~/.config/Cursor/User/globalStorage/state.vscdb</c> (or under <c>$XDG_CONFIG_HOME</c>) on Linux.
    /// </summary>
    public static string DefaultStateDatabase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "Cursor", "User", "globalStorage", "state.vscdb");
}

/// <summary>
/// The Cursor editor's login, from its state database. Read only: the token is
/// never written, refreshed or logged.
/// </summary>
public sealed record CursorLogin(string AccessToken, string? MembershipType)
{
    public const string AccessTokenKey = "cursorAuth/accessToken";
    public const string MembershipTypeKey = "cursorAuth/stripeMembershipType";

    public string? PlanName => CursorUsage.PlanName(MembershipType);

    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"CursorLogin(plan: {PlanName ?? "?"}, token: <redacted>)";

    public static CursorLogin Load(string databasePath)
    {
        if (!File.Exists(databasePath)) throw new UsageException(UsageErrorKind.NotLoggedIn);
        var values = Sqlite.ReadItems(databasePath, [AccessTokenKey, MembershipTypeKey]);
        var token = Clean(values.GetValueOrDefault(AccessTokenKey));
        if (token is null) throw new UsageException(UsageErrorKind.NotLoggedIn);
        return new CursorLogin(token, Clean(values.GetValueOrDefault(MembershipTypeKey)));
    }

    /// <summary>VS Code keeps strings as they are; a JSON-quoted one is accepted too.</summary>
    private static string? Clean(string? value)
    {
        var text = value?.Trim();
        if (text is { Length: >= 2 } && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

/// <summary>
/// <c>aiserver.v1.DashboardService/GetCurrentPeriodUsage</c>: what Cursor's own Settings →
/// Usage screen shows for the current billing cycle. Connect/proto3 JSON: fields are camelCase,
/// zero values are left out, money is in US cents and int64 values (the dates) arrive as strings.
/// </summary>
public static class CursorUsage
{
    /// <summary>A cycle this short is the X Subscription's weekly pool, not a legacy monthly plan.</summary>
    private static readonly TimeSpan WeeklyCycle = TimeSpan.FromDays(10);

    public static ProviderSnapshot? Snapshot(byte[] data, string? plan)
    {
        if (Json.Parse(data) is not JsonObject root) return null;
        var start = Json.Date(root, "billingCycleStart", "billing_cycle_start");
        var end = Json.Date(root, "billingCycleEnd", "billing_cycle_end");
        var cycle = start is { } from && end is { } to && to - from < WeeklyCycle ? "weekly" : "monthly";

        var items = new List<LimitItem>();
        var notes = new List<SnapshotNote>();
        if (Json.Obj(root, "planUsage", "plan_usage") is { } usage)
        {
            var limit = Cents(usage, "limit");
            var included = Cents(usage, "includedSpend", "included_spend");
            // The dashboard's headline. Missing means zero (proto3), unless an older answer only has the spend.
            var total = Percent(usage, "totalPercentUsed", "total_percent_used")
                ?? (limit > 0 ? included * 100.0 / limit : 0);
            items.Add(Item($"cursor-{cycle}", cycle, null, total, end, LimitGroup.Weekly));

            // Plans with two pools: Auto (Cursor's own models) and API (models billed at API prices).
            var auto = Percent(usage, "autoPercentUsed", "auto_percent_used");
            var api = Percent(usage, "apiPercentUsed", "api_percent_used");
            if (auto is not null || api is not null)
            {
                items.Add(Item($"cursor-{cycle}-auto", $"{cycle}_scoped", "Auto", auto ?? 0, end, LimitGroup.Other));
                items.Add(Item($"cursor-{cycle}-api", $"{cycle}_scoped", "API", api ?? 0, end, LimitGroup.Other));
            }

            if (limit > 0)
            {
                notes.Add(new SnapshotNote(NoteKind.IncludedSpend, Amount: included, Limit: limit, Extra: Cents(usage, "bonusSpend", "bonus_spend")));
            }
        }
        if (OnDemand(Json.Obj(root, "spendLimitUsage", "spend_limit_usage")) is { } onDemand) notes.Add(onDemand);

        if (items.Count == 0 && notes.Count == 0) return null;
        return new ProviderSnapshot(ProviderId.Cursor, plan, items, [], notes);
    }

    /// <summary>
    /// Spending past the plan (<c>spendLimitUsage</c>, cents): against the user's own cap first, then
    /// against the team's pool; spending with no cap shows the amount alone.
    /// </summary>
    private static SnapshotNote? OnDemand(JsonObject? spend)
    {
        if (spend is null) return null;
        var used = Cents(spend, "individualUsed", "individual_used");
        if (used == 0) used = Cents(spend, "totalSpend", "total_spend");
        if (Cents(spend, "individualLimit", "individual_limit") is > 0 and var limit)
        {
            return new SnapshotNote(NoteKind.OnDemandSpend, Amount: used, Limit: limit);
        }
        if (Cents(spend, "pooledLimit", "pooled_limit") is > 0 and var pool)
        {
            return new SnapshotNote(NoteKind.TeamOnDemandSpend, Amount: Cents(spend, "pooledUsed", "pooled_used"), Limit: pool);
        }
        return used > 0 ? new SnapshotNote(NoteKind.OnDemandSpend, Amount: used) : null;
    }

    private static LimitItem Item(string id, string kind, string? scope, double percent, DateTimeOffset? reset, LimitGroup group) =>
        new(id, kind, scope, percent, SeverityParser.From(null, percent), reset, group);

    private static double? Percent(JsonNode node, params string[] keys) =>
        Json.Num(node, keys) is { } value && double.IsFinite(value) ? Math.Max(value, 0) : null;

    private static long Cents(JsonNode node, params string[] keys) =>
        Json.Num(node, keys) is { } value && double.IsFinite(value) && value > 0 ? (long)Math.Round(value) : 0;

    /// <summary>
    /// <c>cursorAuth/stripeMembershipType</c>: <c>pro</c> → "Pro", <c>pro_plus</c> → "Pro+",
    /// <c>free</c> → "Hobby", <c>free_trial</c> → "Pro Trial".
    /// </summary>
    public static string? PlanName(string? membership) => membership?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "free" => "Hobby",
        "free_trial" => "Pro Trial",
        var other => BreakdownItem.Capitalize(other.Replace("_plus", "+").Replace("-plus", "+").Replace('_', ' ')),
    };
}

/// <summary>
/// Reads the Cursor editor's login from its state database. The token is read for each
/// request and kept in memory only.
/// </summary>
public sealed class CursorProvider(string? databasePath = null) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");

    private readonly string path = databasePath ?? CursorPaths.DefaultStateDatabase;

    public ProviderId Id => ProviderId.Cursor;

    /// <summary>Cursor is installed and signed in. A database that is busy for a moment still counts.</summary>
    public bool IsAvailable()
    {
        if (!File.Exists(path)) return false;
        try
        {
            CursorLogin.Load(path);
            return true;
        }
        catch (UsageException error)
        {
            return error.Kind != UsageErrorKind.NotLoggedIn;
        }
    }

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default)
    {
        var login = await Task.Run(() => CursorLogin.Load(path), cancellation).ConfigureAwait(false);
        if (Jwt.Expiration(login.AccessToken) is { } expires && expires < DateTimeOffset.UtcNow)
        {
            throw new UsageException(UsageErrorKind.UnauthorizedExpired);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        request.Headers.Add("Connect-Protocol-Version", "1");
        // Cursor's privacy flag; we never send content, only read usage.
        request.Headers.Add("x-ghost-mode", "true");
        var data = await Http.SendAsync(request, cancellation).ConfigureAwait(false);

        var snapshot = CursorUsage.Snapshot(data, login.PlanName) ?? throw new UsageException(UsageErrorKind.Decoding);
        return new ProviderFetchResult(snapshot, ProviderCache.From(login.PlanName, data).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) =>
        ProviderCache.Decode(cache) is { } decoded ? CursorUsage.Snapshot(decoded.RawBytes, decoded.Plan) : null;
}

/// <summary>
/// Just enough SQLite to read VS Code's key/value table (<c>ItemTable</c>), through the copy the
/// system already has: <c>winsqlite3.dll</c> on Windows 10 and later, <c>libsqlite3</c> on Linux and
/// macOS. Nothing is bundled. The database is opened read-only and waits briefly while Cursor writes.
/// </summary>
internal static class Sqlite
{
    private const string Library = "ai-usage-bar-sqlite3";
    private const int Ok = 0;
    private const int Busy = 5;
    private const int Locked = 6;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadOnly = 0x1;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;
    private static readonly IntPtr Transient = new(-1);

    static Sqlite() => NativeLibrary.SetDllImportResolver(typeof(Sqlite).Assembly, Resolve);

    /// <summary>The values of <paramref name="keys"/> in <c>ItemTable</c>; missing keys are left out.</summary>
    public static Dictionary<string, string> ReadItems(string path, IReadOnlyList<string> keys) => Guard(() =>
    {
        var db = Open(path, OpenReadOnly);
        try
        {
            var values = new Dictionary<string, string>();
            foreach (var key in keys)
            {
                var statement = Prepare(db, "SELECT value FROM ItemTable WHERE key = ?1");
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(key);
                    Check(Native.sqlite3_bind_text(statement, 1, bytes, bytes.Length, Transient));
                    var step = Native.sqlite3_step(statement);
                    if (step == Row)
                    {
                        // TEXT or BLOB: both come back as their bytes. The blob pointer is read before its size.
                        var blob = Native.sqlite3_column_blob(statement, 0);
                        var size = Native.sqlite3_column_bytes(statement, 0);
                        values[key] = size == 0 || blob == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(blob, size);
                    }
                    else if (step != Done)
                    {
                        throw Failure(step);
                    }
                }
                finally
                {
                    Native.sqlite3_finalize(statement);
                }
            }
            return values;
        }
        finally
        {
            Native.sqlite3_close(db);
        }
    });

    /// <summary>Runs SQL on a database, creating it if needed; used by tests to build a state database.</summary>
    internal static void Execute(string path, string sql) => Guard(() =>
    {
        var db = Open(path, OpenReadWrite | OpenCreate);
        try
        {
            Check(Native.sqlite3_exec(db, Terminated(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        }
        finally
        {
            Native.sqlite3_close(db);
        }
        return 0;
    });

    private static IntPtr Open(string path, int flags)
    {
        var code = Native.sqlite3_open_v2(Terminated(path), out var db, flags, IntPtr.Zero);
        if (code != Ok)
        {
            if (db != IntPtr.Zero) Native.sqlite3_close(db);
            throw Failure(code);
        }
        Native.sqlite3_busy_timeout(db, 2000);
        return db;
    }

    private static IntPtr Prepare(IntPtr db, string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        Check(Native.sqlite3_prepare_v2(db, bytes, bytes.Length, out var statement, IntPtr.Zero));
        return statement;
    }

    private static void Check(int code)
    {
        if (code != Ok) throw Failure(code);
    }

    /// <summary>
    /// Busy or locked: Cursor is writing, try again later. Anything else (no such table, not a
    /// database) means there is no login to read.
    /// </summary>
    private static UsageException Failure(int code) => (code & 0xFF) is Busy or Locked
        ? new UsageException(UsageErrorKind.LoginUnreadable)
        : new UsageException(UsageErrorKind.NotLoggedIn);

    /// <summary>No SQLite on the system at all.</summary>
    private static T Guard<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new UsageException(UsageErrorKind.LoginUnreadable, inner: error);
        }
    }

    private static byte[] Terminated(string text) => Encoding.UTF8.GetBytes(text + '\0');

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (var candidate in Candidates())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>Windows' own copy by full path, so a stray DLL next to the app is never picked up.</summary>
    private static string[] Candidates() =>
        OperatingSystem.IsWindows() ? [Path.Combine(Environment.SystemDirectory, "winsqlite3.dll")]
        : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib"]
        : ["libsqlite3.so.0", "libsqlite3.so"];

    private static class Native
    {
        [DllImport(Library)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library)]
        public static extern int sqlite3_close(IntPtr db);

        [DllImport(Library)]
        public static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

        [DllImport(Library)]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library)]
        public static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int bytes, IntPtr destructor);

        [DllImport(Library)]
        public static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library)]
        public static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Library)]
        public static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport(Library)]
        public static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library)]
        public static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, IntPtr errorMessage);
    }
}
