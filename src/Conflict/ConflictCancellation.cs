using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// A cancellation token that fires when the screen owning a conflict leaves the tree.
///
/// Sessions are multi-second asynchronous sequences holding Godot nodes; if the map/event screen is torn down
/// mid-fight (run ends, host disconnects, room transition) the loops must stop touching freed nodes. Callers
/// dispose the returned handle once their session is over so the delegate never leaks on a long-lived node.
/// </summary>
internal static class ConflictCancellation
{
    internal static (CancellationToken Token, Action Dispose) Create(ConflictKind kind)
    {
        var cts = new CancellationTokenSource();
        Node? owner = kind == ConflictKind.MapPath ? (Node?)NMapScreen.Instance : NEventRoom.Instance;

        Action? onTreeExiting = null;
        Node? hooked = null;
        if (owner != null && GodotObject.IsInstanceValid(owner))
        {
            onTreeExiting = cts.Cancel;
            owner.TreeExiting += onTreeExiting;
            hooked = owner;
        }

        void Dispose()
        {
            if (hooked != null && onTreeExiting != null && GodotObject.IsInstanceValid(hooked))
                hooked.TreeExiting -= onTreeExiting;
            cts.Dispose();
        }

        return (cts.Token, Dispose);
    }
}
