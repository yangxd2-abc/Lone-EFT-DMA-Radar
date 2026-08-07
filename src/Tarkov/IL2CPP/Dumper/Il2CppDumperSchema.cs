/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 *
 * Adapted from the eft-dma-radar-silk project (HuiTeab, PolyForm Noncommercial 1.0.0).
 */
using SDK;

namespace LoneEftDmaRadar.Tarkov.IL2CPP.Dumper
{
    public static partial class Il2CppDumper
    {
        private enum FieldKind { Normal, MethodRva }

        private readonly struct SchemaField(string il2cpp, string cs, FieldKind kind = FieldKind.Normal)
        {
            public readonly string Il2CppName = il2cpp;
            public readonly string CsName = cs;
            public readonly FieldKind Kind = kind;
        }

        private sealed class SchemaClass(string il2cpp, string cs, bool isStatic, SchemaField[] fields, uint? typeIndex, string? resolveViaChild = null)
        {
            public readonly string Il2CppName = il2cpp;
            public readonly string CsName = cs;
            public readonly bool IsStatic = isStatic;
            public readonly SchemaField[] Fields = fields;
            /// <summary>
            /// When set, resolves the class via TypeInfoTable[TypeIndex] instead of name lookup.
            /// Required for obfuscated EFT singletons (\uXXXX-named).
            /// </summary>
            public readonly uint? TypeIndex = typeIndex;
            /// <summary>
            /// When set, resolves the class by finding this concrete child in the type table
            /// and walking Il2CppClass::parent until a class with name == <see cref="Il2CppName"/>
            /// is found. Required for open generic definitions (e.g. <c>BaseHealthController`1</c>)
            /// whose entry has 0 offsets.
            /// </summary>
            public readonly string? ResolveViaChild = resolveViaChild;
        }

        private static SchemaField F(string il2cpp, string? cs = null)
            => new(il2cpp, cs ?? il2cpp, FieldKind.Normal);
        private static SchemaField M(string il2cpp, string? cs = null)
            => new(il2cpp, cs ?? (il2cpp + "_RVA"), FieldKind.MethodRva);

        private static SchemaClass C(string il2cpp, SchemaField[] f, string? cs = null, bool s = false, uint ti = 0, string? child = null)
            => new(il2cpp, cs ?? il2cpp, s, f, ti == 0 ? null : ti, child);

