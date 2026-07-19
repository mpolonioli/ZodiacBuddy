namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Combat plugin driving the fights of the Trial of the Braves automations.
/// </summary>
public enum CombatPlugin
{
    /// <summary>
    ///     BossMod (vbm) or BossMod Reborn: runs the rotation and dodges AoEs.
    /// </summary>
    BossMod,

    /// <summary>
    ///     Wrath Combo: runs the rotation only, without any movement.
    /// </summary>
    WrathCombo,
}
