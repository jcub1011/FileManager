using FileManager.Core.Trash;

namespace FileManager.Core.Simulation;

/// <summary>
/// An <see cref="ITrashService"/> that never moves anything — it exists only so the dry-run's
/// <see cref="MirrorPlanner"/> can be constructed without a real trash implementation. The dry-run
/// calls <c>MirrorPlanner.Plan</c> exclusively (never <c>Execute</c>/<c>Reconcile</c>), so
/// <see cref="MoveToTrash"/> is never invoked; should that ever change, it fails loudly rather than
/// silently deleting, preserving the engine's "mutates nothing" guarantee by construction.
/// </summary>
internal sealed class NoOpTrashService : ITrashService
{
    /// <summary>The shared instance.</summary>
    public static NoOpTrashService Instance { get; } = new();

    private NoOpTrashService()
    {
    }

    /// <inheritdoc/>
    public TrashResult MoveToTrash(string path) =>
        throw new InvalidOperationException(
            "DryRunEngine must never trash a file; MirrorPlanner.Plan does not call MoveToTrash.");
}
