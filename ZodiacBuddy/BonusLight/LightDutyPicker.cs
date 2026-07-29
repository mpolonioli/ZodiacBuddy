using System;
using System.Linq;

namespace ZodiacBuddy.BonusLight;

/// <summary>
///     Picks the duty a light automation should run next.
/// </summary>
public static class LightDutyPicker
{
    /// <summary>
    ///     Find the duty currently carrying a light bonus that is worth running.
    ///     Several duties can hold a bonus at once, so the richest one wins, and
    ///     only duties the runner can actually reach are considered.
    /// </summary>
    /// <param name="hasPath">Tells whether a duty can be run, e.g. whether
    ///     AutoDuty knows a path for it.</param>
    /// <returns>The territory and name of the duty, or null when no bonus is
    ///     active or none of the bonus duties can be run.</returns>
    public static (uint TerritoryId, string Name)? FindBonusDuty(Func<uint, bool> hasPath)
    {
        var candidates = Service.Configuration.BonusLight.ActiveBonus
            .Select(territoryId => BonusLightDuty.TryGetValue(territoryId, out var duty) && duty is not null
                ? ((uint TerritoryId, BonusLightDuty Duty)?)(territoryId, duty)
                : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!.Value)
            .OrderByDescending(candidate => candidate.Duty.DefaultLightIntensity)
            .ThenBy(candidate => candidate.Duty.DutyName, StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (hasPath(candidate.TerritoryId))
            {
                return (candidate.TerritoryId, candidate.Duty.DutyName);
            }

            Service.PluginLog.Debug(
                $"[LightDutyPicker] Skipping the bonus duty {candidate.Duty.DutyName}: it cannot be run.");
        }

        return null;
    }
}
