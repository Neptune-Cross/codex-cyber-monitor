using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CodexCyberMonitor.Infrastructure;

[Flags]
internal enum InstanceActivationCommand : byte
{
    None = 0,
    Show = 1,
    TestAlert = 2
}

internal static class InstanceActivationCommandExtensions
{
    public static InstanceActivationCommand FromArguments(string[] args)
    {
        var command = InstanceActivationCommand.None;
        if (args.Contains("--show", StringComparer.OrdinalIgnoreCase))
        {
            command |= InstanceActivationCommand.Show;
        }

        if (args.Contains("--test-alert", StringComparer.OrdinalIgnoreCase))
        {
            command |= InstanceActivationCommand.TestAlert;
        }

        if (command == InstanceActivationCommand.None &&
            !args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            command = InstanceActivationCommand.Show;
        }

        return command;
    }
}

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const int MaximumFaultReportsPerInterval = 1;
    private static readonly TimeSpan FaultReportInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _listenerCancellation;
    private NamedPipeServerStream? _activeServer;
    private Task? _listenerTask;
    private DateTime _faultIntervalStartedUtc = DateTime.MinValue;
    private int _faultReportsInInterval;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        var scope = CreateCurrentUserSessionScope();
        _pipeName = $"CodexCyberMonitor.Activation.{scope}";
        _mutex = new Mutex(
            initiallyOwned: true,
            name: $@"Local\CodexCyberMonitor.SingleInstance.{scope}",
            createdNew: out var createdNew);
        _ownsMutex = createdNew;
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    public void StartListening(
        Action<InstanceActivationCommand> activationHandler,
        Action<Exception>? faultHandler = null)
    {
        ArgumentNullException.ThrowIfNull(activationHandler);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!IsFirstInstance)
            {
                throw new InvalidOperationException("只有第一实例可以启动激活通道。");
            }
            if (_listenerTask is not null)
            {
                throw new InvalidOperationException("单实例激活通道已启动。");
            }

            _listenerCancellation = new CancellationTokenSource();
            var cancellationToken = _listenerCancellation.Token;
            _listenerTask = Task.Run(
                () => ListenLoopAsync(activationHandler, faultHandler, cancellationToken),
                CancellationToken.None);
        }
    }

    public bool TrySendActivation(
        InstanceActivationCommand command,
        TimeSpan timeout)
    {
        ThrowIfDisposed();
        if (IsFirstInstance || command == InstanceActivationCommand.None)
        {
            return false;
        }
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect((int)Math.Ceiling(timeout.TotalMilliseconds));
            client.WriteByte((byte)command);
            client.Flush();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void StopListening()
    {
        CancellationTokenSource? cancellation;
        NamedPipeServerStream? activeServer;
        Task? listenerTask;
        lock (_gate)
        {
            cancellation = _listenerCancellation;
            activeServer = _activeServer;
            listenerTask = _listenerTask;
            _listenerCancellation = null;
            _activeServer = null;
            _listenerTask = null;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            activeServer?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 取消期间通道可能已被对端关闭。
        }

        if (listenerTask is not null)
        {
            try
            {
                listenerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(inner =>
                    inner is OperationCanceledException or ObjectDisposedException))
            {
                // 正常取消。
            }
        }

        cancellation.Dispose();
    }

    private async Task ListenLoopAsync(
        Action<InstanceActivationCommand> activationHandler,
        Action<Exception>? faultHandler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 64,
                    outBufferSize: 0);

                lock (_gate)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        server.Dispose();
                        break;
                    }

                    _activeServer = server;
                }

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var commandBuffer = new byte[1];
                var bytesRead = await server.ReadAsync(commandBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 1 && TryValidateCommand(commandBuffer[0], out var command))
                {
                    try
                    {
                        activationHandler(command);
                    }
                    catch (Exception exception)
                    {
                        ReportFault(faultHandler, exception);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                ReportFault(faultHandler, exception);
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                ReportFault(faultHandler, exception);
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeServer, server))
                    {
                        _activeServer = null;
                    }
                }

                server?.Dispose();
            }
        }
    }

    private void ReportFault(Action<Exception>? faultHandler, Exception exception)
    {
        if (faultHandler is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _faultIntervalStartedUtc >= FaultReportInterval)
        {
            _faultIntervalStartedUtc = now;
            _faultReportsInInterval = 0;
        }
        if (_faultReportsInInterval >= MaximumFaultReportsPerInterval)
        {
            return;
        }

        _faultReportsInInterval++;
        try
        {
            faultHandler(exception);
        }
        catch
        {
            // IPC 故障处理不得终止监听循环。
        }
    }

    private static bool TryValidateCommand(
        byte rawCommand,
        out InstanceActivationCommand command)
    {
        const InstanceActivationCommand supportedCommands =
            InstanceActivationCommand.Show | InstanceActivationCommand.TestAlert;
        command = (InstanceActivationCommand)rawCommand;
        return command != InstanceActivationCommand.None &&
               (command & ~supportedCommands) == InstanceActivationCommand.None;
    }

    private static string CreateCurrentUserSessionScope()
    {
        string userIdentity;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            userIdentity = identity.User?.Value ?? identity.Name;
        }
        catch (SystemException)
        {
            userIdentity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        }

        using var process = Process.GetCurrentProcess();
        var sessionId = process.SessionId;
        var scopeBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{userIdentity}|session:{sessionId}"));
        return Convert.ToHexString(scopeBytes)[..24].ToLowerInvariant();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopListening();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 所有权已在进程终止路径中释放。
            }
        }

        _mutex.Dispose();
    }
}
