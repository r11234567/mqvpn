using SQLite;

namespace TruckersMP.Proxy.Database;

internal sealed class SQLiteHelper : IDisposable
{
    private static readonly Lazy<SQLiteHelper> _instance = new(() => new SQLiteHelper());
    public static SQLiteHelper Instance => _instance.Value;

    private readonly object _lock = new();
    private SQLiteConnection? _db;
    private SQLiteAsyncConnection? _dbAsync;
    private string? _dbPath;

    public void Initialize(string dbPath)
    {
        lock (_lock)
        {
            if (_dbPath == dbPath && _db is not null)
                return;

            _db?.Close();
            _db?.Dispose();

            _dbPath = dbPath;
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var flags = SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex;
            _db = new SQLiteConnection(dbPath, flags);
            _dbAsync = new SQLiteAsyncConnection(dbPath, flags);

            _db.CreateTable<ProxyProfile>();
            _db.CreateTable<AppSettingItem>();
        }
    }

    public SQLiteConnection GetConnection() =>
        _db ?? throw new InvalidOperationException("SQLiteHelper 未初始化");

    public SQLiteAsyncConnection GetAsyncConnection() =>
        _dbAsync ?? throw new InvalidOperationException("SQLiteHelper 未初始化");

    public Dictionary<string, string> GetAllSettings()
    {
        lock (_lock)
        {
            return GetConnection().Table<AppSettingItem>().ToList().ToDictionary(i => i.Key, i => i.Value);
        }
    }

    public void SaveAllSettings(Dictionary<string, string> settings)
    {
        lock (_lock)
        {
            var conn = GetConnection();
            conn.RunInTransaction(() =>
            {
                foreach (var kvp in settings)
                {
                    conn.InsertOrReplace(new AppSettingItem { Key = kvp.Key, Value = kvp.Value });
                }
            });
        }
    }

    public List<ProxyProfile> GetAllProfiles()
    {
        lock (_lock)
        {
            return GetConnection().Table<ProxyProfile>().OrderBy(p => p.SortOrder).ToList();
        }
    }

    public void SaveProfiles(IEnumerable<ProxyProfile> profiles)
    {
        lock (_lock)
        {
            var conn = GetConnection();
            conn.RunInTransaction(() =>
            {
                foreach (var profile in profiles)
                {
                    conn.InsertOrReplace(profile);
                }
            });
        }
    }

    public void SaveProfile(ProxyProfile profile)
    {
        lock (_lock)
        {
            GetConnection().InsertOrReplace(profile);
        }
    }

    public void DeleteProfile(string profileId)
    {
        lock (_lock)
        {
            GetConnection().Delete<ProxyProfile>(profileId);
        }
    }

    public void DeleteAllProfiles()
    {
        lock (_lock)
        {
            GetConnection().DeleteAll<ProxyProfile>();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _db?.Close();
                _db?.Dispose();
            }
            finally
            {
                _db = null;
                _dbAsync = null;
            }
        }
    }
}

[Table("AppSettings")]
internal sealed class AppSettingItem
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
