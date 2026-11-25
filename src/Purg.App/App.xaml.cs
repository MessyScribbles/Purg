namespace Purg.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\Purg_SingleInstance_Mutex_v1";
    private const string ActivationEventName = "Local\\Purg_SingleInstance_Activate_v1";
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _activationEvent;
    private System.Threading.CancellationTokenSource? _activationCancellation;
    private System.Threading.Thread? _activationListener;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new System.Threading.Mutex(true, MutexName, out var createdNew);
        _activationEvent = new System.Threading.EventWaitHandle(
            false,
            System.Threading.EventResetMode.AutoReset,
            ActivationEventName);

        if (!createdNew)
        {
            _activationEvent.Set();
            _activationEvent.Dispose();
            _activationEvent = null;
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _ownsInstanceMutex = true;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _activationCancellation = new System.Threading.CancellationTokenSource();
        _activationListener = new System.Threading.Thread(ListenForActivation)
        {
            IsBackground = true,
            Name = "purg single-instance activation"
        };
        _activationListener.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activationEvent?.Set();
        _activationListener?.Join();
        _activationCancellation?.Dispose();
        _activationEvent?.Dispose();

        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ListenForActivation()
    {
        var activationEvent = _activationEvent!;
        var cancellation = _activationCancellation!;
        var handles = new System.Threading.WaitHandle[]
        {
            activationEvent,
            cancellation.Token.WaitHandle
        };

        while (!cancellation.IsCancellationRequested)
        {
            var signaled = System.Threading.WaitHandle.WaitAny(handles);
            if (signaled != 0 || cancellation.IsCancellationRequested)
            {
                return;
            }

            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (this.MainWindow is Purg.App.MainWindow window)
                    {
                        window.ActivateFromSecondInstance();
                    }
                }));
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted)
            {
                return;
            }
        }
    }
}