        /// <summary>
        /// Schema mapping IL2CPP classes/fields to Lone's <see cref="Offsets"/> nested structs.
        /// Each row's CsName must match a nested struct in <c>SDK.Offsets</c>. Fields are matched
        /// to <c>static uint</c>/<c>static ulong</c> members via reflection.
        /// </summary>
        private static SchemaClass[] BuildSchema() =>
        [
            // GameWorld (Lone has merged silk's GameWorld + ClientLocalGameWorld fields)
            C("GameWorld", [
                F("<BtrController>k__BackingField", "BtrController"),
                F("<LocationId>k__BackingField", "LocationId"),
                F("LootList"),
                F("RegisteredPlayers"),
                F("MainPlayer"),
                F("<SynchronizableObjectLogicProcessor>k__BackingField", "SynchronizableObjectLogicProcessor"),
                F("Grenades"),
            ]),

            C("SynchronizableObject", [F("Type")]),

            // Lone uses _staticSynchronizableObjects directly (silk renames it to _activeSynchronizableObjects)
            C("SynchronizableObjectLogicProcessor", [F("_staticSynchronizableObjects")]),

            C("TripwireSynchronizableObject", [
                F("_tripwireState"),
                F("<ToPosition>k__BackingField", "ToPosition"),
            ]),

            // BtrController — obfuscated singleton; resolved via TypeIndex
            C("BtrController", [
                F("<BtrView>k__BackingField", "BtrView"),
            ], s: true, ti: Offsets.Special.BtrController_TypeIndex),

            C("BTRView", [
                F("turret"),
                F("_previousPosition"),
            ]),

            C("BTRTurretView", [F("_bot")]),

            C("Throwable", [F("_isDestroyed")]),

            C("Player", [
                F("<MovementContext>k__BackingField", "MovementContext"),
                F("_playerBody"),
                F("<GameWorld>k__BackingField", "GameWorld"),
                F("Corpse"),
                F("<Location>k__BackingField", "Location"),
                F("<Profile>k__BackingField", "Profile"),
                F("_handsController"),
                F("_playerLookRaycastTransform"),
            ]),

            C("ObservedPlayerView", [
                F("<ObservedPlayerController>k__BackingField", "ObservedPlayerController"),
                F("<Voice>k__BackingField", "Voice"),
                F("<Id>k__BackingField", "Id"),
                F("<Side>k__BackingField", "Side"),
                F("<IsAI>k__BackingField", "IsAI"),
                F("<PlayerBody>k__BackingField", "PlayerBody"),
            ]),

            C("ObservedPlayerController", [
                F("<InventoryController>k__BackingField", "InventoryController"),
                F("<PlayerView>k__BackingField", "PlayerView"),
                F("<MovementController>k__BackingField", "MovementController"),
                F("<HealthController>k__BackingField", "HealthController"),
                F("<HandsController>k__BackingField", "HandsController"),
            ]),

            C("ObservedPlayerHandsController", [F("_item")]),

            C("InventoryController", [F("<Inventory>k__BackingField", "Inventory")]),

            C("Inventory", [F("Equipment")]),

            C("Slot", [
                F("<ContainedItem>k__BackingField", "ContainedItem"),
                F("<ID>k__BackingField", "ID"),
            ]),

            C("ObservedPlayerStateContext", [
                F("<Rotation>k__BackingField", "Rotation"),
            ]),

            // Lone's ObservedHealthController = silk's ObservedPlayerHealthController
            C("ObservedPlayerHealthController", [
                F("_player"),
                F("_playerCorpse"),
                F("HealthStatus"),
            ], cs: "ObservedHealthController"),

            C("Profile", [
                F("Id"),
                F("AccountId"),
                F("Info"),
                F("QuestsData"),
                F("WishlistManager"),
            ]),

            // Lone's _wishlistItems = silk's _userItems
            C("WishlistManager", [F("_userItems", "_wishlistItems")]),

            // Lone's PlayerInfo = silk's ProfileInfo
            C("ProfileInfo", [
                F("<Side>k__BackingField", "Side"),
                F("RegistrationDate"),
                F("GroupId"),
            ], cs: "PlayerInfo"),

            // Lone's QuestsData = silk's QuestStatusData
            C("QuestStatusData", [
                F("Id"),
                F("Status"),
                F("CompletedConditions"),
            ], cs: "QuestsData"),

            C("MovementContext", [
                F("_player"),
                F("_rotation"),
            ]),

            // Lone's InteractiveLootItem = silk's LootItem→InteractiveLootItem
            C("LootItem", [
                F("_item"),
                F("_startPosition"),
            ], cs: "InteractiveLootItem"),

            // Lone's DizSkinningSkeleton = silk's Skeleton→DizSkinningSkeleton
            C("Skeleton", [F("_values")], cs: "DizSkinningSkeleton"),

            C("LootableContainer", [F("ItemOwner")]),

            C("WorldInteractiveObject", [
                F("interactPosition1", "InteractPosition1"),
                F("interactPosition2", "InteractPosition2"),
            ]),

            C("ItemController", [F("<RootItem>k__BackingField", "RootItem")]),

            // Lone's LootItem = silk's Item→LootItem
            C("Item", [F("<Template>k__BackingField", "Template")], cs: "LootItem"),

            C("ItemTemplate", [
                F("ShortName"),
                F("QuestItem"),
                F("<_id>k__BackingField", "_id"),
            ]),

            C("PlayerBody", [F("SkeletonRootJoint")]),

            C("OpticCameraManager", [
                F("<Camera>k__BackingField", "Camera"),
                F("<CurrentOpticSight>k__BackingField", "CurrentOpticSight"),
            ]),

            C("CameraManager", [
                F("<OpticCameraManager>k__BackingField", "OpticCameraManager"),
                F("<Camera>k__BackingField", "Camera"),
                M("get_Instance", "GetInstance_RVA"),
            ], cs: "EFTCameraManager"),

            // GamePlayerOwner — obfuscated singleton; resolved via TypeIndex
            C("GamePlayerOwner", [F("_myPlayer")], s: true, ti: Offsets.Special.GamePlayerOwner_TypeIndex),
        ];
    }
}
