## What's New in 2026.6.0

### Log Levels

- Added more logs to analyze potential issues of users in future.
- Added logs to see how memory is being used.
- Added more REST API logs to see how requests are handled.

### Refactor / Core Changes

- Full refactor of MainForm.cs. The file previously acted as a large bridge between backend services and the wwwroot frontend, which resulted in deeply nested logic and tight coupling between components. The refactor separates responsibilities more clearly and improves the structure of the communication between services and the UI. This makes the codebase easier to read, maintain, and extend for both contributors and myself.

### Fixes

This update mainly focuses on performance and stability improvements. Several parts of the application could keep resources alive longer than intended, which over time could increase memory usage or create unnecessary background activity. This update ensures that resources are properly disposed, event handlers are correctly unsubscribed, and background tasks are restarted safely when needed. The goal is to prevent potential leaks and keep the application stable during long sessions.

- Fixed WS event handler accumulation in StartWebSocket by unsubscribing before resubscribing.
- Fixed CancellationTokenSource leak in VRChatWebSocketService.Start by disposing the old CTS.
- Fixed _connectTask zombie by canceling and awaiting the old task before starting a new one.
- Fixed WaveStream not being disposed in VoiceFightService.OpenAudioFile.
- Fixed five SemaphoreSlim instances not being disposed by adding Dispose in OnFormClosing.
- Fixed FileSystemWatcher (_vrcPhotoWatcher) not being disposed in OnFormClosing.
- Fixed HttpClient not being disposed in WebhookService.
- Fixed global JavaScript event listeners in init.js not being removed.
- Fixed _logWatcher not being disposed in OnFormClosing. The object implements IDisposable and runs a background polling timer.
- Fixed _webView (WebView2) not being disposed in OnFormClosing. The control also had an active WebMessageReceived event handler that is now properly cleaned up during shutdown.
- Fixed _userDetailCache size cap (max 200 entries) - have to experiment with that though.

These are just some additional fixes. C# is pretty reliable with its GC but however just to be safe we dispose some things. I might need to refactor things once we switch to Photino.NET but for now this will do the trick.
