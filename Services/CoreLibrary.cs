using System.Collections.Concurrent;
using Photino.NET;
using VRCNext.Services;

namespace VRCNext;

public class CoreLibrary
{
    public VRChatApiService VrcApi { get; }

    public AuthAPI Auth { get; }
    public UsersAPI Users { get; }
    public FriendsAPI Friends { get; }
    public AvatarsAPI Avatars { get; }
    public WorldAPI World { get; }
    public InstancesAPI Instances { get; }
    public InviteAPI Invite { get; }
    public FavoritesAPI Favorites { get; }
    public GroupsAPI Groups { get; }
    public CalendarAPI Calendar { get; }
    public NotificationsAPI Notifications { get; }
    public PlayerModerationAPI PlayerModeration { get; }
    public FilesAPI Files { get; }
    public InventoryAPI Inventory { get; }
    public EconomyAPI Economy { get; }
    public VRChatLogWatcher LogWatcher { get; }
    // DB services are recreated on account switch since disposed instances cannot be reused.
    public TimelineService Timeline { get; internal set; } = null!;
    public AppSettings Settings { get; }
    public CacheHandler Cache { get; }
    public UnifiedTimeEngine TimeEngine { get; internal set; } = null!;
    public PhotoPlayersStore PhotoPlayersStore { get; internal set; } = null!;
    public LocalFavoritesStore LocalFavorites { get; internal set; } = null!;
    public WebhookService Webhook { get; }
    public FileWatcherService FileWatcher { get; }
    public Action<string, object?> SendToJS { get; }
    public Action<string>? AvtrdbSubmit { get; set; }
    public Action<string>? VrcndbSubmit { get; set; }

    public ConcurrentDictionary<string, bool> PlayerAgeVerifiedCache { get; } = new();
    public ConcurrentDictionary<string, Newtonsoft.Json.Linq.JObject> PlayerProfileCache { get; } = new();

    public Dictionary<string, (string name, string thumb)> VrWorldCache { get; } = new();

    public string CurrentVrcUserId { get; set; } = "";
    public string MyVrcStatus { get; set; } = "active";

    // Permini — userId → (allowActive, allowAskMe, allowDnD)
    public Dictionary<string, (bool allowActive, bool allowAskMe, bool allowDnD)> PerminiList { get; } = new();
    public DateTime DiscordJoinedAt { get; set; } = DateTime.MinValue;
    public int HttpPort { get; set; }
    public string CustomThemesDir { get; set; } = "";

    public MemoryTrimService MemTrim { get; }
    public UpdateService UpdateService { get; }

    public PhotinoWindow? Window { get; set; }

    public Func<Task>? PrefetchSharedContent { get; set; }
    public Action? PushDiscordPresence { get; set; }
    public Func<bool>? IsVrcRunning { get; set; }
    public Func<bool>? IsSteamVrRunning { get; set; }
    public Action<string, string, string>? LogSelfProfile { get; set; }
    public Func<string, string>? GetVirtualMediaUrl { get; set; }
    public Action<string>? LoadPage { get; set; }
    public Func<string, Task>? DispatchMessage { get; set; }

    public Action<int>? OnChatboxPauseRequest { get; set; }

#if WINDOWS
    public VRSubprocessHost? VrOverlay { get; set; }
    public Action<string, string, string>? SpeakToast { get; set; }
    public Action<bool, bool>? OnTraySettingChanged { get; set; } // (enabled, autoHideNow)
    public Action<string, string, string, string>? OnTrayUserUpdate { get; set; } // name, status, statusDesc, imageUrl
    public Action? OnTrayLoggedOut { get; set; }
    public Action<Dictionary<string, string>>? OnTrayThemeUpdate { get; set; }
#endif

    // Constructs API wrappers only, the DB services are populated later via LoadDatabaseServices.
    public CoreLibrary(
        VRChatApiService vrcApi,
        VRChatLogWatcher logWatcher,
        AppSettings settings,
        CacheHandler cache,
        WebhookService webhook,
        FileWatcherService fileWatcher,
        MemoryTrimService memTrim,
        UpdateService updateService,
        Action<string, object?> sendToJS)
    {
        VrcApi = vrcApi;
        Auth             = new AuthAPI(vrcApi);
        Users            = new UsersAPI(vrcApi);
        Friends          = new FriendsAPI(vrcApi);
        Avatars          = new AvatarsAPI(vrcApi);
        World            = new WorldAPI(vrcApi);
        Instances        = new InstancesAPI(vrcApi);
        Invite           = new InviteAPI(vrcApi);
        Favorites        = new FavoritesAPI(vrcApi);
        Groups           = new GroupsAPI(vrcApi);
        Calendar         = new CalendarAPI(vrcApi);
        Notifications    = new NotificationsAPI(vrcApi);
        PlayerModeration = new PlayerModerationAPI(vrcApi);
        Files            = new FilesAPI(vrcApi);
        Inventory        = new InventoryAPI(vrcApi);
        Economy          = new EconomyAPI(vrcApi);
        LogWatcher = logWatcher;
        Settings = settings;
        Cache = cache;
        Webhook = webhook;
        FileWatcher = fileWatcher;
        MemTrim = memTrim;
        UpdateService = updateService;
        SendToJS = sendToJS;
    }

