using Photino.NET;
using Newtonsoft.Json.Linq;
using VRCNext.Services;
using System.Diagnostics;

namespace VRCNext;

public partial class MainForm
{
    private PhotinoWindow _window = null!;
    private string _imgCacheDir = "";
    private string _thumbCacheDir = "";
    private string _vrcPhotoDir = "";
    private int _httpPort;
    private System.Net.HttpListener? _httpListener;
    private System.Threading.Timer? _uptimeTimer2;
    private bool _minimized;
    private readonly HashSet<string> _recentlyClosedLocs = new();
    private string _lastVideoUrl = "";
    private DateTime _lastVideoUrlTime = DateTime.MinValue;
    private string _lastAvatarName = "";

    private readonly AppSettings _settings;
    private readonly WebhookService _webhook = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly VRChatApiService _vrcApi = new();
    private readonly VRChatLogWatcher _logWatcher = new();
    private ChatboxService? _chatbox;
    private SteamVRService? _steamVR;
    private OscService? _osc;
    // Favorite friends: userId -> fvrt_xxx id
    private readonly Dictionary<string, string> _favoriteFriends = new();
    private ImageCacheService? _imgCache;
    private readonly CacheHandler _cache = new();
    private readonly SemaphoreSlim _friendsRefreshLock = new(1, 1);
    private DateTime _wsDisconnectedAt = DateTime.MinValue;
    private static readonly System.Text.RegularExpressions.Regex _vrcImgUrlRegex = new(
        @"""(https://(?:api\.vrchat\.cloud|assets\.vrchat\.com|files\.vrchat\.cloud)[^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly List<WebhookService.PostRecord> _postHistory = new();
    private bool _relayRunning;
    private DateTime _relayStart;
    private double _totalSizeMB;
    private int _fileCount;
    private VRChatWebSocketService? _wsService;
    private Process? _vcProcess;
    private readonly UserTimeTracker _timeTracker;
    private readonly WorldTimeTracker _worldTimeTracker;
    private readonly PhotoPlayersStore _photoPlayersStore;
    private readonly TimelineService _timeline;
    private readonly UpdateService _updateService = new();
    private readonly MemoryTrimService _memTrim = new();
    private string _lastTrackedWorldId = "";
    private FileSystemWatcher? _vrcPhotoWatcher;

    // Voice Fight
    private VoiceFightService? _voiceFight;
    private VoiceFightSettings _vfSettings = VoiceFightSettings.Load();

    // Discord Rich Presence
    private DiscordPresenceService? _discordPresence;
    private DateTime _discordJoinedAt = DateTime.MinValue;
    private string   _myVrcStatus     = "active"; // updated on login + status change

    // Tracks which userIds currently have a background profile refresh in-flight (deduplication)
    private readonly HashSet<string> _profileRefreshInFlight = new();

    // Library file cache: quick-scan result for pagination
    private List<LibFileEntry> _libFileCache = new();
    private int _libFileCacheTotal = 0;

    // Timeline: cumulative instance player tracking
    private readonly Dictionary<string, (string displayName, string image)> _cumulativeInstancePlayers = new();
    private readonly HashSet<string>   _meetAgainThisInstance = new();
    private string?                    _pendingInstanceEventId;
    private System.Threading.Timer?    _instanceSnapshotTimer;
    private bool                       _logWatcherBootstrapped;
    private string                     _currentVrcUserId = "";

    // Cached instance data — updated by VrcGetCurrentInstanceAsync, used for instant join/leave pushes
    private string _cachedInstLocation  = "";
    private string _cachedInstWorldName = "";
    private string _cachedInstWorldThumb = "";
    private int    _cachedInstCapacity  = 0;
    private string _cachedInstType      = "";

    // Live friend store — seeded by REST on startup, kept fresh by WebSocket events
    private readonly Dictionary<string, JObject>             _friendStore      = new(); // userId -> latest user JObject

    // Friends Timeline: per-friend state for change detection
    private readonly Dictionary<string, string>              _friendLastLoc        = new(); // userId -> location
    private readonly Dictionary<string, string>              _friendLastStatus     = new(); // userId -> status
    private readonly Dictionary<string, string>              _friendLastStatusDesc = new(); // userId -> statusDescription
    private readonly Dictionary<string, string>              _friendLastBio        = new(); // userId -> bio
    private readonly Dictionary<string, (string name, string image)> _friendNameImg = new(); // userId -> name+image
    private bool                                             _friendStateSeeded = false;

    // VRCX import — path retained between preview and start
    private string _vrcxImportPath = "";

    // Timeline image/world resolution session caches — prevents repeated API calls for already-resolved data
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tlPlayerImageCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tlWorldThumbCache  = new(); // "" = tried but 404

    private System.Threading.CancellationTokenSource _tlFetchCts  = new();
    private System.Threading.CancellationTokenSource _ftlFetchCts = new();
}
