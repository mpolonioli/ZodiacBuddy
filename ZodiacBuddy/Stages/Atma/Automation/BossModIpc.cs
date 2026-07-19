using ECommons.EzIpcManager;
using ECommons.Throttlers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     IPC wrapper for the BossMod (vbm) / BossMod Reborn plugin. Control works by
///     creating a ZodiacBuddy autorotation preset that combines the standard
///     per-job rotation modules with BossMod's AI movement module, so the
///     character runs its rotation on our hard target and moves out of AoEs. The
///     preset is only activated while actually fighting - outside of fights
///     BossMod's movement would wrestle with vnavmesh travel and cancel teleport
///     casts - and the preset the user had active before is restored afterwards.
///     Modeled on the bundleoftweaks FATE grinder, a proven BossMod consumer.
/// </summary>
internal sealed class BossModIpc
{
    private const string PresetName = "ZodiacBuddy";

    // Same shape and option names as the preset of the bundleoftweaks FATE
    // grinder, with two deliberate differences: Targeting is Manual instead of
    // Auto and its AutoTarget module is left out, because the automation
    // hard-targets the mob it wants killed and auto-targeting would pull
    // unrelated mobs. FateUtils applies FATE level sync as a safety net for our
    // own sync step, and NormalMovement is the module that dodges AoEs.
    private const string PresetJson = """
        {
          "Name": "ZodiacBuddy",
          "Modules": {
            "BossMod.Autorotation.xan.AST": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.BLM": [{ "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.BRD": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.DNC": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.DRG": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.DRK": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.GNB": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.MCH": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.MNK": [{ "Track": "RoF", "Option": "Automatic" }, { "Track": "BH", "Option": "Automatic" }, { "Track": "RoW", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.NIN": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.PCT": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }, { "Track": "Motifs", "Option": "Downtime" }],
            "BossMod.Autorotation.xan.PLD": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.RDM": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.RPR": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.SAM": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.SCH": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.SGE": [{ "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.SMN": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.xan.VPR": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }, { "Track": "WrithingSnap", "Option": "Ranged" }],
            "BossMod.Autorotation.xan.WHM": [{ "Track": "Buffs", "Option": "Automatic" }, { "Track": "AOE", "Option": "AOE" }, { "Track": "Targeting", "Option": "Manual" }],
            "BossMod.Autorotation.VeynWAR": [{ "Track": "AOE", "Option": "AutoFinishCombo" }],
            "BossMod.Autorotation.MiscAI.FateUtils": [{ "Track": "Sync", "Option": "Enable" }],
            "BossMod.Autorotation.xan.Caster": [],
            "BossMod.Autorotation.xan.HealerAI": [],
            "BossMod.Autorotation.xan.MeleeAI": [],
            "BossMod.Autorotation.xan.RangedAI": [],
            "BossMod.Autorotation.xan.TankAI": [],
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

    [EzIPC("ObstacleMap.Generate")]
    private readonly Func<Vector3, float, bool, bool>? obstacleMapGenerate;

    [EzIPC("ObstacleMap.GetGenerationStatus")]
    private readonly Func<TaskStatus>? obstacleMapStatus;

    [EzIPC("ObstacleMap.ClearTempMap")]
    private readonly Func<bool>? obstacleMapClear;

    [EzIPC("ObstacleMap.EvaluateTempMapQuality")]
    private readonly Func<BitmapQuality?>? obstacleMapQuality;
#pragma warning restore SA1310, CS0649

    private readonly HashSet<uint> badMapFates = [];

    private bool hasControl;
    private bool combatActive;
    private string? previousPreset;
    private uint pendingMapFateId;
    private uint generatedMapFateId;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BossModIpc" /> class.
    /// </summary>
    public BossModIpc()
    {
        EzIPC.Init(this, "BossMod");
    }

    /// <summary>
    ///     Gets a value indicating whether control is still healthy: while fighting
    ///     our preset must be the active one, otherwise the plugin merely has to be
    ///     loaded. The check goes through IPC, so it is throttled; in between, the
    ///     last known answer is returned.
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
                    this.hasControl = this.combatActive
                        ? this.presetsGetActive?.Invoke() == PresetName
                        : IsAvailable();
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
    ///     Take control of BossMod: create or refresh the ZodiacBuddy preset and
    ///     remember the user's active preset. The preset itself is only activated
    ///     while fighting, through <see cref="SetCombatActive" />.
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

            // Remember the user's own active preset so it can be restored when
            // the automation ends; in between, fights use ours and travel none.
            this.previousPreset = this.presetsGetActive?.Invoke();
            if (this.previousPreset == PresetName)
            {
                this.previousPreset = null;
            }

            this.hasControl = true;
            this.combatActive = false;
            return true;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Error(ex, "Problem while setting up BossMod.");
            return false;
        }
    }

    /// <summary>
    ///     Activate our preset while fighting and deactivate it otherwise, so
    ///     BossMod's movement can never interfere with travel or teleport casts.
    /// </summary>
    /// <param name="active">Whether a fight is in progress.</param>
    public void SetCombatActive(bool active)
    {
        if (!this.hasControl || this.combatActive == active)
        {
            return;
        }

        try
        {
            if (active)
            {
                if (this.presetsSetActive?.Invoke(PresetName) != true)
                {
                    Service.PluginLog.Warning("Could not activate the ZodiacBuddy preset in BossMod.");
                    return;
                }
            }
            else if (this.presetsGetActive?.Invoke() == PresetName)
            {
                this.presetsClearActive?.Invoke();
            }

            this.combatActive = active;
        }
        catch (Exception ex)
        {
            Service.PluginLog.Warning(ex, "Problem while toggling the BossMod preset.");
        }
    }

    /// <summary>
    ///     Start generating a temporary obstacle map for a FATE area. Without one,
    ///     BossMod dodges but does not path around terrain in the open world. The
    ///     generation is asynchronous; <see cref="TickObstacleMap" /> completes it.
    /// </summary>
    /// <param name="fateId">Fate row ID, used to avoid regenerating.</param>
    /// <param name="center">A reachable point near the FATE's center.</param>
    /// <param name="radius">Radius to cover.</param>
    public void PrepareFateObstacleMap(uint fateId, Vector3 center, float radius)
    {
        if (!this.hasControl
            || this.pendingMapFateId == fateId
            || this.generatedMapFateId == fateId
            || this.badMapFates.Contains(fateId))
        {
            return;
        }

        try
        {
            if (this.obstacleMapGenerate?.Invoke(center, radius, false) == true)
            {
                Service.PluginLog.Debug($"[FateAutomation] Generating a BossMod obstacle map for FATE {fateId}.");
                this.pendingMapFateId = fateId;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "BossMod obstacle map generation failed to start");
        }
    }

    /// <summary>
    ///     Poll a pending obstacle map generation; once done, discard maps whose
    ///     quality is too poor for BossMod to navigate by (following the quality
    ///     gate of the bundleoftweaks grinder).
    /// </summary>
    public void TickObstacleMap()
    {
        if (!this.hasControl || this.pendingMapFateId == 0
            || !EzThrottler.Throttle("ZodiacBuddy.BossMod.MapTick", 1000))
        {
            return;
        }

        try
        {
            var status = this.obstacleMapStatus?.Invoke();
            if (status is TaskStatus.RanToCompletion)
            {
                if (this.obstacleMapQuality?.Invoke() is { IsBad: true } quality)
                {
                    Service.PluginLog.Debug(
                        $"[FateAutomation] Obstacle map for FATE {this.pendingMapFateId} too poor ({quality}); discarding it.");
                    this.badMapFates.Add(this.pendingMapFateId);
                    this.obstacleMapClear?.Invoke();
                }
                else
                {
                    Service.PluginLog.Debug($"[FateAutomation] Obstacle map for FATE {this.pendingMapFateId} ready.");
                    this.generatedMapFateId = this.pendingMapFateId;
                }

                this.pendingMapFateId = 0;
            }
            else if (status is TaskStatus.Faulted or TaskStatus.Canceled or null)
            {
                Service.PluginLog.Debug($"[FateAutomation] Obstacle map generation for FATE {this.pendingMapFateId} failed.");
                this.pendingMapFateId = 0;
            }
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "BossMod obstacle map polling failed");
            this.pendingMapFateId = 0;
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
                this.presetsClearActive?.Invoke();
            }

            if (this.previousPreset is not null)
            {
                this.presetsSetActive?.Invoke(this.previousPreset);
            }

            this.obstacleMapClear?.Invoke();
        }
        catch (Exception ex)
        {
            Service.PluginLog.Debug(ex, "Problem while releasing BossMod control.");
        }
        finally
        {
            this.hasControl = false;
            this.combatActive = false;
            this.previousPreset = null;
            this.pendingMapFateId = 0;
            this.generatedMapFateId = 0;
        }
    }

    /// <summary>
    ///     Quality metrics of a generated obstacle map, mirroring BossMod's
    ///     evaluation result (field shapes and thresholds from bundleoftweaks).
    /// </summary>
    public readonly record struct BitmapQuality(
        float BlockedFraction,
        float LargestPassableComponentFraction,
        float TinyPassableComponentFraction,
        float SpeckleFraction,
        int PassableComponents)
    {
        /// <summary>
        ///     Gets a value indicating whether the map is too poor to navigate by.
        /// </summary>
        public bool IsBad => this.BlockedFraction >= 0.85f
                             || this.LargestPassableComponentFraction >= 0.5f
                             || this.TinyPassableComponentFraction >= 0.03f
                             || this.SpeckleFraction >= 0.003f;

        /// <inheritdoc />
        public override string ToString()
            => $"Blocked: {this.BlockedFraction:P1}, LargestComp: {this.LargestPassableComponentFraction:P1}, " +
               $"TinyComp: {this.TinyPassableComponentFraction:P1}, Speckle: {this.SpeckleFraction:P1}, " +
               $"PassableComps: {this.PassableComponents}";
    }
}
