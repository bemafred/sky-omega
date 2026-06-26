using System.Net.Sockets;
using System.Text;

namespace SkyOmega.DrHook.Engine.Transport;

/// <summary>The ADR-012 Phase 2 transport: a Unix-domain-socket server that publishes the surface-agnostic
/// debug-state to human-launched views. On connect a client receives a <b>snapshot</b> (the current
/// <see cref="DebugStateSnapshot"/>, so it renders full state with no prior context — the 2026-06-25
/// ownership directive), then stays live on the <b>delta stream</b> (<see cref="DebugStateDelta"/>). One
/// message per newline-terminated JSON line (NDJSON, Q4).
///
/// <para><b>Why this is concurrency-safe.</b> The server handles ONLY immutable snapshots + deltas — it
/// never calls into <see cref="DebugSession"/>. The session <i>driver</i> (the MCP request thread that owns
/// stepping) captures the snapshot after each stop and pushes it via <see cref="PublishSnapshot"/>; deltas
/// arrive via the <see cref="IDebugEventSink"/> fan-out. So there is no transport↔stepping race (ADR-012
/// Phase 2 decision). Per Q1/Q6 the host is the listener and views are connect-in clients; per Q3 a
/// terminated view (a broken pipe) is silently dropped and never affects the session.</para>
///
/// <para><b>Resource account</b> (<c>feedback_resource_limit_class_audit</c>): the listener FD + at most
/// <see cref="_maxClients"/> client FDs (excess connections refused); a bounded outbound queue
/// (drop-oldest + <see cref="DroppedMessages"/> count, like the bounded sinks); two background threads
/// joined on <see cref="Stop"/>; the socket file unlinked on <see cref="Start"/> (stale) and on stop.</para>
///
/// <para>BCL-only: <see cref="System.Net.Sockets"/> + <see cref="System.Text.Json"/> ship in the shared
/// framework. The server NEVER writes to stdout/stderr — it must not pollute the MCP JSON-RPC channel
/// (the ADR-011 D2 lesson generalized); a connecting client only ever receives bytes over its socket.</para></summary>
public sealed class DebugStateServer : IDebugEventSink, IDisposable
{
    private readonly record struct Outbound(long Seq, DebugStateSnapshot? Snapshot, DebugStateDelta? Delta);

    // A connected view + the sequence number it joined at. The publisher skips any message with
    // seq <= JoinedSeq for this client: everything published before it connected is already reflected in
    // its connect-time snapshot, so re-delivering it would arrive out of order (a stale snapshot after the
    // newer connect snapshot) — the Phase-2 connect-race bug.
    private sealed class ClientConn(Socket socket, long joinedSeq)
    {
        public Socket Socket { get; } = socket;
        public long JoinedSeq { get; } = joinedSeq;
    }

    private readonly string _socketPath;
    private readonly int _maxClients;
    private readonly int _queueCapacity;
    private readonly int _sendTimeoutMs;

    private readonly LinkedList<Outbound> _queue = new();
    private readonly object _queueLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _shutdownCts = new();
    private long _droppedMessages;
    private long _workerErrors;
    private long _seq;

    private readonly List<ClientConn> _clients = new();
    private readonly object _clientsLock = new();

    private volatile DebugStateSnapshot? _latestSnapshot;

    private Socket? _listener;
    private Thread? _acceptThread;
    private Thread? _publishThread;
    private volatile bool _running;

