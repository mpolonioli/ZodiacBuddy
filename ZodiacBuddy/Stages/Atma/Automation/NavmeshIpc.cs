using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

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

    [EzIPC("Nav.Pathfind")]
    private readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>>? navPathfind;

    [EzIPC("Path.MoveTo")]
    private readonly Action<List<Vector3>, bool>? pathMoveTo;

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

    [EzIPC("Query.Mesh.NearestPoint")]
    private readonly Func<Vector3, float, float, Vector3?>? queryNearestPoint;
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
    ///     Compute a path to the destination without following it.
    /// </summary>
    /// <param name="from">Start position in world coordinates.</param>
    /// <param name="to">Destination in world coordinates.</param>
    /// <param name="fly">Whether to use the flying volume instead of the ground mesh.</param>
    /// <returns>The pathfinding task, or null when the request failed.</returns>
    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to, bool fly)
        => this.Invoke<Task<List<Vector3>>?>(() => this.navPathfind?.Invoke(from, to, fly), null);

    /// <summary>
    ///     Follow an already computed path.
    /// </summary>
    /// <param name="waypoints">Waypoints of the path.</param>
    /// <param name="fly">Whether the path is a flying path.</param>
    public void MoveTo(List<Vector3> waypoints, bool fly)
        => InvokeAction(() => this.pathMoveTo?.Invoke(waypoints, fly));

    /// <summary>
    ///     Pathfind to the destination and follow the path.
    /// </summary>
    /// <param name="destination">Destination in world coordinates.</param>
    /// <param name="fly">Whether to fly there.</param>
    /// <returns>Whether the request was accepted.</returns>
    public bool PathfindAndMoveTo(Vector3 destination, bool fly = false)
        => this.Invoke(() => this.pathfindAndMoveTo?.Invoke(destination, fly) ?? false, false);

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

    /// <summary>
    ///     Find the navmesh point closest to the given position within the given
    ///     search box.
    /// </summary>
    /// <param name="position">Position in world coordinates.</param>
    /// <param name="halfExtentXZ">Horizontal search radius.</param>
    /// <param name="halfExtentY">Vertical search radius.</param>
    /// <returns>The nearest mesh point, or null if none was found.</returns>
    public Vector3? NearestPoint(Vector3 position, float halfExtentXZ, float halfExtentY)
        => this.Invoke<Vector3?>(() => this.queryNearestPoint?.Invoke(position, halfExtentXZ, halfExtentY), null);

    /// <summary>
    ///     Find a navigable point near the given position, whose Y coordinate is
    ///     trustworthy, preferring mesh points on the same vertical layer. On maps
    ///     with stacked terrain (e.g. The Big Bagoly Theory's area, where one piece
    ///     of land sits above another) a plain top-down floor drop would snap to
    ///     the upper layer even though the target is on the lower one.
    /// </summary>
    /// <param name="position">Position in world coordinates, with a correct Y.</param>
    /// <returns>The point on the floor, or null if none was found.</returns>
    public Vector3? FindNavigablePointOnLayer(Vector3 position)
    {
        foreach (var halfExtent in new[] { 5f, 10f, 20f })
        {
            var point = this.NearestPoint(position, halfExtent, 15f);
            if (point is not null)
            {
                return point;
            }
        }

        return this.FindNavigablePoint(position);
    }

    /// <summary>
    ///     Find a navigable point near the given position, whose Y coordinate may
    ///     be unknown, by dropping to the floor with increasing search radii.
    /// </summary>
    /// <param name="approximate">Approximate position in world coordinates.</param>
    /// <returns>The point on the floor, or null if none was found.</returns>
    public Vector3? FindNavigablePoint(Vector3 approximate)
    {
        foreach (var y in new[] { 1024f, 0f })
        {
            foreach (var halfExtent in new[] { 5f, 10f, 20f, 50f })
            {
                var floor = this.PointOnFloor(approximate with { Y = y }, halfExtent);
                if (floor is not null)
                {
                    return floor;
                }
            }
        }

        return null;
    }

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