    public void TrimCaches(bool force = false)
    {
        VRCNext.Services.Helpers.ImageCacheHelper.TrimIfNeeded(force);
        var profileCount = PlayerProfileCache.Count;
        if (force)
        {
            PlayerProfileCache.Clear();
            PlayerAgeVerifiedCache.Clear();
            SendToJS("log", new { msg = $"[GC] - PlayerProfileCache - Force trimmed {profileCount} entries. 0 Remaining.", color = "sec" });
        }
        else if (profileCount > 300)
        {
            var toRemove = PlayerProfileCache.Keys.Take(profileCount - 150).ToList();
            foreach (var key in toRemove)
            {
                PlayerProfileCache.TryRemove(key, out _);
                PlayerAgeVerifiedCache.TryRemove(key, out _);
            }
            SendToJS("log", new { msg = $"[GC] - PlayerProfileCache - Trimmed {toRemove.Count} Cached files. {PlayerProfileCache.Count} Remaining.", color = "sec" });
        }
        else
        {
            SendToJS("log", new { msg = $"[GC] - PlayerProfileCache - No trim needed. {profileCount}/300 entries.", color = "sec" });
        }

        lock (VrWorldCache)
        {
            var worldCount = VrWorldCache.Count;
            if (force)
            {
                VrWorldCache.Clear();
                SendToJS("log", new { msg = $"[GC] - VrWorldCache - Force trimmed {worldCount} entries. 0 Remaining.", color = "sec" });
            }
            else if (worldCount > 150)
            {
                var toRemove = VrWorldCache.Keys.Take(worldCount - 75).ToList();
                foreach (var key in toRemove)
                    VrWorldCache.Remove(key);
                SendToJS("log", new { msg = $"[GC] - VrWorldCache - Trimmed {toRemove.Count} Cached files. {VrWorldCache.Count} Remaining.", color = "sec" });
            }
            else
            {
                SendToJS("log", new { msg = $"[GC] - VrWorldCache - No trim needed. {worldCount}/150 entries.", color = "sec" });
            }
        }
    }

    public string FixLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http://localhost:")) return url;
        var slash = url.IndexOf('/', "http://localhost:".Length);
        return slash < 0 ? url : $"http://localhost:{HttpPort}{url[slash..]}";
    }

    // Multi-Account lifecycle.

    // Non-reentrant lock that serializes Accounts-list and settings mutations across all account flows.
    internal readonly System.Threading.SemaphoreSlim AccountMutationLock = new(1, 1);

    private readonly object _switchLock = new();
    private bool _switchInProgress;

    public bool IsSwitchInProgress { get { lock (_switchLock) return _switchInProgress; } }

    public bool TryBeginAccountSwitch()
    {
        lock (_switchLock)
        {
            if (_switchInProgress) return false;
            _switchInProgress = true;
            return true;
        }
    }

    public void EndAccountSwitch()
    {
        lock (_switchLock) { _switchInProgress = false; }
    }

    // Callback slots wired by controllers so CoreLibrary can stop and restart account-scoped tasks without hard coupling.
    public Func<Task>? StopAccountScopedTasks { get; set; }
    public Func<Task>? StartAccountScopedTasks { get; set; }

    public async Task StopAccountScopedTasksAsync()
    {
        if (StopAccountScopedTasks != null) await StopAccountScopedTasks();
    }

    public async Task StartAccountScopedTasksAsync()
    {
        if (StartAccountScopedTasks != null) await StartAccountScopedTasks();
    }

    // Disposes the three DB services, which afterwards are unusable due to readonly connection fields.
    public void DisposeDatabaseServices()
    {
        try { Timeline?.Dispose(); } catch (Exception ex) { CrashHandler.WriteEntry("DisposeDatabaseServices.Timeline", ex); }
        try { PhotoPlayersStore?.Dispose(); } catch (Exception ex) { CrashHandler.WriteEntry("DisposeDatabaseServices.PhotoPlayersStore", ex); }
        try { TimeEngine?.Dispose(); } catch (Exception ex) { CrashHandler.WriteEntry("DisposeDatabaseServices.TimeEngine", ex); }
        try { LocalFavorites?.Dispose(); } catch (Exception ex) { CrashHandler.WriteEntry("DisposeDatabaseServices.LocalFavorites", ex); }
    }

    // Creates fresh DB service instances using their static Load factories at the path set on Database.
    public void LoadDatabaseServices()
    {
        TimeEngine        = UnifiedTimeEngine.Load(
            () => IsVrcRunning?.Invoke() ?? false,
            msg => SendToJS("log", new { msg, color = "sec" }));
        PhotoPlayersStore = PhotoPlayersStore.Load();
        Timeline          = TimelineService.Load(Settings);
        LocalFavorites    = LocalFavoritesStore.Load();
    }

}