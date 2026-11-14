using System.Net.Sockets;
using System.Text;

namespace SkyOmega.DrHook.Engine.Transport;

/// <summary>The ADR-012 Phase 2 transport: a Unix-domain-socket server that publishes the surface-agnostic
/// debug-state to human-launched views. On connect a client receives the session's <b>delta history</b>
/// (what the session emitted before the view connected, oldest first, bounded), then a <b>snapshot</b> (the
/// current <see cref="DebugStateSnapshot"/>, so it renders full state with no prior context — the 2026-06-25
/// ownership directive), then stays live on the <b>delta stream</b> (<see cref="DebugStateDelta"/>). One
/// message per newline-terminated JSON line (NDJSON, Q4).
///
/// <para><b>Why the history is replayed.</b> The snapshot carries the stream counts, not the records, and a
/// hypothesis or an event is in no snapshot at all. Without the history a view that connects after the
/// session started — every view that was waiting for a session connects a moment after it starts — never
/// shows the target's first output or the hypothesis the session was launched with. The history holds the
/// last <c>replayCapacity</c> deltas of the running session; the oldest fall off past that
/// (<see cref="ReplayDropped"/>), and it is emptied when the session ends. The other views wait while one
/// view is sent the history, so a view that does not take it within the send timeout is dropped.</para>
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
/// (drop-oldest + <see cref="DroppedMessages"/> count, like the bounded sinks); a bounded replay history
/// (drop-oldest + <see cref="ReplayDropped"/> count); two background threads
/// joined on <see cref="Stop"/>; the socket file unlinked on <see cref="Start"/> (stale) and on stop.</para>
///
/// <para>BCL-only: <see cref="System.Net.Sockets"/> + <see cref="System.Text.Json"/> ship in the shared
/// framework. The server NEVER writes to stdout/stderr — it must not pollute the MCP JSON-RPC channel
/// (the ADR-011 D2 lesson generalized); a connecting client only ever receives bytes over its socket.</para></summary>
public sealed class DebugStateServer : IDebugEventSink, IDisposable
{
    private readonly record struct Outbound(long Seq, DebugStateSnapshot? Snapshot, DebugStateDelta? Delta);

    // A connected view + the sequence number it joined at. The publisher skips any message with
    // seq <= JoinedSeq for this client: everything published before it connected was sent to it at the
    // join (the deltas as the replayed history, the snapshots as its connect-time snapshot), so
    // re-delivering it would arrive twice or out of order (a stale snapshot after the newer connect
    // snapshot) — the Phase-2 connect-race bug.
    private sealed class ClientConn(Socket socket, long joinedSeq)
    {
        public Socket Socket { get; } = socket;
        public long JoinedSeq { get; } = joinedSeq;
    }

    private readonly string _socketPath;
    private readonly int _maxClients;
    private readonly int _queueCapacity;
    private readonly int _sendTimeoutMs;
    private readonly int _replayCapacity;

    // _queueLock guards the queue, the replay history, the latest snapshot and the sequence counter
    // together: a sequence number is handed out in the same critical section that stores its message, so
    // "everything up to seq N" is exactly what these hold at that moment. The join in AcceptLoop depends on it.
    private readonly LinkedList<Outbound> _queue = new();
    private readonly LinkedList<Outbound> _history = new(); // this session's deltas, oldest first
    private readonly object _queueLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private CancellationTokenSource _shutdownCts = new(); // recreated per Start (see Start) so the server restarts
    private long _droppedMessages;
    private long _workerErrors;
    private long _replayDropped;
    private long _seq;

    private readonly List<ClientConn> _clients = new();
    private readonly object _clientsLock = new();

    private DebugStateSnapshot? _latestSnapshot;

    private Socket? _listener;
    private Thread? _acceptThread;
    private Thread? _publishThread;
    private volatile bool _running;

    /// <summary>Construct a server bound to <paramref name="socketPath"/> (see <see cref="SkyOmega.DrHook.Wire.WireRendezvous.DefaultSocketPath"/>).
    /// Does not bind until <see cref="Start"/>. <paramref name="maxClients"/> caps concurrent views;
    /// <paramref name="queueCapacity"/> bounds the outbound buffer (drop-oldest on overflow).
    /// <paramref name="replayCapacity"/> is how many of the session's most recent deltas a connecting view is
    /// sent before its snapshot (drop-oldest on overflow); 0 turns the replay off. The default matches the
    /// tail a view keeps (<c>DebugStateClientOptions.DeltaHistory</c>).</summary>
    public DebugStateServer(string socketPath, int maxClients = 8, int queueCapacity = 4096, int sendTimeoutMs = 2000,
        int replayCapacity = 1000)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        if (maxClients <= 0) throw new ArgumentOutOfRangeException(nameof(maxClients));
        if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (replayCapacity < 0) throw new ArgumentOutOfRangeException(nameof(replayCapacity));
        _socketPath = socketPath;
        _maxClients = maxClients;
        _queueCapacity = queueCapacity;
        _sendTimeoutMs = sendTimeoutMs;
        _replayCapacity = replayCapacity;
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

    /// <summary>Number of deltas that fell off the replay history to honor its cap. Non-zero means a view
    /// connecting now is not shown the start of the session.</summary>
    public long ReplayDropped => Interlocked.Read(ref _replayDropped);

