using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Runs;

namespace STS2BetterRockPaperScissors.Net;

/// <summary>
/// Routes the mod's custom relic-fight messages to/from the active fight resolver.
///
/// Lifetime: registered for the duration of one relic-awarding orchestration (registered when the
/// patch takes over, unregistered when it finishes). Net messages are delivered on the main thread
/// during NetService.Update, so the buffers here need no locking.
/// </summary>
public static class RpsNetHub
{
    // Buffered client->host moves keyed by (relicKey, round, netId). Lets early arrivals wait for
    // the host to reach that round.
    private static readonly Dictionary<(byte relic, byte round, ulong netId), byte> _receivedMoves = new();

    // Host->client resolved rounds. Clients await these; the host fills them locally too.
    private static readonly Dictionary<(byte relic, byte round), TaskCompletionSource<List<byte>>> _resolvedRounds = new();

    // Host->client round-begin announcements (carry the host-authoritative countdown + re-choose flag).
    private static readonly Dictionary<(byte relic, byte round), TaskCompletionSource<(byte countdown, bool allowReset)>> _roundBegan = new();

    // The host's net id, learned from the sender of any host->client message. Lets a client query
    // its latency to the host so it can phase-align its (cosmetic) countdown bar.
    private static ulong? _hostId;

    private static bool _registered;

