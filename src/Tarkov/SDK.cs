namespace SDK
{
    public readonly partial struct Offsets
    {
        public readonly partial struct GameWorld
        {
            public static uint BtrController = 0x28; // object
            public static uint LocationId = 0xE8; // string
            public static uint LootList = 0x1B0; // object
            public static uint RegisteredPlayers = 0x1D0; // object
            public static uint MainPlayer = 0x230; // object
            public static uint SynchronizableObjectLogicProcessor = 0x270; // object
            public static uint Grenades = 0x2B0; // object
        }

        public readonly partial struct SynchronizableObject
        {
            public static uint Type = 0x68; // object
        }

        public readonly partial struct SynchronizableObjectLogicProcessor
        {
            public static uint _staticSynchronizableObjects = 0x18; // object
        }

        public readonly partial struct TripwireSynchronizableObject
        {
            public static uint _tripwireState = 0xE4; // object
            public static uint ToPosition = 0x158; // object
        }

        public readonly partial struct BtrController
        {
            public static uint BtrView = 0x50; // object
        }

        public readonly partial struct BTRView
        {
            public static uint turret = 0x60; // object
            public static uint _previousPosition = 0xB4; // object
        }

        public readonly partial struct BTRTurretView
        {
            public static uint _bot = 0x60; // object
        }

        public readonly partial struct Throwable
        {
            public static uint _isDestroyed = 0x4D; // bool
        }

        public readonly partial struct Player
        {
            public static uint MovementContext = 0x60; // object
            public static uint _playerBody = 0x198; // object
            public static uint GameWorld = 0x688; // object
            public static uint Corpse = 0x720; // object
            public static uint Location = 0x920; // string
            public static uint Profile = 0x9B8; // object
            public static uint _handsController = 0xA38; // object
            public static uint _playerLookRaycastTransform = 0xAC8; // object
        }

        public readonly partial struct ObservedPlayerView
        {
            public static uint ObservedPlayerController = 0x28; // object
            public static uint Voice = 0x40; // string
            public static uint Id = 0x7C; // int32_t
            public static uint Side = 0x94; // object
            public static uint IsAI = 0xA0; // bool
            public static uint PlayerBody = 0xD8; // object
        }

        public readonly partial struct ObservedPlayerController
        {
            public static uint InventoryController = 0x10; // object
            public static uint PlayerView = 0x18; // object
            public static uint MovementController = 0xD8; // object
            public static uint HealthController = 0xE8; // object
            public static uint HandsController = 0x120; // object
        }

        public readonly partial struct ObservedPlayerHandsController
        {
            public static uint _item = 0x58; // object
        }

        public readonly partial struct InventoryController
        {
            public static uint Inventory = 0x100; // object
        }

        public readonly partial struct Inventory
        {
            public static uint Equipment = 0x18; // object
        }

        public readonly partial struct InventoryEquipment
        {
            public static uint _cachedSlots = 0x90; // object
        }

        public readonly partial struct Slot
        {
            public static uint ContainedItem = 0x48; // object
            public static uint ID = 0x58; // string
        }

        public readonly partial struct ObservedPlayerMovementController
        {
            public static uint ObservedPlayerStateContext = 0x98; // object
        }

        public readonly partial struct ObservedPlayerStateContext
        {
            public static uint Rotation = 0x28; // object
        }

        public readonly partial struct ObservedHealthController
        {
            public static uint HealthStatus = 0x10; // object
            public static uint _player = 0x18; // object
            public static uint _playerCorpse = 0x20; // object
        }

        public readonly partial struct Profile
        {
            public static uint Id = 0x10; // string
            public static uint AccountId = 0x18; // string
            public static uint Info = 0x48; // object
            public static uint QuestsData = 0x98; // object
            public static uint WishlistManager = 0x108; // object
        }

        public readonly partial struct WishlistManager
        {
            public static uint _wishlistItems = 0x28; // object
        }

        public readonly partial struct PlayerInfo
        {
            public static uint Side = 0x48; // object
            public static uint RegistrationDate = 0x4C; // int32_t
            public static uint GroupId = 0x50; // string
        }

        public readonly partial struct QuestsData
        {
            public static uint Id = 0x10; // string
            public static uint Status = 0x1C; // object
            public static uint CompletedConditions = 0x28; // object
        }

        public readonly partial struct MovementContext
        {
            public static uint _player = 0x40; // object
            public static uint _rotation = 0xC0; // object
        }

        public readonly partial struct InteractiveLootItem
        {
            public static uint _item = 0xF0; // object
        }

        public readonly partial struct DizSkinningSkeleton
        {
            public static uint _values = 0x30; // object
        }

        public readonly partial struct LootableContainer
        {
            public static uint ItemOwner = 0x178; // object
        }

        public readonly partial struct ItemController
        {
            public static uint RootItem = 0xD0; // object
        }

        public readonly partial struct LootItem
        {
            public static uint Template = 0x60; // object
        }

        public readonly partial struct ItemTemplate
        {
            public static uint ShortName = 0x18; // string
            public static uint QuestItem = 0x34; // bool
            public static uint _id = 0xE0; // object
        }

        public readonly partial struct PlayerBody
        {
            public static uint SkeletonRootJoint = 0x30; // object
        }

        public readonly partial struct GamePlayerOwner
        {
            public static uint _myPlayer = 0x8; // object
        }

        /// <summary>
        /// Resolver-managed values: TypeInfoTable RVA in GameAssembly.dll and
        /// TypeIndex slots for obfuscated singletons that can't be name-resolved.
        /// All fields are writable so <see cref="LoneEftDmaRadar.Tarkov.IL2CPP.Dumper.Il2CppDumper"/>
        /// and <see cref="LoneEftDmaRadar.Tarkov.IL2CPP.Dumper.TypeInfoTableResolver"/> can
        /// overwrite them at runtime. Values shown are the last known-good fallbacks.
        /// </summary>
        public readonly partial struct Special
        {
            public static ulong TypeInfoTableRva = 0x598BAD8;
            public static uint GamePlayerOwner_TypeIndex = 0;
            public static uint EFTHardSettings_TypeIndex = 0;
            public static uint WeatherController_TypeIndex = 0;
            public static uint BtrController_TypeIndex = 0;
            public static uint MatchingProgress_TypeIndex = 0;
            public static uint TarkovApplication_TypeIndex = 0;
        }
    }

    public readonly partial struct Enums
    {
        public enum EPlayerSide
        {
            Usec = 1,
            Bear = 2,
            Savage = 4,
        }

        [Flags]
        public enum ETagStatus
        {
            Unaware = 1,
            Aware = 2,
            Combat = 4,
            Solo = 8,
            Coop = 16,
            Bear = 32,
            Usec = 64,
            Scav = 128,
            TargetSolo = 256,
            TargetMultiple = 512,
            Healthy = 1024,
            Injured = 2048,
            BadlyInjured = 4096,
            Dying = 8192,
            Birdeye = 16384,
            Knight = 32768,
            BigPipe = 65536,
            BlackDivision = 131072,
            VSRF = 262144,
        }

        [Flags]
        public enum EMemberCategory
        {
            Default = 0,
            Developer = 1,
            UniqueId = 2,
            Trader = 4,
            Group = 8,
            System = 16,
            ChatModerator = 32,
            ChatModeratorWithPermanentBan = 64,
            UnitTest = 128,
            Sherpa = 256,
            Emissary = 512,
            Unheard = 1024,
        }

        public enum SynchronizableObjectType
        {
            AirDrop = 0,
            AirPlane = 1,
            Tripwire = 2,
        }

        public enum ETripwireState
        {
            None = 0,
            Wait = 1,
            Active = 2,
            Exploding = 3,
            Exploded = 4,
            Inert = 5,
        }
    }
}
