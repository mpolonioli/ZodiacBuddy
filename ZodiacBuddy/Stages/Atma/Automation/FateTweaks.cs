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
    ///     Gets the name fragments (case-insensitive) of the only enemies that
    ///     advance progress in FATEs where the area also holds hostiles that
    ///     count for nothing. Non-matching enemies are only fought when they
    ///     attack us first.
    /// </summary>
    public static IReadOnlyDictionary<uint, string[]> ProgressMobs { get; } = new Dictionary<uint, string[]>
    {
        // The Enemy of My Enemy (East Shroud): the sylphs' brawl with the imperial
        // scouts fields other enemies that leave the progress bar untouched.
        [610] = ["3rd Cohort Secutor", "Wild Jackal"],
    };

    /// <summary>
    ///     Gets replacement approach points (map coordinates) for FATEs whose
    ///     centre is a bad place to head for: under an overlapping piece of
    ///     terrain, where any floor query is ambiguous between the two layers,
    ///     or off walkable ground, where a flying approach cannot land. The
    ///     replacement point is inside the FATE area but clear of the problem,
    ///     so approaching it always resolves to solid, landable ground.
    /// </summary>
    public static IReadOnlyDictionary<uint, Vector2> ApproachPoints { get; } = new Dictionary<uint, Vector2>
    {
        // The Big Bagoly Theory (Eastern Thanalan): only a small portion around
        // the centre is overlapped by the upper terrain layer.
        [543] = new(30.1f, 25.4f),

        // The Enemy of My Enemy (East Shroud): the centre sits off walkable
        // ground, so a flying approach hovers there without ever landing. This
        // point is solid ground next to the NPC that starts the FATE.
        [610] = new(28.0f, 21.0f),
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
