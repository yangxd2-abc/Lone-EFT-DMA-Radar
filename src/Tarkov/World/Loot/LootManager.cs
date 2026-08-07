/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
using Collections.Pooled;
using LoneEftDmaRadar.Misc;
using LoneEftDmaRadar.Tarkov.Unity.Collections;
using LoneEftDmaRadar.Tarkov.Unity.Structures;
using LoneEftDmaRadar.UI.Loot;
using VmmSharpEx.Extensions;

namespace LoneEftDmaRadar.Tarkov.World.Loot
{
    public sealed class LootManager
    {
        #region Fields/Properties/Constructor

        private readonly ulong _gameWorld;
        private readonly Lock _filterSync = new();
        private readonly ConcurrentDictionary<ulong, LootItem> _loot = new();
        private readonly HashSet<string> _loggedQuestItems = new(StringComparer.OrdinalIgnoreCase);
        private RateLimiter _refreshErrorRateLimit = new(TimeSpan.FromSeconds(5));
        private RateLimiter _scatterFallbackRateLimit = new(TimeSpan.FromSeconds(10));
        private RateLimiter _sequentialErrorRateLimit = new(TimeSpan.FromSeconds(10));
        private RateLimiter _unrecognizedClassRateLimit = new(TimeSpan.FromSeconds(10));
        private RateLimiter _lootSummaryRateLimit = new(TimeSpan.FromSeconds(10));
        private static int _managedPositionFallbackLogged;
        // The current IL2CPP layout exposes stable managed position fields for loot,
        // while the legacy native Component/GameObject scatter chain is no longer valid.
        // Start on the verified managed path instead of failing one scatter batch per raid.
        private bool _useSequentialLootReads = true;
        private static readonly object _nativeLayoutSync = new();
        private static int _nativeLayoutScanStarted;
        private static NativeLootLayout _nativeLayout = new(
            UnityOffsets.Component_GameObjectOffset,
            UnityOffsets.GameObject_ComponentsOffset,
            0x8,
            UnityOffsets.GameObject_NameOffset);

        /// <summary>
        /// All loot (with filter applied).
        /// </summary>
        public IReadOnlyList<LootItem> FilteredLoot { get; private set; }
        /// <summary>
        /// All Static Containers on the map.
        /// </summary>
        public IEnumerable<StaticLootContainer> StaticContainers => _loot.Values.OfType<StaticLootContainer>();

