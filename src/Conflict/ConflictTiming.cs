using Godot;

namespace STS2BetterRockPaperScissors.Conflict;

/// <summary>
/// Frame-driven waits shared by the conflict presentation code.
///
/// Godot does not install a synchronization context, so awaiting <see cref="Task.Delay(TimeSpan)"/> resumes on a
/// thread-pool thread — and anything that then touches Godot nodes is illegal. Waiting on the scene tree's
/// <c>process_frame</c> signal keeps every continuation on the main thread.
/// </summary>
internal static class ConflictTiming
{
    /// <summary>Waits roughly <paramref name="seconds"/>, accumulating real frame deltas.</summary>
    internal static async Task WaitSecondsAsync(double seconds, CancellationToken token)
    {
        if (seconds <= 0.0)
            return;

        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            return;
        }

        double remaining = seconds;
        while (remaining > 0.0 && !token.IsCancellationRequested)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            remaining -= tree.Root.GetProcessDeltaTime();
        }
    }
}
