/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
using LoneEftDmaRadar.Tarkov.Unity.Collections;
using LoneEftDmaRadar.Tarkov.Unity.Structures;
using VmmSharpEx.Extensions;

namespace LoneEftDmaRadar.Tarkov.World.Player
{
    public class ClientPlayer : AbstractPlayer
    {
        /// <summary>
        /// EFT.Profile Address
        /// </summary>
        public ulong Profile { get; }
        /// <summary>
        /// PlayerInfo Address (GClass1044)
        /// </summary>
        public ulong Info { get; }
        /// <summary>
        /// Player name.
        /// </summary>
        public override string Name
        {
            get => "AI";
        }
        /// <summary>
        /// Player's Faction.
        /// </summary>
        public override Enums.EPlayerSide PlayerSide { get; protected set; }
        /// <summary>
        /// Player is Human-Controlled.
        /// </summary>
        public override bool IsHuman { get; }
        /// <summary>
        /// MovementContext / StateContext
        /// </summary>
        public override ulong MovementContext { get; }
        /// <summary>
        /// Corpse field address..
        /// </summary>
        public override ulong CorpseAddr { get; }
        /// <summary>
        /// Player Rotation Field Address (view angles).
        /// </summary>
        public override ulong RotationAddress { get; }

        internal ClientPlayer(ulong playerBase) : base(playerBase)
        {
            try
            {
                Profile = Memory.ReadPtr(this + Offsets.Player.Profile, false);
                Info = Memory.ReadPtr(Profile + Offsets.Profile.Info, false);
                CorpseAddr = this + Offsets.Player.Corpse;
                PlayerSide = (Enums.EPlayerSide)Memory.ReadValue<int>(Info + Offsets.PlayerInfo.Side, false);
                if (!Enum.IsDefined<Enums.EPlayerSide>(PlayerSide))
                    throw new ArgumentOutOfRangeException(nameof(PlayerSide));

                MovementContext = GetMovementContext();
                RotationAddress = ValidateRotationAddr(MovementContext + Offsets.MovementContext._rotation);
                /// Setup Transform
                InitializeSkeleton(Offsets.Player._playerBody);
            }
            catch (Exception ex)
            {
                LogClientPlayerInitFailure(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Get Movement Context Instance.
        /// </summary>
        private ulong GetMovementContext()
        {
            var movementContext = Memory.ReadPtr(this + Offsets.Player.MovementContext, false);
            var player = Memory.ReadPtr(movementContext + Offsets.MovementContext._player, false);
            if (player != this)
                throw new ArgumentOutOfRangeException(nameof(movementContext));
            return movementContext;
        }

        private static void LogClientPlayerInitFailure(ulong playerBase, Exception ex)
        {
            Logging.WriteLine(
                $"[ClientPlayer] Init failed for Player=0x{playerBase:X}; " +
                $"Offsets: Profile=0x{Offsets.Player.Profile:X}, Profile.Info=0x{Offsets.Profile.Info:X}, " +
                $"PlayerInfo.Side=0x{Offsets.PlayerInfo.Side:X}, MovementContext=0x{Offsets.Player.MovementContext:X}; " +
                $"{ex.GetType().Name}: {ex.Message}");

            LogPtrProbe("Profile", playerBase + Offsets.Player.Profile, out var profile);
            if (profile != 0)
                LogPtrProbe("Profile.Info", profile + Offsets.Profile.Info, out _);
            LogPtrProbe("MovementContext", playerBase + Offsets.Player.MovementContext, out var movementContext);
            if (movementContext != 0)
                LogPtrProbe("MovementContext._player", movementContext + Offsets.MovementContext._player, out _);
        }

        private static void LogPtrProbe(string name, ulong address, out ulong value)
        {
            value = 0;
            try
            {
                var raw = Memory.ReadValue<ulong>(address, false);
                Logging.WriteLine($"[ClientPlayer] Probe {name}: addr=0x{address:X}, raw=0x{raw:X}");
                if (raw.IsValidUserVA())
                    value = raw;
            }
            catch (Exception probeEx)
            {
                Logging.WriteLine($"[ClientPlayer] Probe {name}: addr=0x{address:X}, failed: {probeEx.Message}");
            }
        }

    }
}