    /// <summary>Current number of connected views.</summary>
    public int ClientCount { get { lock (_clientsLock) return _clients.Count; } }

    /// <summary>Bind the listener (unlinking any stale socket file first), and start the accept +
    /// publisher threads. Idempotent-guarded: throws if already running. The caller (the MCP host) should
    /// treat a bind failure as non-fatal — the debug session works without the transport.</summary>
    public void Start()
    {
        if (_running) throw new InvalidOperationException("DebugStateServer is already running.");

        // A CancellationTokenSource is single-use; the previous run cancelled it on Stop. Recreate it so the
        // server is RESTARTABLE — the MCP host starts/stops the same server object per debug session.
        if (_shutdownCts.IsCancellationRequested) { _shutdownCts.Dispose(); _shutdownCts = new CancellationTokenSource(); }

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
        Enqueue(snapshot, null);
    }

    // IDebugEventSink — deltas pushed from the pump worker / console-drain / event threads. Must be O(1)
    // and never throw (some call sites are on threads with stack budgets we don't own): build the cheap
    // delta record + enqueue; ALL serialization and socket I/O happens on the publisher thread.
    public void OnEvent(string name)
    {
        if (name is null || !_running) return;
        Enqueue(null, DebugStateDelta.ForEvent(DateTimeOffset.UtcNow, name));
    }

    public void OnLog(LogRecord record)
    {
        if (record is null || !_running) return;
        Enqueue(null, DebugStateDelta.ForLog(record));
    }

    public void OnAnomaly(EngineAnomaly anomaly)
    {
        if (anomaly is null || !_running) return;
        Enqueue(null, DebugStateDelta.ForAnomaly(anomaly));
    }

    public void OnConsoleOutput(ConsoleOutputRecord record)
    {
        if (record is null || !_running) return;
        Enqueue(null, DebugStateDelta.ForConsole(record));
    }

    public void OnHypothesis(HypothesisRecord record)
    {
        if (record is null || !_running) return;
        Enqueue(null, DebugStateDelta.ForHypothesis(record));
    }

    private void Enqueue(DebugStateSnapshot? snapshot, DebugStateDelta? delta)
    {
        lock (_queueLock)
        {
            // Checked under the lock: Stop clears the history and the queue under it after clearing _running,
            // so nothing from a session that has ended can be left behind for the next one to replay.
            if (!_running) return;

            var message = new Outbound(++_seq, snapshot, delta);
            if (snapshot is not null)
            {
                _latestSnapshot = snapshot;
            }
            else if (_replayCapacity > 0)
            {
                if (_history.Count >= _replayCapacity)
                {
                    _history.RemoveFirst();
                    Interlocked.Increment(ref _replayDropped);
                }
                _history.AddLast(message);
            }

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

                // The join. The client is sent the session's delta history, then the current snapshot, and
                // is then registered for the live stream — all under _clientsLock, so no broadcast can reach
                // it ahead of that or slip past between the capture and the registration. What it is sent is
                // captured in one step under _queueLock: every message up to joinedSeq is either a delta in
                // the history or a snapshot no newer than the one captured, so the publisher skips those for
                // this client (seq <= joinedSeq) and delivers everything after. The sequence numbers on its
                // wire only ever rise: the history keeps its own, the snapshot gets a fresh one.
                // Lock order is _clientsLock, then _queueLock; nothing takes them the other way round.
                lock (_clientsLock)
                {
                    if (_clients.Count >= _maxClients) { SafeClose(client); continue; }

                    Outbound[] replay;
                    DebugStateSnapshot? snap;
                    long snapshotSeq = 0, joinedSeq;
                    lock (_queueLock)
                    {
                        replay = new Outbound[_history.Count];
                        _history.CopyTo(replay, 0);
                        snap = _latestSnapshot;
                        if (snap is not null) snapshotSeq = ++_seq;
                        joinedSeq = _seq;
                    }

                    // The other views get nothing while this lock is held, so the whole history has the
                    // time one message has (the send timeout). A view that takes it slower than that is
                    // dropped, as a view that cannot take one message is; without the bound a slow reader
                    // whose every send just succeeds would hold the others back for as long as it liked.
                    long deadline = _sendTimeoutMs > 0 ? Environment.TickCount64 + _sendTimeoutMs : long.MaxValue;
                    bool sent = true;
                    foreach (Outbound m in replay)
                    {
                        string line;
                        // One delta that cannot be serialized must not keep every view out for the rest of
                        // the session: count it, leave it out, send the rest.
                        try { line = DebugStateWireMapper.DeltaLine(m.Delta!, m.Seq); }
                        catch { Interlocked.Increment(ref _workerErrors); continue; }
                        if (!TrySend(client, line) || Environment.TickCount64 > deadline) { sent = false; break; }
                    }
                    if (sent && snap is not null) sent = TrySend(client, DebugStateWireMapper.SnapshotLine(snap, snapshotSeq));
                    if (!sent) { SafeClose(client); continue; }

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
                        ? DebugStateWireMapper.SnapshotLine(s, m.Seq)
                        : DebugStateWireMapper.DeltaLine(m.Delta!, m.Seq);
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

        lock (_queueLock) // the session is over: its snapshot and its history are not the next session's
        {
            _latestSnapshot = null;
            _queue.Clear();
            _history.Clear();
        }
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
