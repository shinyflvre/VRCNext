#if WINDOWS
using System.Windows.Forms;

namespace VRCNext.Services.Helpers;

internal sealed class WindowsTrayHelper
{
    private readonly Action _initialize;
    private readonly Action _onReady;
    private readonly Action<Exception> _onFailure;
    private Thread? _thread;
    private SynchronizationContext? _context;
    private Action? _cleanup;
    private int _failureReported;
    private volatile bool _stopRequested;
    private volatile bool _isReady;
    private volatile bool _hasFailed;

    public WindowsTrayHelper(Action initialize, Action onReady, Action<Exception> onFailure)
    {
        _initialize = initialize;
        _onReady = onReady;
        _onFailure = onFailure;
    }

    public bool IsReady => _isReady;
    public bool HasFailed => _hasFailed;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run)
        {
            Name = "WindowsTray",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Post(SendOrPostCallback callback, object? state)
    {
        Volatile.Read(ref _context)?.Post(callback, state);
    }

    public void Stop(Action cleanup)
    {
        _cleanup = cleanup;
        _stopRequested = true;
        Post(_ => Shutdown(), null);
        _thread?.Join(3000);
    }

    private void Run()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.ThreadException += (_, e) =>
            {
                ReportFailure(e.Exception);
                Application.ExitThread();
            };

            var context = new WindowsFormsSynchronizationContext();
            Volatile.Write(ref _context, context);
            SynchronizationContext.SetSynchronizationContext(context);

            _initialize();
            _isReady = true;
            _onReady();
            if (_stopRequested)
                Shutdown();
            else
                Application.Run();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
        finally
        {
            _isReady = false;
        }
    }

    private void Shutdown()
    {
        try
        {
            _cleanup?.Invoke();
        }
        finally
        {
            _isReady = false;
            Application.ExitThread();
        }
    }

    private void ReportFailure(Exception exception)
    {
        _isReady = false;
        _hasFailed = true;
        if (Interlocked.Exchange(ref _failureReported, 1) != 0) return;
        try { _onFailure(exception); } catch { }
    }
}
#endif