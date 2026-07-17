using ECommons.EzIpcManager;
using System;
using System.Linq;
using System.Numerics;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     IPC wrapper for the vnavmesh plugin.
/// </summary>
internal sealed class NavmeshIpc
{
#pragma warning disable SA1310, CS0649
    [EzIPC("Nav.IsReady")]
    private readonly Func<bool>? navIsReady;

    [EzIPC("Nav.BuildProgress")]
    private readonly Func<float>? navBuildProgress;

    [EzIPC("SimpleMove.PathfindAndMoveTo")]
    private readonly Func<Vector3, bool, bool>? pathfindAndMoveTo;

    [EzIPC("SimpleMove.PathfindAndMoveCloseTo")]
    private readonly Func<Vector3, bool, float, bool>? pathfindAndMoveCloseTo;

    [EzIPC("SimpleMove.PathfindInProgress")]
    private readonly Func<bool>? pathfindInProgress;

    [EzIPC("Path.IsRunning")]
    private readonly Func<bool>? pathIsRunning;

    [EzIPC("Path.Stop")]
    private readonly Action? pathStop;

    [EzIPC("Path.SetTolerance")]
    private readonly Action<float>? pathSetTolerance;

    [EzIPC("Query.Mesh.PointOnFloor")]
    private readonly Func<Vector3, bool, float, Vector3?>? queryPointOnFloor;
#pragma warning restore SA1310, CS0649

    /// <summary>
    ///     Initializes a new instance of the <see cref="NavmeshIpc" /> class.
    /// </summary>
    public NavmeshIpc()
    {
        EzIPC.Init(this, "vnavmesh");
    }

    /// <summary>
    ///     Gets a value indicating whether the vnavmesh plugin is installed and loaded.
    /// </summary>
    public static bool IsInstalled =>
        Service.Interface.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);

    /// <summary>
    ///     Gets a value indicating whether the navmesh of the current zone is ready.
    /// </summary>
    public bool IsReady => this.Invoke(this.navIsReady, false);

    /// <summary>
    ///     Gets the navmesh build progress of the current zone, in percent.
    /// </summary>
    public int BuildProgressPercent => (int)(this.Invoke(this.navBuildProgress, 0f) * 100);

    /// <summary>
    ///     Gets a value indicating whether a pathfind computation is in progress.
    /// </summary>
    public bool IsPathfindInProgress => this.Invoke(this.pathfindInProgress, false);

    /// <summary>
    ///     Gets a value indicating whether a path is currently being followed.
    /// </summary>
    public bool IsPathRunning => this.Invoke(this.pathIsRunning, false);

    /// <summary>
    ///     Pathfind to the destination and follow the path.
    /// </summary>
    /// <param name="destination">Destination in world coordinates.</param>
    /// <returns>Whether the request was accepted.</returns>
    public bool PathfindAndMoveTo(Vector3 destination)
        => this.Invoke(() => this.pathfindAndMoveTo?.Invoke(destination, false) ?? false, false);

    /// <summary>
    ///     Pathfind toward the destination and stop within the given range of it.
    /// </summary>
    /// <param name="destination">Destination in world coordinates.</param>
    /// <param name="range">Distance from the destination at which to stop.</param>
    /// <returns>Whether the request was accepted.</returns>
    public bool PathfindAndMoveCloseTo(Vector3 destination, float range)
        => this.Invoke(() => this.pathfindAndMoveCloseTo?.Invoke(destination, false, range) ?? false, false);

    /// <summary>
    ///     Stop following the current path.
    /// </summary>
    public void Stop()
        => InvokeAction(this.pathStop);

    /// <summary>
    ///     Set the distance from a waypoint at which it counts as reached.
    /// </summary>
    /// <param name="tolerance">Tolerance in yalms.</param>
    public void SetTolerance(float tolerance)
        => InvokeAction(() => this.pathSetTolerance?.Invoke(tolerance));

    /// <summary>
    ///     Find the point on the navmesh floor closest to the given position.
    /// </summary>
    /// <param name="position">Approximate position in world coordinates.</param>
    /// <param name="halfExtentXZ">Horizontal search radius.</param>
    /// <returns>The point on the floor, or null if none was found.</returns>
    public Vector3? PointOnFloor(Vector3 position, float halfExtentXZ)
        => this.Invoke<Vector3?>(() => this.queryPointOnFloor?.Invoke(position, false, halfExtentXZ), null);

    private static void InvokeAction(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "vnavmesh IPC call failed");
        }
    }

    private T Invoke<T>(Func<T>? func, T fallback)
    {
        try
        {
            return func is not null ? func() : fallback;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "vnavmesh IPC call failed");
            return fallback;
        }
    }
}
