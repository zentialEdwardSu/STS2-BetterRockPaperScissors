using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Runs;
using STS2BetterRockPaperScissors.Conflict;
using STS2BetterRockPaperScissors.Fight;
using STS2BetterRockPaperScissors.Settings;

namespace STS2BetterRockPaperScissors.Net;

/// <summary>One authoritative round result: moves aligned to participants, plus the survivor bitmask.</summary>
/// <param name="Moves">Null when the session ended before this round resolved (aborted).</param>
/// <param name="AliveMask">Survivors after the round (bit i = participant i is still in).</param>
internal readonly record struct ConflictRoundResult(List<byte>? Moves, uint AliveMask);

/// <summary>Terminal session result: status plus the winning participant slot.</summary>
internal readonly record struct ConflictSessionResult(byte Status, byte WinnerSlot, int WinnerSide);

/// <summary>
/// Routes the mod's conflict messages to/from the active resolver, and owns the per-session buffers.
///
/// Deliberately a separate hub from <see cref="RpsNetHub"/>: the relic-fight hub is lifecycle-scoped to a
/// single award animation, while this one has to be registered for the whole run (the first conflict can
/// happen before the first relic room). Messages are delivered on the main thread during NetService.Update,
/// so the buffers need no locking.
/// </summary>
internal static class ConflictRpsNetHub
{
    // Client -> host gestures, keyed by (session, round, participant slot). Buffered so an early arrival
    // simply waits for the host to reach that round.
    private static readonly Dictionary<(byte session, byte round, byte slot), byte> _moves = new();

    // Host -> client round announcements and results.
    private static readonly Dictionary<(byte session, byte round), (byte countdown, bool allowReset)> _roundSettings = new();
    private static readonly Dictionary<(byte session, byte round), TaskCompletionSource<bool>> _roundBegan = new();
    private static readonly Dictionary<(byte session, byte round), TaskCompletionSource<ConflictRoundResult>> _roundResolved = new();
    private static readonly Dictionary<byte, TaskCompletionSource<ConflictSessionResult>> _sessionResolved = new();

    private static INetGameService? _registeredFor;

    // The host's net id, learned from the sender of any host -> client message. Lets a client align its
    // (cosmetic) countdown bar with the host's.
    private static ulong? _hostId;

    /// <summary>True once handlers were installed on the current net service.</summary>
    internal static bool IsRegistered { get; private set; }

    private static INetGameService? Net => RunManager.Instance?.NetService;

    /// <summary>
    /// Installs the message handlers, idempotently per net service instance. Called from the map-selection
    /// synchronizer's constructor (i.e. once per run on every peer, before any room can raise a conflict) and
    /// again right before a host starts a session.
    /// </summary>
    internal static void EnsureRegistered(INetGameService? net)
    {
        if (net == null)
            return;
        if (IsRegistered && ReferenceEquals(_registeredFor, net))
            return;

        if (_registeredFor != null)
            Unregister(_registeredFor);

        _registeredFor = net;
        _hostId = null;
        IsRegistered = true;
        net.RegisterMessageHandler<ConflictRpsBeganMessage>(OnBegan);
        net.RegisterMessageHandler<ConflictRpsRoundBeganMessage>(OnRoundBegan);
        net.RegisterMessageHandler<ConflictRpsMoveMessage>(OnMove);
        net.RegisterMessageHandler<ConflictRpsRoundResolvedMessage>(OnRoundResolved);
        net.RegisterMessageHandler<ConflictRpsResolvedMessage>(OnSessionResolved);
        Entry.Logger.Info($"[conflict net] handlers registered (net type {net.Type}, local id {net.NetId})");
    }

    private static void Unregister(INetGameService net)
    {
        try
        {
            net.UnregisterMessageHandler<ConflictRpsBeganMessage>(OnBegan);
            net.UnregisterMessageHandler<ConflictRpsRoundBeganMessage>(OnRoundBegan);
            net.UnregisterMessageHandler<ConflictRpsMoveMessage>(OnMove);
            net.UnregisterMessageHandler<ConflictRpsRoundResolvedMessage>(OnRoundResolved);
            net.UnregisterMessageHandler<ConflictRpsResolvedMessage>(OnSessionResolved);
        }
        catch (Exception e)
        {
            Entry.Logger.Warn($"[conflict net] failed to unregister handlers from the previous net service: {e.Message}");
        }
    }

    // ----- Host side: announcing rounds and collecting moves -----

    /// <summary>Host: announces a new session. Also seeds round 0 so a client is ready to prompt immediately.</summary>
    internal static void BroadcastBegan(ConflictRpsSession session, byte countdownSeconds, bool allowReset)
    {
        var participants = new List<byte>(session.Slots.Count);
        foreach (int slot in session.Slots)
            participants.Add((byte)slot);

        PrepareRound(session.Id, 0, countdownSeconds, allowReset);
        INetGameService? net = Net;
        if (net == null)
            return;

        net.SendMessage(new ConflictRpsBeganMessage
        {
            sessionId = session.Id,
            kind = (byte)session.Kind,
            countdownSeconds = countdownSeconds,
            allowReset = (byte)(allowReset ? 1 : 0),
            participants = participants,
        });
        Entry.Logger.Info(
            $"[conflict net] host broadcasting begin: {session.Describe()} countdown {countdownSeconds}s allowReset {allowReset}");
    }

