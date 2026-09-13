using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ForgeDevelopment.Native;

internal sealed class AsyncReportWriter : IDisposable
{
    private const int MaxPendingPaths = 8;
    private const int DisposeTimeoutMilliseconds = 10_000;

    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly LinkedList<Request> _pending = new();
    private readonly Dictionary<string, LinkedListNode<Request>> _byPath = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Action<Exception> _onError;
    private readonly Thread _worker;
    private bool _accepting = true;
    private bool _disposed;

    internal AsyncReportWriter(Action<Exception> onError)
    {
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _worker = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "Forge Runtime Report Writer"
        };
        _worker.Start();
    }

    internal bool Enqueue(DiagnosticsReport report, string path, string outcome)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        lock (_gate)
        {
            if (!_accepting) return false;
        }

        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_accepting) return false;
            // Reject before capturing when no pending slot can be used. Holding the
            // queue gate makes capture/coalescing order match request order.
            if (!_byPath.ContainsKey(fullPath) && _pending.Count >= MaxPendingPaths) return false;
            var request = new Request(report.Freeze(outcome), fullPath);
            if (_byPath.TryGetValue(fullPath, out var existing))
            {
                existing.Value = request;
            }
            else
            {
                _byPath.Add(fullPath, _pending.AddLast(request));
            }

            _workAvailable.Set();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            if (Thread.CurrentThread == _worker)
                throw new InvalidOperationException("The report writer cannot be disposed from its error callback.");
            lock (_gate) _accepting = false;
            _workAvailable.Set();
            if (!_worker.Join(DisposeTimeoutMilliseconds))
                throw new TimeoutException("Timed out waiting for pending diagnostic reports to finish writing.");
            _workAvailable.Dispose();
            _disposed = true;
        }
    }

    private void WriteLoop()
    {
        while (true)
        {
            Request? request = null;
            lock (_gate)
            {
                if (_pending.First != null)
                {
                    var node = _pending.First;
                    request = node.Value;
                    _pending.RemoveFirst();
                    _byPath.Remove(request.Path);
                }
                else if (!_accepting)
                {
                    return;
                }
            }

            if (request == null)
            {
                _workAvailable.WaitOne();
                continue;
            }

            try
            {
                request.Snapshot.Export(request.Path);
            }
            catch (Exception error)
            {
                try
                {
                    _onError(error);
                }
                catch
                {
                    // An error reporter must not terminate the single writer thread.
                }
            }
        }
    }

    private sealed record Request(DiagnosticsReport.FrozenReport Snapshot, string Path);
}
