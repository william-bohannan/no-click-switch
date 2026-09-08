using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace NoClickSwitch;

/// <summary>
/// One live process per user session. A second Start Menu / Run / logon launch
/// activates the existing bars instead of stacking another set. <see cref="ReplaceArg"/>
/// asks the current process to exit so a new one can take over.
/// </summary>
internal static class SingleInstance
{
    public const string ReplaceArg = "--replace";

    private const string MutexName = @"Local\NoClickSwitch.SingleInstance";
    private const string ShowName = @"Local\NoClickSwitch.Activate";
    private const string ExitName = @"Local\NoClickSwitch.Exit";

    private static Mutex? _mutex;
    private static EventWaitHandle? _show;
    private static EventWaitHandle? _exit;
    private static CancellationTokenSource? _listen;

    /// <summary>
    /// True if this process should start the UI. False if another instance
    /// is already running (it was shown, or asked to exit for a takeover).
    /// </summary>
    public static bool TryEnter(IReadOnlyList<string> args)
    {
        var replace = args.Any(a => string.Equals(a, ReplaceArg, StringComparison.OrdinalIgnoreCase));

        try
        {
            _mutex = MutexAcl.Create(initiallyOwned: true, MutexName, out var created, MutexSecurity());
            _show = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowName, out _, EventSecurity());
            _exit = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ExitName, out _, EventSecurity());

            if (created)
            {
                StartListening();
                return true;
            }

            // Create() with initiallyOwned does not take a pre-existing mutex.
            if (replace)
            {
                try { _exit.Set(); } catch { /* other process may be exiting */ }
                if (WaitTakeover(TimeSpan.FromSeconds(10)))
                {
                    StartListening();
                    return true;
                }

                return false;
            }

            try { _show.Set(); } catch { /* ignore */ }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Cross-integrity open denied — treat as already running.
            if (replace)
                TryKillOtherProcesses();
            return replace;
        }
        catch
        {
            // Don't block the app if the kernel objects fail.
            return true;
        }
    }

    public static void Dispose()
    {
        try { _listen?.Cancel(); } catch { /* ignore */ }
        _listen = null;
        try { _show?.Dispose(); } catch { /* ignore */ }
        try { _exit?.Dispose(); } catch { /* ignore */ }
        _show = null;
        _exit = null;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // not owned
        }

        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }

    private static bool WaitTakeover(TimeSpan timeout)
    {
        if (_mutex is null)
            return false;
        try
        {
            return _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StartListening()
    {
        if (_show is null || _exit is null)
            return;

        _listen = new CancellationTokenSource();
        var ct = _listen.Token;
        var show = _show;
        var exit = _exit;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var handles = new WaitHandle[] { show, exit };
            while (!ct.IsCancellationRequested)
            {
                int which;
                try
                {
                    which = WaitHandle.WaitAny(handles, 400);
                }
                catch
                {
                    break;
                }

                if (which == WaitHandle.WaitTimeout)
                    continue;
                if (which == 0)
                    Dispatch(() => BarCoordinator.Instance.EnsureBarsVisible());
                else if (which == 1)
                    Dispatch(ExitThisProcess);
            }
        });
    }

    private static void ExitThisProcess()
    {
        try { BarCoordinator.Instance.Shutdown(); } catch { /* ignore */ }
        try { Application.Current?.Shutdown(); } catch { /* ignore */ }
    }

    private static void Dispatch(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
                return;
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch
        {
            // shutting down
        }
    }

    private static void TryKillOtherProcesses()
    {
        try
        {
            var me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(AppInstaller.AppName))
            {
                try
                {
                    if (p.Id == me)
                        continue;
                    p.Kill(entireProcessTree: false);
                }
                catch
                {
                    // next
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static MutexSecurity MutexSecurity()
    {
        var sec = new MutexSecurity();
        sec.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        return sec;
    }

    private static EventWaitHandleSecurity EventSecurity()
    {
        var sec = new EventWaitHandleSecurity();
        sec.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            EventWaitHandleRights.FullControl,
            AccessControlType.Allow));
        return sec;
    }
}
