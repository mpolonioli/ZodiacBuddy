using System.Collections.Generic;
using System.Numerics;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Per-FATE special-case data for the FATEs automation: FATEs whose progress
///     comes from destroying specific unanimated objects, and FATEs that only
///     spawn after a prerequisite FATE the game's FATEChain column does not
///     record.
/// </summary>
internal static class FateTweaks
{
    /// <summary>
    ///     Gets the FATEs completed by destroying specific unanimated objects
    ///     scattered across the whole FATE area. They take priority over regular
    ///     enemies, and the automation patrols the area looking for more when
    ///     none are in sight.
    /// </summary>
    public static IReadOnlyDictionary<uint, PriorityTarget> PriorityTargets { get; } = new Dictionary<uint, PriorityTarget>
    {
        // Schism (Outer La Noscea): destroy the kobolds' toolboxes.
        [587] = new("toolbox", "kobold toolboxes"),

        // Air Supply (North Shroud): destroy every airstone in the area.
        [633] = new("airstone", "airstones"),
    };

    /// <summary>
    ///     Gets replacement approach points (map coordinates) for FATEs whose
    ///     centre lies under an overlapping piece of terrain, where any floor
    ///     query is ambiguous between the two layers. The replacement point is
    ///     inside the FATE area but clear of the overlap, so approaching it
    ///     always resolves to the correct layer.
    /// </summary>
    public static IReadOnlyDictionary<uint, Vector2> ApproachPoints { get; } = new Dictionary<uint, Vector2>
    {
        // The Big Bagoly Theory (Eastern Thanalan): only a small portion around
        // the centre is overlapped by the upper terrain layer.
        [543] = new(30.1f, 25.4f),
    };

    /// <summary>
    ///     Gets the FATEs that only spawn once another FATE has been completed
    ///     where the Fate sheet's FATEChain column does not record the link
    ///     (it is 0 for all of these). The prerequisites are treated exactly
    ///     like sheet-defined chained FATEs.
    /// </summary>
    public static IReadOnlyDictionary<uint, uint[]> PrerequisiteFates { get; } = new Dictionary<uint, uint[]>
    {
        // Breaching North Tidegate requires Gauging North Tidegate.
        [569] = [568],

        // Breaching South Tidegate requires Gauging South Tidegate.
        [571] = [570],

        // The Enmity of My Enemy requires The Enemy of My Enemy.
        [611] = [610],
    };

    /// <summary>
    ///     An unanimated objective object that a FATE requires destroyed.
    /// </summary>
    /// <param name="NameFragment">Case-insensitive fragment of the object's name.</param>
    /// <param name="DisplayName">Plural display name for status messages.</param>
    public readonly record struct PriorityTarget(string NameFragment, string DisplayName);
}
