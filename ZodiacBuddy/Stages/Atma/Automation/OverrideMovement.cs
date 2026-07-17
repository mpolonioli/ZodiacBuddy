using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ZodiacBuddy.Stages.Atma.Automation;

/// <summary>
///     Hooks the movement input functions to steer the player toward
///     <see cref="DesiredPosition" /> regardless of user input. Used to physically
///     dislodge the character when navmesh path following gets stuck.
///     Ported from ZodiacBuddyReborn.
/// </summary>
internal sealed unsafe class OverrideMovement : IDisposable
{
    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);

    private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);

#pragma warning disable SA1310
    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", DetourName = nameof(RMIWalkDetour))]
    private readonly Hook<RMIWalkDelegate> rmiWalkHook = null!;

    [Signature("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8", DetourName = nameof(RMIFlyDetour))]
    private readonly Hook<RMIFlyDelegate> rmiFlyHook = null!;
#pragma warning restore SA1310

    private bool legacyMode;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OverrideMovement" /> class.
    /// </summary>
    public OverrideMovement()
    {
        Service.GameInterop.InitializeFromAttributes(this);
        Service.GameConfig.UiControlChanged += this.OnConfigChanged;
        this.UpdateLegacyMode();
    }

    /// <summary>
    ///     Gets or sets a value indicating whether the movement override is active.
    /// </summary>
    public bool Enabled
    {
        get => this.rmiWalkHook.IsEnabled;
        set
        {
            if (value)
            {
                this.rmiWalkHook.Enable();
                this.rmiFlyHook.Enable();
            }
            else
            {
                this.rmiWalkHook.Disable();
                this.rmiFlyHook.Disable();
            }
        }
    }

    /// <summary>
    ///     Gets or sets the position to steer toward while enabled.
    /// </summary>
    public Vector3 DesiredPosition { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Service.GameConfig.UiControlChanged -= this.OnConfigChanged;
        this.rmiWalkHook.Dispose();
        this.rmiFlyHook.Dispose();
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        this.rmiWalkHook.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);

        // Don't fight actual user input or forced movement.
        var movementAllowed = bAdditiveUnk == 0 && !Service.Condition[ConditionFlag.BeingMoved];
        if (movementAllowed && *sumLeft == 0 && *sumForward == 0 && this.DirectionToDestination() is { } relDir)
        {
            var dir = relDir.ToDirection();
            *sumLeft = dir.X;
            *sumForward = dir.Y;
        }
    }

    private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result)
    {
        this.rmiFlyHook.Original(self, result);

        // While flying, a simple forward-and-down push is enough to break free
        // from the geometry the path follower is caught on.
        if (Service.ObjectTable.LocalPlayer is not null)
        {
            result->Forward = 2f;
            result->Left = 0f;
            result->Up = -1f;
        }
    }

    private Angle? DirectionToDestination()
    {
        var player = Service.ObjectTable.LocalPlayer;
        if (player is null)
        {
            return null;
        }

        var dist = this.DesiredPosition - player.Position;
        if (dist.LengthSquared() <= 0.01f * 0.01f)
        {
            return null;
        }

        var dirH = Angle.FromDirectionXZ(dist);
        var refDir = this.legacyMode
            ? ((CameraEx*)CameraManager.Instance()->GetActiveCamera())->DirH.Radians() + 180.Degrees()
            : player.Rotation.Radians();
        return dirH - refDir;
    }

    private void OnConfigChanged(object? sender, ConfigChangeEvent evt)
    {
        this.UpdateLegacyMode();
    }

    private void UpdateLegacyMode()
    {
        this.legacyMode = Service.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;
    }

    /// <summary>
    ///     Movement input of the flying move controller.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    private struct PlayerMoveControllerFlyInput
    {
        [FieldOffset(0x0)]
        public float Forward;

        [FieldOffset(0x4)]
        public float Left;

        [FieldOffset(0x8)]
        public float Up;

        [FieldOffset(0xC)]
        public float Turn;

        [FieldOffset(0x10)]
        public float U10;

        [FieldOffset(0x14)]
        public byte DirMode;

        [FieldOffset(0x15)]
        public byte HaveBackwardOrStrafe;
    }

    /// <summary>
    ///     Camera fields not exposed by ClientStructs.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
    private struct CameraEx
    {
        // 0 is north, increases clockwise.
        [FieldOffset(0x130)]
        public float DirH;

        // 0 is horizontal, positive is looking up.
        [FieldOffset(0x134)]
        public float DirV;
    }
}