    /// <summary>Construct a server bound to <paramref name="socketPath"/> (see <see cref="DefaultSocketPath"/>).
    /// Does not bind until <see cref="Start"/>. <paramref name="maxClients"/> caps concurrent views;
    /// <paramref name="queueCapacity"/> bounds the outbound buffer (drop-oldest on overflow).</summary>
    public DebugStateServer(string socketPath, int maxClients = 8, int queueCapacity = 4096, int sendTimeoutMs = 2000)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        if (maxClients <= 0) throw new ArgumentOutOfRangeException(nameof(maxClients));
        if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _socketPath = socketPath;
        _maxClients = maxClients;
        _queueCapacity = queueCapacity;
        _sendTimeoutMs = sendTimeoutMs;
    }

    /// <summary>The well-known per-host rendezvous path (ADR-012 Q7) — one active session at a time, so a
    /// fixed path is the simplest rendezvous. macOS uses the Sky Omega data-dir convention
    /// (<c>~/Library/SkyOmega/drhook/session.sock</c>); other POSIX uses an XDG-style path.</summary>
    public static string DefaultSocketPath()
    {
        string home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "SkyOmega", "drhook")
            : Path.Combine(home, ".local", "share", "sky-omega", "drhook");
        return Path.Combine(dir, "session.sock");
    }

    /// <summary>True once <see cref="Start"/> has bound the listener and not yet stopped.</summary>
    public bool IsRunning => _running;

    /// <summary>Number of outbound messages SILENTLY DROPPED to honor the queue cap (the honesty marker,
    /// parallel to the bounded sinks). Non-zero means a consumer fell behind a flood.</summary>
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <summary>Number of times a worker thread (accept or publisher) caught and swallowed an error
    /// (serialization / send) instead of dying. The honesty marker for the "a worker must never die
    /// silently" rule — a non-zero count means messages were lost to an error, NOT to capacity. Surfaced so
    /// a silent thread death can never again masquerade as a client hang (the Phase-2 reflection-Serialize
    /// bug).</summary>
    public long WorkerErrors => Interlocked.Read(ref _workerErrors);

    /// <summary>Current number of connected views.</summary>
    public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

    /// <summary>Bind the listener (unlinking any stale socket file first), and start the accept +
    /// publisher threads. Idempotent-guarded: throws if already running. The caller (the MCP host) should
    /// treat a bind failure as non-fatal — the debug session works without the transport.</summary>
    public void Start()
    {
        if (_running) throw new InvalidOperationException("DebugStateServer is already running.");

        string? dir = Path.GetDirectoryName(_socketPath);
        if (dir is not null) Directory.CreateDirectory(dir);
        TryUnlinkSocketFile();

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        listener.Listen(_maxClients);
        _listener = listener;
        _running = true;

        _publishThread = new Thread(PublishLoop) { IsBackground = true, Name = "drhook-transport-publish" };
        _publishThread.Start();
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "drhook-transport-accept" };
        _acceptThread.Start();
    }

    /// <summary>Push a fresh snapshot — called by the session driver after each stop / state change. Caches
    /// it for connect-time init and broadcasts it to connected views. The snapshot is immutable, so this is
    /// safe to call from the MCP request thread while views read on other threads.</summary>
    public void PublishSnapshot(DebugStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_running) return;
        _latestSnapshot = snapshot;
        Enqueue(new Outbound(Interlocked.Increment(ref _seq), snapshot, null));
    }

    // IDebugEventSink — deltas pushed from the pump worker / console-drain / event threads. Must be O(1)
    // and never throw (some call sites are on threads with stack budgets we don't own): build the cheap
    // delta record + enqueue; ALL serialization and socket I/O happens on the publisher thread.
    public void OnEvent(string name)
    {
        if (name is null || !_running) return;
        Enqueue(new Outbound(Interlocked.Increment(ref _seq), null, DebugStateDelta.ForEvent(DateTimeOffset.UtcNow, name)));
    }

    public void OnLog(LogRecord record)
    {
        if (record is null || !_running) return;
        Enqueue(new Outbound(Interlocked.Increment(ref _seq), null, DebugStateDelta.ForLog(record)));
    }

    public void OnAnomaly(EngineAnomaly anomaly)
    {
        if (anomaly is null || !_running) return;
        Enqueue(new Outbound(Interlocked.Increment(ref _seq), null, DebugStateDelta.ForAnomaly(anomaly)));
    }

    public void OnConsoleOutput(ConsoleOutputRecord record)
    {
        if (record is null || !_running) return;
        Enqueue(new Outbound(Interlocked.Increment(ref _seq), null, DebugStateDelta.ForConsole(record)));
    }

    private void Enqueue(Outbound message)
    {
        lock (_queueLock)
        {
            if (_queue.Count >= _queueCapacity)
            {
                _queue.RemoveFirst();
                Interlocked.Increment(ref _droppedMessages);
            }
            _queue.AddLast(message);
        }
        _wake.Set();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            Socket client;
            // AcceptAsync with a cancellation token — NOT blocking Accept(): on Stop the token is cancelled so
            // this returns and the thread exits, instead of leaving an in-flight Accept that makes the listener's
            // Close() deadlock (the macOS Socket.Close-while-Accept-in-flight hang — found via DrHook + a probe).
            try { client = _listener!.AcceptAsync(_shutdownCts.Token).AsTask().GetAwaiter().GetResult(); }
            catch { break; } // cancelled on Stop (or listener closed) — exit the accept loop
            if (!_running) { SafeClose(client); break; }

            // A worker thread must NEVER die on a transient error (serialize / send): that turns the error
            // into a SILENT hang for the connecting view (the Phase-2 reflection-Serialize bug). Drop just
            // this connection, count it, and keep accepting.
            try
            {
                client.SendTimeout = _sendTimeoutMs;

                // Everything published up to joinedSeq is reflected in the connect-time snapshot, so this
                // client skips any in-flight broadcast with seq <= joinedSeq (else a stale snapshot could
                // arrive after the newer connect snapshot — out of order). Serialize the snapshot OUTSIDE the
                // lock (immutable); send it BEFORE registering so a concurrent broadcast can't reach this
                // client ahead of its snapshot.
                long joinedSeq = Interlocked.Read(ref _seq);
                DebugStateSnapshot? snap = _latestSnapshot;
                string? snapshotLine = snap is null ? null : DebugStateWireSerializer.SnapshotLine(snap, Interlocked.Increment(ref _seq));

                lock (_clientsLock)
                {
                    if (_clients.Count >= _maxClients) { SafeClose(client); continue; }
                    if (snapshotLine is not null && !TrySend(client, snapshotLine)) { SafeClose(client); continue; }
                    _clients.Add(new ClientConn(client, joinedSeq));
                }
            }
            catch
            {
                Interlocked.Increment(ref _workerErrors);
                SafeClose(client);
            }
        }
    }

    private void PublishLoop()
    {
        var batch = new List<Outbound>();
        while (_running)
        {
            _wake.Wait();
            _wake.Reset(); // reset BEFORE draining so a message enqueued during the drain re-signals us
            if (!_running) break;

            batch.Clear();
            lock (_queueLock)
            {
                foreach (Outbound m in _queue) batch.Add(m);
                _queue.Clear();
            }
            if (batch.Count == 0) continue;

            foreach (Outbound m in batch)
            {
                // Never let one bad message kill the publisher thread (→ a silent hang for ALL views).
                try
                {
                    string line = m.Snapshot is { } s
                        ? DebugStateWireSerializer.SnapshotLine(s, m.Seq)
                        : DebugStateWireSerializer.DeltaLine(m.Delta!, m.Seq);
                    Broadcast(m.Seq, line);
                }
                catch
                {
                    Interlocked.Increment(ref _workerErrors);
                }
            }
        }
    }

    private void Broadcast(long seq, string line)
    {
        lock (_clientsLock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (seq <= _clients[i].JoinedSeq) continue; // pre-join — already covered by this client's connect snapshot
                if (!TrySend(_clients[i].Socket, line)) // a broken pipe = a terminated view — drop it; the session is unaffected
                {
                    SafeClose(_clients[i].Socket);
                    _clients.RemoveAt(i);
                }
            }
        }
    }

    private static bool TrySend(Socket client, string line)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            int sent = 0;
            while (sent < bytes.Length)
            {
                int n = client.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                if (n <= 0) return false;
                sent += n;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Close the listener, drop all client connections, join the threads, and unlink the socket
    /// file. Closing client connections never calls back into the session (ADR-012: session-end drops view
    /// connections, but a view drop never affects the session). Idempotent.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _shutdownCts.Cancel(); // cancel the pending AcceptAsync so the accept thread exits BEFORE we Close the
                               // listener below — else Close deadlocks on the in-flight Accept (the macOS hang).
        _wake.Set();           // unblock the publisher

        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _publishThread?.Join(TimeSpan.FromSeconds(2));
        _acceptThread = null;
        _publishThread = null;

        try { _listener?.Close(); } catch { } // safe now — the accept thread has exited; no in-flight Accept
        _listener = null;

        lock (_clientsLock)
        {
            foreach (ClientConn c in _clients) SafeClose(c.Socket);
            _clients.Clear();
        }

        _latestSnapshot = null;
        lock (_queueLock) _queue.Clear();
        TryUnlinkSocketFile();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
        _shutdownCts.Dispose();
    }

    private void TryUnlinkSocketFile()
    {
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { /* best effort */ }
    }

    private static void SafeClose(Socket s)
    {
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        try { s.Close(); } catch { }
    }
}
