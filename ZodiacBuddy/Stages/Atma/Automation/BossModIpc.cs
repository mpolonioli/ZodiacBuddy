using ECommons.EzIpcManager;
using ECommons.Throttlers;
using System;
using System.Linq;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     IPC wrapper for the BossMod (vbm) / BossMod Reborn plugin. Control works by
///     creating and activating a ZodiacBuddy autorotation preset that combines the
///     standard per-job rotation modules with BossMod's AI movement module, so the
///     character runs its rotation on our hard target and moves out of AoEs while
///     fighting. The preset the user had active before is restored afterwards.
/// </summary>
internal sealed class BossModIpc
{
    private const string PresetName = "ZodiacBuddy";

    // Same shape as the presets AutoDuty feeds to BossMod: the xan rotation
    // modules for every job (Manual targeting - the automation hard-targets the
    // mob it wants killed, and auto-targeting would pull unrelated mobs), the
    // utility AI modules for self-sustain, and NormalMovement with pathfinding,
    // which is the module that makes BossMod dodge AoEs.
    private const string PresetJson = """
        {
          "Name": "ZodiacBuddy",
          "Modules": {
            "BossMod.Autorotation.xan.BLM": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }],
            "BossMod.Autorotation.xan.PCT": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.RDM": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.SMN": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.AST": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.SCH": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.SGE": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.WHM": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.DRG": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.MNK": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }],
            "BossMod.Autorotation.xan.NIN": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.RPR": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.SAM": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.VPR": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.BRD": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.DNC": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.MCH": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.DRK": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.GNB": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.xan.PLD": [{ "Track": "Targeting", "Option": "Manual" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Buffs", "Option": "Auto" }],
            "BossMod.Autorotation.VeynWAR": [{ "Track": "AOE", "Option": "AutoFinishCombo" }, { "Track": "Burst", "Option": "Spend" }, { "Track": "Potion", "Option": "Manual" }, { "Track": "Infuriate", "Option": "ForceIfNoNC" }, { "Track": "IR", "Option": "Automatic" }, { "Track": "Upheaval", "Option": "Automatic" }, { "Track": "PR", "Option": "Automatic" }, { "Track": "Onslaught", "Option": "Force" }, { "Track": "Tomahawk", "Option": "Opener" }, { "Track": "Wrath", "Option": "Automatic" }],
            "BossMod.Autorotation.xan.HealerAI": [{ "Track": "Heal", "Option": "Enabled" }, { "Track": "Esuna2", "Option": "Enabled" }],
            "BossMod.Autorotation.xan.MeleeAI": [{ "Track": "Second Wind", "Option": "Enabled" }, { "Track": "Bloodbath", "Option": "Enabled" }, { "Track": "Stun", "Option": "Enabled" }],
            "BossMod.Autorotation.xan.RangedAI": [{ "Track": "Head Graze", "Option": "Enabled" }, { "Track": "Second Wind", "Option": "Enabled" }],
            "BossMod.Autorotation.xan.TankAI": [{ "Track": "Stance", "Option": "Enabled" }, { "Track": "Ranged GCD", "Option": "Enabled" }, { "Track": "Low Blow", "Option": "Enabled" }, { "Track": "Arms' Length", "Option": "Enabled" }, { "Track": "Personal mits", "Option": "Enabled" }, { "Track": "Invuln", "Option": "Enabled" }, { "Track": "Interject2", "Option": "Enabled" }],
            "BossMod.Autorotation.MiscAI.StayCloseToTarget": [],
            "BossMod.Autorotation.MiscAI.StayWithinLeylines": [{ "Track": "Use Retrace", "Option": "Yes" }, { "Track": "Use Between The Lines", "Option": "Yes" }],
            "BossMod.Autorotation.MiscAI.NormalMovement": [{ "Track": "Destination", "Option": "Pathfind" }]
          }
        }
        """;

#pragma warning disable SA1310, CS0649
    [EzIPC("Presets.Create")]
    private readonly Func<string, bool, bool>? presetsCreate;

    [EzIPC("Presets.GetActive")]
    private readonly Func<string?>? presetsGetActive;

    [EzIPC("Presets.SetActive")]
    private readonly Func<string, bool>? presetsSetActive;

    [EzIPC("Presets.ClearActive")]
    private readonly Func<bool>? presetsClearActive;
#pragma warning restore SA1310, CS0649

    private bool hasControl;
    private string? previousPreset;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BossModIpc" /> class.
    /// </summary>
    public BossModIpc()
    {
        EzIPC.Init(this, "BossMod");
    }

    /// <summary>
    ///     Gets a value indicating whether our preset is still the active one. The
    ///     check goes through IPC, so it is throttled; in between, the last known
    ///     answer is returned.
    /// </summary>
    public bool HasControl
    {
        get
        {
            if (!this.hasControl)
            {
                return false;
            }

            if (EzThrottler.Throttle("ZodiacBuddy.BossMod.ControlCheck", 2000))
            {
                try
                {
                    this.hasControl = this.presetsGetActive?.Invoke() == PresetName;
                }
                catch (Exception ex)
                {
                    Service.PluginLog.Debug(ex, "BossMod IPC call failed");
                    this.hasControl = false;
                }
            }

            return this.hasControl;
        }
    }

    /// <summary>
    ///     Check whether BossMod or BossMod Reborn is installed and loaded.
    /// </summary>
    /// <returns>Whether BossMod can be controlled.</returns>
    public static bool IsAvailable()
        => Service.Interface.InstalledPlugins.Any(
            p => p.InternalName is "BossMod" or "BossModReborn" && p.IsLoaded);

    /// <summary>
    ///     Take control of BossMod by activating the ZodiacBuddy preset, creating
    ///     or refreshing it first.
    /// </summary>
    /// <returns>Whether control was acquired.</returns>
    public bool BeginControl()
    {
        if (this.hasControl)
        {
            return true;
        }

        try
        {
            if (this.presetsCreate is null || this.presetsSetActive is null)
            {
                Service.PluginLog.Error("BossMod IPC is not available.");
                return false;
            }

            if (!this.presetsCreate(PresetJson, true))
            {
                Service.PluginLog.Error("Could not create the ZodiacBuddy preset in BossMod.");
                return false;
            }

            // Remember the user's own active preset so it can be restored.
            this.previousPreset = this.presetsGetActive?.Invoke();
            if (this.previousPreset == PresetName)
            {
                this.previousPreset = null;
            }

            if (!this.presetsSetActive(PresetName))
            {
                Service.PluginLog.Error("Could not activate the ZodiacBuddy preset in BossMod.");
                return false;
            }

            this.hasControl = true;
            return true;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Problem while setting up BossMod.");
            return false;
        }
    }

    /// <summary>
    ///     Release control of BossMod, restoring the preset the user had active.
    /// </summary>
    public void EndControl()
    {
        if (!this.hasControl)
        {
            return;
        }

        try
        {
            if (this.presetsGetActive?.Invoke() == PresetName)
            {
                if (this.previousPreset is not null)
                {
                    this.presetsSetActive?.Invoke(this.previousPreset);
                }
                else
                {
                    this.presetsClearActive?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "Problem while releasing BossMod control.");
        }
        finally
        {
            this.hasControl = false;
            this.previousPreset = null;
        }
    }
}