    /// <summary>Host: announces round <paramref name="round"/> (round 0 rides on the began message).</summary>
    internal static void BroadcastRoundBegan(byte sessionId, int round, byte countdownSeconds, bool allowReset)
    {
        PrepareRound(sessionId, round, countdownSeconds, allowReset);
        INetGameService? net = Net;
        if (net == null)
            return;

        net.SendMessage(new ConflictRpsRoundBeganMessage
        {
            sessionId = sessionId,
            round = (byte)round,
            countdownSeconds = countdownSeconds,
            allowReset = (byte)(allowReset ? 1 : 0),
        });
        Entry.Logger.Debug($"[conflict net] host broadcasting round begin: session {sessionId} round {round} countdown {countdownSeconds}s");
    }

    /// <summary>Host: broadcasts the authoritative result of one round.</summary>
    internal static void BroadcastRoundResolved(byte sessionId, int round, IReadOnlyList<byte> moves, uint aliveMask)
    {
        INetGameService? net = Net;
        if (net == null)
            return;

        net.SendMessage(new ConflictRpsRoundResolvedMessage
        {
            sessionId = sessionId,
            round = (byte)round,
            moves = moves.ToList(),
            aliveMask = aliveMask,
        });
        Entry.Logger.Debug(
            $"[conflict net] host broadcasting round {round} of session {sessionId}: moves [{string.Join(",", moves)}] alive 0x{aliveMask:X}");
    }

    /// <summary>Host: broadcasts the terminal state of the session.</summary>
    internal static void BroadcastResolved(byte sessionId, ConflictSessionStatus status, int winnerSlot, int winnerSide)
    {
        INetGameService? net = Net;
        if (net == null)
            return;

        net.SendMessage(new ConflictRpsResolvedMessage
        {
            sessionId = sessionId,
            status = (byte)status,
            winnerSlot = (byte)Math.Max(winnerSlot, 0),
            winnerSide = winnerSide,
        });
        Entry.Logger.Info(
            $"[conflict net] host broadcasting session {sessionId} resolved: {status} winning side {winnerSide} (representative slot {winnerSlot})");
    }

    /// <summary>Host: latest gesture received from <paramref name="slot"/> for a round, or null.</summary>
    internal static byte? TryGetMove(byte sessionId, int round, int slot)
    {
        return _moves.TryGetValue((sessionId, (byte)round, (byte)slot), out byte move) ? move : null;
    }

    // ----- Client side: sending moves and awaiting host messages -----

    /// <summary>Client: sends the local player's gesture for a round (re-sent on every re-choose).</summary>
    internal static void SendMove(byte sessionId, int round, byte move)
    {
        INetGameService? net = Net;
        if (net == null)
            return;

        net.SendMessage(new ConflictRpsMoveMessage
        {
            sessionId = sessionId,
            round = (byte)round,
            move = move,
        });
        Entry.Logger.Debug($"[conflict net] client sending move: session {sessionId} round {round} move {move}");
    }

    /// <summary>Countdown + re-choose flag the host announced for a round.</summary>
    internal static (byte countdown, bool allowReset) RoundSettings(byte sessionId, int round)
    {
        if (_roundSettings.TryGetValue((sessionId, (byte)round), out var settings))
            return settings;

        return ((byte)ModConfig.CountdownSeconds, ModConfig.AllowReset);
    }

    /// <summary>Client: awaits the host's announcement of a round.</summary>
    internal static Task<bool> AwaitRoundBegan(byte sessionId, int round) =>
        GetOrCreate(_roundBegan, (sessionId, (byte)round)).Task;

    /// <summary>Client: awaits the host's authoritative result for a round.</summary>
    internal static Task<ConflictRoundResult> AwaitRoundResolved(byte sessionId, int round) =>
        GetOrCreate(_roundResolved, (sessionId, (byte)round)).Task;

    /// <summary>Client: awaits the terminal state of the session.</summary>
    internal static Task<ConflictSessionResult> AwaitSessionResolved(byte sessionId) =>
        GetOrCreate(_sessionResolved, sessionId).Task;

    /// <summary>
    /// Estimated one-way latency to the host in seconds (half the round-trip ping), or 0 when unknown. Used
    /// to phase-align the cosmetic countdown bar so both bars empty at the same wall-clock instant.
    /// </summary>
    internal static double OneWayLatencySeconds()
    {
        if (_hostId is not ulong hostId || Net == null)
            return 0.0;

        ConnectionStats? stats = Net.GetStatsForPeer(hostId);
        if (stats == null)
            return 0.0;

        double oneWay = stats.PingMsec / 1000.0 / 2.0;
        if (oneWay < 0.0) oneWay = 0.0;
        if (oneWay > 1.0) oneWay = 1.0;
        return oneWay;
    }