    private static INetGameService Net => RunManager.Instance.NetService;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        _hostId = null;
        _receivedMoves.Clear();
        _resolvedRounds.Clear();
        _roundBegan.Clear();
        Net.RegisterMessageHandler<RpsMoveChosenMessage>(OnMoveChosen);
        Net.RegisterMessageHandler<RpsRoundResolvedMessage>(OnRoundResolved);
        Net.RegisterMessageHandler<RpsRoundBeganMessage>(OnRoundBegan);
        Entry.Logger.Info($"[RPS net] handlers registered (net type {Net.Type}, local id {Net.NetId})");
    }

    public static void Unregister()
    {
        if (!_registered)
            return;
        _registered = false;
        _hostId = null;
        Net.UnregisterMessageHandler<RpsMoveChosenMessage>(OnMoveChosen);
        Net.UnregisterMessageHandler<RpsRoundResolvedMessage>(OnRoundResolved);
        Net.UnregisterMessageHandler<RpsRoundBeganMessage>(OnRoundBegan);
        _receivedMoves.Clear();
        // Fail any still-pending waiters so awaiting resolvers don't hang on teardown.
        foreach (var tcs in _resolvedRounds.Values)
            tcs.TrySetResult(null);
        _resolvedRounds.Clear();
        foreach (var tcs in _roundBegan.Values)
            tcs.TrySetResult((0, false));
        _roundBegan.Clear();
        Entry.Logger.Info("[RPS net] handlers unregistered");
    }

    // ----- Host side: collecting client moves -----

    private static void OnMoveChosen(RpsMoveChosenMessage message, ulong senderId)
    {
        if (!Fight.RpsMove.IsValidMove(message.move))
        {
            Entry.Logger.Warn($"[RPS net] ignoring invalid move {message.move} from {senderId} ({message})");
            return;
        }
        _receivedMoves[(message.relicKey, message.round, senderId)] = message.move;
        Entry.Logger.Info(
            $"[RPS net] host received move from client {senderId}: relic {message.relicKey} round {message.round} move {(RelicPickingFightMove)message.move}");
    }

    /// <summary>Host: returns a remote fighter's chosen move for a round, or null if not yet received.</summary>
    public static byte? TryGetReceivedMove(byte relicKey, byte round, ulong netId)
    {
        return _receivedMoves.TryGetValue((relicKey, round, netId), out byte move) ? move : (byte?)null;
    }

    /// <summary>Host: broadcasts the authoritative resolved round to every peer.</summary>
    public static void BroadcastResolvedRound(byte relicKey, byte round, List<byte> moves)
    {
        Entry.Logger.Info(
            $"[RPS net] host broadcasting resolved round: relic {relicKey} round {round} moves [{string.Join(",", moves)}]");
        Net.SendMessage(new RpsRoundResolvedMessage
        {
            relicKey = relicKey,
            round = round,
            moves = moves,
        });
    }

    /// <summary>Host: announces a round start and its authoritative countdown to every peer.</summary>
    public static void BroadcastRoundBegan(byte relicKey, byte round, byte countdownSeconds, bool allowReset)
    {
        Entry.Logger.Info(
            $"[RPS net] host broadcasting round-began: relic {relicKey} round {round} countdown {countdownSeconds}s allowReset {allowReset}");
        Net.SendMessage(new RpsRoundBeganMessage
        {
            relicKey = relicKey,
            round = round,
            countdownSeconds = countdownSeconds,
            allowReset = (byte)(allowReset ? 1 : 0),
        });
    }

    private static void OnRoundBegan(RpsRoundBeganMessage message, ulong senderId)
    {
        _hostId = senderId;
        bool allowReset = message.allowReset != 0;
        Entry.Logger.Info(
            $"[RPS net] client received round-began: relic {message.relicKey} round {message.round} countdown {message.countdownSeconds}s allowReset {allowReset}");
        GetOrCreateBegan(message.relicKey, message.round).TrySetResult((message.countdownSeconds, allowReset));
    }

    /// <summary>
    /// Client: estimated one-way latency to the host in seconds (half the round-trip ping), or 0 if
    /// unknown. Used to phase-align the cosmetic countdown bar: the round-began message took roughly
    /// this long to arrive, so the host's bar is already this far drained when the client starts its
    /// own. Capped so a bad ping reading can't swallow the whole countdown.
    /// </summary>
    public static double OneWayLatencySeconds()
    {
        if (_hostId is not ulong hostId)
            return 0.0;
        ConnectionStats stats = Net.GetStatsForPeer(hostId);
        if (stats == null)
            return 0.0;
        double oneWay = stats.PingMsec / 1000.0 / 2.0;
        if (oneWay < 0.0) oneWay = 0.0;
        if (oneWay > 1.0) oneWay = 1.0;
        return oneWay;
    }

    /// <summary>
    /// Client: awaits the host's round-begin announcement; result is the countdown seconds plus the
    /// host's re-choose (reset) setting for this round.
    /// </summary>
    public static Task<(byte countdown, bool allowReset)> AwaitRoundBegan(byte relicKey, byte round)
    {
        return GetOrCreateBegan(relicKey, round).Task;
    }

    private static TaskCompletionSource<(byte countdown, bool allowReset)> GetOrCreateBegan(byte relicKey, byte round)
    {
        var key = (relicKey, round);
        if (!_roundBegan.TryGetValue(key, out var tcs))
        {
            tcs = new TaskCompletionSource<(byte countdown, bool allowReset)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _roundBegan[key] = tcs;
        }
        return tcs;
    }

    // ----- Client side: sending a move + awaiting resolution -----

    /// <summary>Client: sends the local player's chosen move to the host.</summary>
    public static void SendMove(byte relicKey, byte round, byte move)
    {
        Entry.Logger.Info(
            $"[RPS net] client sending move to host: relic {relicKey} round {round} move {(RelicPickingFightMove)move}");
        Net.SendMessage(new RpsMoveChosenMessage
        {
            relicKey = relicKey,
            round = round,
            move = move,
        });
    }

    private static void OnRoundResolved(RpsRoundResolvedMessage message, ulong senderId)
    {
        Entry.Logger.Info(
            $"[RPS net] client received resolved round: relic {message.relicKey} round {message.round} moves [{string.Join(",", message.moves ?? new List<byte>())}]");
        var tcs = GetOrCreateResolved(message.relicKey, message.round);
        tcs.TrySetResult(message.moves);
    }

    /// <summary>Client: awaits the host's resolved round (the authoritative per-fighter move list).</summary>
    public static Task<List<byte>> AwaitResolvedRound(byte relicKey, byte round)
    {
        return GetOrCreateResolved(relicKey, round).Task;
    }

    private static TaskCompletionSource<List<byte>> GetOrCreateResolved(byte relicKey, byte round)
    {
        var key = (relicKey, round);
        if (!_resolvedRounds.TryGetValue(key, out var tcs))
        {
            tcs = new TaskCompletionSource<List<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resolvedRounds[key] = tcs;
        }
        return tcs;
    }
}