        public LootManager(ulong gameWorld)
        {
            _gameWorld = gameWorld;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Force a filter refresh.
        /// Thread Safe.
        /// </summary>
        public void RefreshFilter()
        {
            if (_filterSync.TryEnter())
            {
                try
                {
                    var filter = LootFilter.Create();
                    FilteredLoot = _loot.Values?
                        .Where(x => filter(x))
                        .OrderBy(x => x.Important)
                        .ThenBy(x => x?.Price ?? 0)
                        .ToList();
                }
                catch { }
                finally
                {
                    _filterSync.Exit();
                }
            }
        }

        /// <summary>
        /// Refreshes loot, only call from a memory thread (Non-GUI).
        /// </summary>
        public void Refresh(CancellationToken ct)
        {
            try
            {
                GetLoot(ct);
                RefreshFilter();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_refreshErrorRateLimit.TryEnter())
                    Logging.WriteLine($"CRITICAL ERROR - Failed to refresh loot: {ex}");
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Updates referenced FilteredLoot List with fresh values.
        /// </summary>
        private void GetLoot(CancellationToken ct)
        {
            var lootListAddr = Memory.ReadPtr(_gameWorld + Offsets.GameWorld.LootList);
            using var lootList = UnityList<ulong>.Create(
                addr: lootListAddr,
                useCache: true);
            // Remove any loot no longer present
            using var lootListHs = lootList.ToPooledSet();
            foreach (var existing in _loot.Keys)
            {
                if (!lootListHs.Contains(existing))
                {
                    _ = _loot.TryRemove(existing, out _);
                }
            }
            if (_useSequentialLootReads)
            {
                RefreshLootSequential(lootListHs, ct);
                SyncCorpses();
                return;
            }
            // Proceed to get new loot
            using var map = Memory.CreateScatterMap();
            var round1 = map.AddRound();
            var round2 = map.AddRound();
            var round3 = map.AddRound();
            var round4 = map.AddRound();
            int scatterLootCount = 0;
            foreach (var lootBase in lootList)
            {
                ct.ThrowIfCancellationRequested();
                if (_loot.ContainsKey(lootBase))
                {
                    continue; // Already processed this loot item once before
                }
                if (!lootBase.IsValidUserVA())
                {
                    continue;
                }
                ++scatterLootCount;
                round1.PrepareReadPtr(lootBase + ObjectClass.MonoBehaviourOffset); // UnityComponent
                round1.PrepareReadPtr(lootBase + ObjectClass.To_NamePtr[0]); // C1
                round1.Completed += (sender, s1) =>
                {
                    if (s1.ReadPtr(lootBase + ObjectClass.MonoBehaviourOffset, out var monoBehaviour) &&
                        s1.ReadPtr(lootBase + ObjectClass.To_NamePtr[0], out var c1) &&
                        monoBehaviour.IsValidUserVA &&
                        c1.IsValidUserVA)
                    {
                        round2.PrepareReadPtr(monoBehaviour + UnityOffsets.Component_ObjectClassOffset); // InteractiveClass
                        round2.PrepareReadPtr(monoBehaviour + UnityOffsets.Component_GameObjectOffset); // GameObject
                        round2.PrepareReadPtr(c1 + ObjectClass.To_NamePtr[1]); // C2
                        round2.Completed += (sender, s2) =>
                        {
                            if (s2.ReadPtr(monoBehaviour + UnityOffsets.Component_ObjectClassOffset, out var interactiveClass) &&
                                s2.ReadPtr(monoBehaviour + UnityOffsets.Component_GameObjectOffset, out var gameObject) &&
                                s2.ReadPtr(c1 + ObjectClass.To_NamePtr[1], out var classNamePtr) &&
                                interactiveClass.IsValidUserVA &&
                                gameObject.IsValidUserVA &&
                                classNamePtr.IsValidUserVA)
                            {
                                round3.PrepareRead(classNamePtr, 64); // ClassName
                                round3.PrepareReadPtr(gameObject + UnityOffsets.GameObject_ComponentsOffset); // Components
                                round3.PrepareReadPtr(gameObject + UnityOffsets.GameObject_NameOffset); // PGameObjectName
                                round3.Completed += (sender, s3) =>
                                {
                                    if (s3.ReadString(classNamePtr, 64, Encoding.UTF8) is string className &&
                                        s3.ReadPtr(gameObject + UnityOffsets.GameObject_ComponentsOffset, out var components)
                                        && s3.ReadPtr(gameObject + UnityOffsets.GameObject_NameOffset, out var pGameObjectName) &&
                                        components.IsValidUserVA &&
                                        pGameObjectName.IsValidUserVA)
                                    {
                                        round4.PrepareRead(pGameObjectName, 64); // ObjectName
                                        round4.PrepareReadPtr(components + 0x8); // T1
                                        round4.Completed += (sender, s4) =>
                                        {
                                            if (
                                                s4.ReadString(pGameObjectName, 64, Encoding.UTF8) is string objectName &&
                                                s4.ReadPtr(components + 0x8, out var transformInternal) &&
                                                transformInternal.IsValidUserVA)
                                            {
                                                map.Completed += (sender, _) => // Store this as callback, let scatter reads all finish first (benchmarked faster)
                                                {
                                                    ct.ThrowIfCancellationRequested();
                                                    try
                                                    {
                                                        var @params = new LootIndexParams
                                                        {
                                                            ItemBase = lootBase,
                                                            InteractiveClass = interactiveClass,
                                                            ObjectName = objectName,
                                                            TransformInternal = transformInternal,
                                                            ClassName = className
                                                        };
                                                        ProcessLootIndex(ref @params);
                                                    }
                                                    catch
                                                    {
                                                    }
                                                };
                                            }
                                        };
                                    }
                                };
                            }
                        };
                    }
                };
            }
            if (scatterLootCount > 0)
            {
                try
                {
                    map.Execute(); // execute scatter read
                }
                catch (Exception ex)
                {
                    _useSequentialLootReads = true;
                    if (_scatterFallbackRateLimit.TryEnter())
                        Logging.WriteLine($"[LootManager] Scatter loot refresh failed for {scatterLootCount} new loot entries; falling back to sequential reads: {ex.Message}");
                    RefreshLootSequential(lootListHs, ct);
                }
            }

            // Post Scatter Read - Sync Corpses
            SyncCorpses();
        }

        private void SyncCorpses()
        {
            var deadPlayers = Memory.Players?
                .Where(x => x.Corpse is not null)?.ToList();
            foreach (var corpse in _loot.Values.OfType<LootCorpse>())
            {
                corpse.Sync(deadPlayers);
            }
        }

        /// <summary>
        /// Slower fallback path used when a scatter batch fails.
        /// </summary>
        private void RefreshLootSequential(IEnumerable<ulong> lootList, CancellationToken ct)
        {
            int candidates = 0;
            int recognized = 0;
            int validPositions = 0;
            int managedPositions = 0;
            int transformPositions = 0;
            int added = 0;
            int failures = 0;
            foreach (var lootBase in lootList)
            {
                ct.ThrowIfCancellationRequested();
                if (_loot.ContainsKey(lootBase) || !lootBase.IsValidUserVA())
                    continue;

                candidates++;

                try
                {
                    var monoBehaviour = Memory.ReadPtr(
                        lootBase + ObjectClass.MonoBehaviourOffset,
                        false);
                    var interactiveClass = Memory.ReadPtr(
                        monoBehaviour + UnityOffsets.Component_ObjectClassOffset,
                        false);
                    var className = ObjectClass.ReadName(lootBase, 64, false);
                    bool isRecognized =
                        className.Contains("Corpse", StringComparison.OrdinalIgnoreCase) ||
                        className.Equals("ObservedLootItem", StringComparison.OrdinalIgnoreCase) ||
                        className.Equals("LootItem", StringComparison.OrdinalIgnoreCase) ||
                        className.Equals("InteractiveLootItem", StringComparison.OrdinalIgnoreCase) ||
                        className.Equals("LootableContainer", StringComparison.OrdinalIgnoreCase);
                    if (isRecognized)
                        recognized++;

                    string objectName = string.Empty;
                    Vector3 position;
                    if (TryReadManagedLootPosition(interactiveClass, className, out position))
                    {
                        managedPositions++;
                    }
                    else
                    {
                        ResolveNativeLootObject(
                            lootBase,
                            out objectName,
                            out var transformInternal);
                        position = new UnityTransform(transformInternal, false).UpdatePosition();
                        if (!IsValidLootPosition(position))
                            continue;
                        transformPositions++;
                    }

                    validPositions++;

                    if (Interlocked.Exchange(ref _managedPositionFallbackLogged, 1) == 0)
                    {
                        Logging.WriteLine(
                            "[LootManager] Using managed loot position fields from the current IL2CPP layout.");
                    }

                    var @params = new LootIndexParams
                    {
                        ItemBase = lootBase,
                        InteractiveClass = interactiveClass,
                        ObjectName = objectName,
                        Position = position,
                        HasPosition = true,
                        ClassName = className
                    };
                    ProcessLootIndex(ref @params);
                    if (_loot.ContainsKey(lootBase))
                        added++;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (_sequentialErrorRateLimit.TryEnter())
                    {
                        Logging.WriteLine(
                            $"[LootManager] Sequential loot probe failed for 0x{lootBase:X}: " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (candidates > 0 && (_loot.IsEmpty || added > 0) && _lootSummaryRateLimit.TryEnter())
            {
                Logging.WriteLine(
                    $"[LootManager] Sequential scan: candidates={candidates}, " +
                    $"recognized={recognized}, validPositions={validPositions}, " +
                    $"managed={managedPositions}, transform={transformPositions}, " +
                    $"added={added}, total={_loot.Count}, failures={failures}.");
            }
        }

        private static bool TryReadManagedLootPosition(
            ulong lootBase,
            string className,
            out Vector3 position)
        {
            position = default;
            try
            {
                bool isContainer = className.Equals(
                    "LootableContainer",
                    StringComparison.OrdinalIgnoreCase);
                bool isLootItem =
                    className.Contains("Corpse", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("ObservedLootItem", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("LootItem", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("InteractiveLootItem", StringComparison.OrdinalIgnoreCase);

                if (isLootItem)
                {
                    position = Memory.ReadValue<Vector3>(
                        lootBase + Offsets.InteractiveLootItem._startPosition,
                        false);
                    return IsValidLootPosition(position);
                }

                if (isContainer)
                {
                    position = Memory.ReadValue<Vector3>(
                        lootBase + Offsets.WorldInteractiveObject.InteractPosition1,
                        false);
                    if (IsValidLootPosition(position))
                        return true;

                    position = Memory.ReadValue<Vector3>(
                        lootBase + Offsets.WorldInteractiveObject.InteractPosition2,
                        false);
                    return IsValidLootPosition(position);
                }
            }
            catch
            {
            }

            position = default;
            return false;
        }

        private static bool IsValidLootPosition(Vector3 position) =>
            float.IsFinite(position.X) &&
            float.IsFinite(position.Y) &&
            float.IsFinite(position.Z) &&
            MathF.Abs(position.X) < 10000f &&
            MathF.Abs(position.Y) < 10000f &&
            MathF.Abs(position.Z) < 10000f &&
            position.LengthSquared() > 0.01f;

        private static void ResolveNativeLootObject(
            ulong managedObject,
            out string objectName,
            out ulong transformInternal)
        {
            // Use the same complete managed object -> TransformInternal chain used by
            // grenades and other working Unity objects before attempting layout recovery.
            try
            {
                ulong gameObject = Memory.ReadPtrChain(managedObject, false, ObjectClass.To_GameObject);
                ulong transform = Memory.ReadPtrChain(managedObject, false, UnityOffsets.TransformChain);
                _ = new UnityTransform(transform, false);
                objectName = TryReadObjectName(gameObject, UnityOffsets.GameObject_NameOffset);
                transformInternal = transform;
                return;
            }
            catch
            {
                // Native Unity layouts can change independently; use the resolver below.
            }

            ulong component = Memory.ReadPtr(managedObject + ObjectClass.MonoBehaviourOffset, false);
            var layout = _nativeLayout;
            if (TryReadNativeLootObject(component, layout, out objectName, out transformInternal))
                return;

            lock (_nativeLayoutSync)
            {
                layout = _nativeLayout;
                if (TryReadNativeLootObject(component, layout, out objectName, out transformInternal))
                    return;

                if (!TryScanNativeLootLayout(component, out layout, out objectName, out transformInternal))
                {
                    throw new InvalidOperationException(
                        $"Unable to resolve Unity Component/GameObject layout for 0x{managedObject:X}.");
                }

                _nativeLayout = layout;
                Logging.WriteLine(
                    $"[LootManager] Native Unity object layout resolved: " +
                    $"gameObject=0x{layout.ComponentGameObjectOffset:X}, " +
                    $"components=0x{layout.GameObjectComponentsOffset:X}, " +
                    $"transform=0x{layout.ComponentsTransformOffset:X}, " +
                    $"name=0x{layout.GameObjectNameOffset:X}");
            }
        }

        private static bool TryReadNativeLootObject(
            ulong component,
            NativeLootLayout layout,
            out string objectName,
            out ulong transformInternal)
        {
            objectName = string.Empty;
            transformInternal = 0;
            try
            {
                ulong gameObject = Memory.ReadPtr(component + layout.ComponentGameObjectOffset, false);
                ulong components = Memory.ReadPtr(gameObject + layout.GameObjectComponentsOffset, false);
                ulong transformComponent = Memory.ReadPtr(components + layout.ComponentsTransformOffset, false);
                if (!TryResolveTransformInternal(transformComponent, out ulong transform))
                    return false;

                objectName = TryReadObjectName(gameObject, layout.GameObjectNameOffset) ?? string.Empty;
                transformInternal = transform;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryScanNativeLootLayout(
            ulong component,
            out NativeLootLayout layout,
            out string objectName,
            out ulong transformInternal)
        {
            layout = default;
            objectName = string.Empty;
            transformInternal = 0;

            const int scanBytes = 0x108;
            if (Interlocked.Exchange(ref _nativeLayoutScanStarted, 1) == 0)
            {
                Logging.WriteLine(
                    $"[LootManager] Resolving native Unity loot layout from component 0x{component:X}...");
            }
            Span<ulong> componentData = stackalloc ulong[scanBytes / sizeof(ulong)];
            var gameObjectDataBuffer = new ulong[scanBytes / sizeof(ulong)];
            var componentsDataBuffer = new ulong[0x48 / sizeof(ulong)];
            try
            {
                Memory.ReadSpan(component, componentData, false);
            }
            catch
            {
                return false;
            }

            for (int gameObjectSlot = 0; gameObjectSlot < componentData.Length; gameObjectSlot++)
            {
                ulong gameObject = componentData[gameObjectSlot];
                if (!gameObject.IsValidUserVA())
                    continue;

                Span<ulong> gameObjectData = gameObjectDataBuffer;
                try
                {
                    Memory.ReadSpan(gameObject, gameObjectData, false);
                }
                catch
                {
                    continue;
                }

                // A Component contains several native pointers. Verify that a candidate
                // actually resembles a GameObject before walking all of its component
                // list candidates; doing this after the nested scan can take minutes via DMA.
                uint nameOffset = FindGameObjectNameOffset(gameObject, gameObjectData, out var candidateName);
                if (candidateName.Length == 0)
                    continue;

                for (int componentsSlot = 0; componentsSlot < gameObjectData.Length; componentsSlot++)
                {
                    ulong components = gameObjectData[componentsSlot];
                    if (!components.IsValidUserVA())
                        continue;

                    Span<ulong> componentsData = componentsDataBuffer;
                    try
                    {
                        Memory.ReadSpan(components, componentsData, false);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int transformSlot = 0; transformSlot < componentsData.Length; transformSlot++)
                    {
                        ulong transformComponent = componentsData[transformSlot];
                        if (!transformComponent.IsValidUserVA())
                            continue;

                        if (!TryResolveTransformInternal(transformComponent, out ulong transform))
                            continue;

                        objectName = candidateName;
                        layout = new(
                            (uint)(gameObjectSlot * sizeof(ulong)),
                            (uint)(componentsSlot * sizeof(ulong)),
                            (uint)(transformSlot * sizeof(ulong)),
                            nameOffset);
                        transformInternal = transform;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveTransformInternal(
            ulong transformComponent,
            out ulong transformInternal)
        {
            transformInternal = 0;
            if (!transformComponent.IsValidUserVA())
                return false;

            // Some Unity layouts expose TransformInternal directly in the component
            // array, while others expose a native Component that links back through
            // its ObjectClass and managed MonoBehaviour object.
            try
            {
                _ = new UnityTransform(transformComponent, false);
                transformInternal = transformComponent;
                return true;
            }
            catch
            {
            }

            try
            {
                ulong objectClass = Memory.ReadPtr(
                    transformComponent + UnityOffsets.Component_ObjectClassOffset,
                    false);
                ulong candidate = Memory.ReadPtr(
                    objectClass + ObjectClass.MonoBehaviourOffset,
                    false);
                _ = new UnityTransform(candidate, false);
                transformInternal = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static uint FindGameObjectNameOffset(
            ulong gameObject,
            ReadOnlySpan<ulong> gameObjectData,
            out string objectName)
        {
            objectName = TryReadObjectName(gameObject, UnityOffsets.GameObject_NameOffset) ?? string.Empty;
            if (objectName.Length != 0)
                return UnityOffsets.GameObject_NameOffset;

            for (int slot = 0; slot < gameObjectData.Length; slot++)
            {
                string candidate = TryReadString(gameObjectData[slot]);
                if (candidate.Length == 0)
                    continue;

                objectName = candidate;
                return (uint)(slot * sizeof(ulong));
            }

            return UnityOffsets.GameObject_NameOffset;
        }

        private static string TryReadObjectName(ulong gameObject, uint offset)
        {
            try
            {
                return TryReadString(Memory.ReadPtr(gameObject + offset, false));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryReadString(ulong address)
        {
            if (!address.IsValidUserVA())
                return string.Empty;
            try
            {
                string value = Memory.ReadUtf8String(address, 64, false);
                if (string.IsNullOrWhiteSpace(value) || value.Length >= 64 ||
                    value.Any(c => char.IsControl(c) && c is not '\t'))
                {
                    return string.Empty;
                }
                return value;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Process a single loot index.
        /// </summary>
        private void ProcessLootIndex(ref LootIndexParams p)
        {
            var isCorpse = p.ClassName.Contains("Corpse", StringComparison.OrdinalIgnoreCase);
            var isLooseLoot =
                p.ClassName.Equals("ObservedLootItem", StringComparison.OrdinalIgnoreCase) ||
                p.ClassName.Equals("LootItem", StringComparison.OrdinalIgnoreCase) ||
                p.ClassName.Equals("InteractiveLootItem", StringComparison.OrdinalIgnoreCase);
            var isContainer = p.ClassName.Equals("LootableContainer", StringComparison.OrdinalIgnoreCase);
            var interactiveClass = p.InteractiveClass;

            if (p.ObjectName.Contains("script", StringComparison.OrdinalIgnoreCase))
            {
                // skip these
            }
            else
            {
                // Get Item Position
                var pos = p.HasPosition
                    ? p.Position
                    : new UnityTransform(p.TransformInternal, false).UpdatePosition();
                if (isCorpse)
                {
                    var corpse = new LootCorpse(interactiveClass, pos);
                    _ = _loot.TryAdd(p.ItemBase, corpse);
                }
                else if (isContainer)
                {
                    try
                    {
                        if (p.ObjectName.Equals("loot_collider", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = _loot.TryAdd(p.ItemBase, new LootAirdrop(pos));
                        }
                        else
                        {
                            var itemOwner = Memory.ReadPtr(interactiveClass + Offsets.LootableContainer.ItemOwner);
                            var ownerItemBase = Memory.ReadPtr(itemOwner + Offsets.ItemController.RootItem);
                            var ownerItemTemplate = Memory.ReadPtr(ownerItemBase + Offsets.LootItem.Template);
                            var ownerItemMongoId = Memory.ReadValue<MongoID>(ownerItemTemplate + Offsets.ItemTemplate._id);
                            var ownerItemId = ownerItemMongoId.ReadString();
                            _ = _loot.TryAdd(p.ItemBase, new StaticLootContainer(ownerItemId, pos));
                        }
                    }
                    catch
                    {
                    }
                }
                else if (isLooseLoot)
                {
                    var item = Memory.ReadPtr(interactiveClass + Offsets.InteractiveLootItem._item); //EFT.InventoryLogic.Item
                    var itemTemplate = Memory.ReadPtr(item + Offsets.LootItem.Template); //EFT.InventoryLogic.ItemTemplate
                    var isQuestItem = Memory.ReadValue<bool>(itemTemplate + Offsets.ItemTemplate.QuestItem);

                    var mongoId = Memory.ReadValue<MongoID>(itemTemplate + Offsets.ItemTemplate._id);
                    var id = mongoId.ReadString();
                    if (isQuestItem)
                    {
                        if (!_loggedQuestItems.Contains(id))
                        {
                            var shortNamePtr = Memory.ReadPtr(itemTemplate + Offsets.ItemTemplate.ShortName);
                            var shortName = Memory.ReadUnityString(shortNamePtr, 128);
                            if (shortName.Any(c => c > 127))
                            {
                                shortName = id.Length > 8 ? id[^8..] : id; // Edge case some shortnames are russki
                            }
                            _ = _loot.TryAdd(p.ItemBase, new LootItem(id, $"Q_{shortName}", pos) { IsQuestItem = true });
                            _loggedQuestItems.Add(id);
                        }
                    }
                    else
                    {
                        //If NOT a quest item. Quest items are like the quest related things you need to find like the pocket watch or Jaeger's Letter etc. We want to ignore these quest items.
                        if (TarkovDataManager.AllItems.TryGetValue(id, out var entry))
                        {
                            _ = _loot.TryAdd(p.ItemBase, new LootItem(entry, pos));
                        }
                    }
                }
                else if (_unrecognizedClassRateLimit.TryEnter())
                {
                    Logging.WriteLine(
                        $"[LootManager] Ignoring unrecognized loot class '{p.ClassName}' " +
                        $"(object '{p.ObjectName}', base=0x{p.ItemBase:X}).");
                }
            }
        }

        private readonly struct LootIndexParams
        {
            public ulong ItemBase { get; init; }
            public ulong InteractiveClass { get; init; }
            public string ObjectName { get; init; }
            public ulong TransformInternal { get; init; }
            public Vector3 Position { get; init; }
            public bool HasPosition { get; init; }
            public string ClassName { get; init; }
        }

        private readonly record struct NativeLootLayout(
            uint ComponentGameObjectOffset,
            uint GameObjectComponentsOffset,
            uint ComponentsTransformOffset,
            uint GameObjectNameOffset);

        #endregion

    }
}