    /// <summary>Drops every buffer belonging to <paramref name="sessionId"/> and releases any waiter.</summary>
    internal static void ClearSession(byte sessionId)
    {
        foreach ((byte session, byte round, byte slot) key in _moves.Keys.Where(k => k.session == sessionId).ToList())
            _moves.Remove(key);
        foreach ((byte session, byte round) key in _roundSettings.Keys.Where(k => k.session == sessionId).ToList())
            _roundSettings.Remove(key);

        foreach ((byte session, byte round) key in _roundBegan.Keys.Where(k => k.session == sessionId).ToList())
        {
            _roundBegan[key].TrySetResult(false);
            _roundBegan.Remove(key);
        }

        foreach ((byte session, byte round) key in _roundResolved.Keys.Where(k => k.session == sessionId).ToList())
        {
            _roundResolved[key].TrySetResult(new ConflictRoundResult(null, 0));
            _roundResolved.Remove(key);
        }

        if (_sessionResolved.TryGetValue(sessionId, out TaskCompletionSource<ConflictSessionResult>? terminal))
        {
            terminal.TrySetResult(new ConflictSessionResult((byte)ConflictSessionStatus.Aborted, 0, ConflictRpsSession.NoSide));
            _sessionResolved.Remove(sessionId);
        }
    }

    // ----- Message handlers -----

    private static void OnBegan(ConflictRpsBeganMessage message, ulong senderId)
    {
        _hostId = senderId;
        Entry.Logger.Info($"[conflict net] client received {message}");
        PrepareRound(message.sessionId, 0, message.countdownSeconds, message.allowReset != 0);
        ConflictRpsClient.OnBegan(message);
    }

    private static void OnRoundBegan(ConflictRpsRoundBeganMessage message, ulong senderId)
    {
        Entry.Logger.Debug($"[conflict net] client received {message}");
        PrepareRound(message.sessionId, message.round, message.countdownSeconds, message.allowReset != 0);
    }

    private static void OnMove(ConflictRpsMoveMessage message, ulong senderId)
    {
        if (!RpsMove.IsValidMove(message.move))
        {
            Entry.Logger.Warn($"[conflict net] ignoring invalid move {message.move} from {senderId} ({message})");
            return;
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return;

        int slot = runState.GetPlayerSlotIndex(senderId);
        if (slot < 0)
            return;

        _moves[(message.sessionId, message.round, (byte)slot)] = message.move;
        Entry.Logger.Debug($"[conflict net] host received move from {senderId} (slot {slot}): {message}");
    }

    private static void OnRoundResolved(ConflictRpsRoundResolvedMessage message, ulong senderId)
    {
        Entry.Logger.Debug($"[conflict net] client received {message}");
        GetOrCreate(_roundResolved, (message.sessionId, message.round))
            .TrySetResult(new ConflictRoundResult(message.moves, message.aliveMask));
    }

    private static void OnSessionResolved(ConflictRpsResolvedMessage message, ulong senderId)
    {
        Entry.Logger.Info($"[conflict net] client received {message}");

        // Mark the session before anything else can observe it: the stock map/event animation seams may run later
        // in this same message batch, and they must see the session as already decided.
        ConflictRpsClient.OnResolved(message.sessionId, message.status, message.winnerSlot, message.winnerSide);

        GetOrCreate(_sessionResolved, message.sessionId)
            .TrySetResult(new ConflictSessionResult(message.status, message.winnerSlot, message.winnerSide));

        // Release anything still waiting on a round that will now never resolve, so no client loop can hang.
        foreach ((byte session, byte round) key in _roundResolved.Keys.Where(k => k.session == message.sessionId).ToList())
            _roundResolved[key].TrySetResult(new ConflictRoundResult(null, 0));
        foreach ((byte session, byte round) key in _roundBegan.Keys.Where(k => k.session == message.sessionId).ToList())
            _roundBegan[key].TrySetResult(false);
    }

    // ----- Helpers -----

    private static void PrepareRound(byte sessionId, int round, byte countdownSeconds, bool allowReset)
    {
        var key = (sessionId, (byte)round);
        _roundSettings[key] = (countdownSeconds, allowReset);
        GetOrCreate(_roundBegan, key).TrySetResult(true);
    }

    private static TaskCompletionSource<T> GetOrCreate<TKey, T>(
        Dictionary<TKey, TaskCompletionSource<T>> buffer,
        TKey key)
        where TKey : notnull
    {
        if (!buffer.TryGetValue(key, out TaskCompletionSource<T>? source))
        {
            source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            buffer[key] = source;
        }

        return source;
    }
}
