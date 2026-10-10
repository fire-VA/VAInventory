// BackpackStorageSync — handles the equip/unequip storage round-trip.
//
// On equip:
//   1. The Slots system has appended a fresh backpack-storage block to
//      the equipment panel (sized rows × cols from the bridge).
//   2. We read the base64 ZPackage off the backpack item's customData
//      and place each item at its saved grid position inside the block.
//
// On unequip:
//   1. We collect every item currently sitting in the backpack-storage
//      block grid positions.
//   2. Pack into a ZPackage, base64-encode, and write back to the
//      backpack item via BackpackBridge.WriteStorage.
//   3. Remove those items from the player's Inventory so the next
//      Slots.InitializeSlots (which removes the block) doesn't leave
//      orphan items at out-of-bounds grid positions.
//   4. If the new backpack (when hot-swapping) is smaller, we leave
//      overflow items in the player's main inventory or drop on ground.
//
// See Refs/BACKPACK_AND_COIN_SLOT_PLAN.md §5.

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Managers
{
    public static class BackpackStorageSync
    {
        /// <summary>
        /// Rebuild the slot layout (with or without the storage block) and
        /// the equipment-panel UI, then either deserialize-into or
        /// serialize-out-of the storage block.
        /// </summary>
        public static void Initialize()
        {
            // Subscribe to the bridge events. Order:
            //   - Bridge.Initialize() must have run first (Ascend.cs).
            //   - On equip: rebuild slots so the storage block exists,
            //               THEN populate it from the backpack's ZPackage.
            //   - On unequip: serialize the storage block into the
            //                 ZPackage, THEN rebuild slots so the block
            //                 is gone.
            BackpackBridge.OnBackpackEquipped   += HandleEquipped;
            BackpackBridge.OnBackpackUnequipped += HandleUnequipped;

            Debug.Log("[VAInventory] BackpackStorageSync subscribed to bridge events.");
        }

        public static void Shutdown()
        {
            BackpackBridge.OnBackpackEquipped   -= HandleEquipped;
            BackpackBridge.OnBackpackUnequipped -= HandleUnequipped;
        }

        // ── Event handlers ────────────────────────────────────────────

        // Tracks which bag instances have already had their stored blob
        // deserialized into the player's grid. Without this, vanilla and
        // mod code paths that re-equip an already-equipped bag (e.g.
        // companion-interaction patches in FiresNPCs that cycle the
        // player's equipment, hotbar tool reselection cascading into
        // EquipItem on the backpack, etc.) would call HandleEquipped a
        // second/third/Nth time. Each call ran DeserializeIntoBlock,
        // which appends the bag's items to inventory at saved positions
        // — and when those positions are already occupied (from the
        // first deserialize), the new copies overflow into main
        // inventory. Result: full inventory of duplicate items, exactly
        // the symptom reported.
        private static readonly System.Collections.Generic.HashSet<ItemDrop.ItemData> _deserializedBags
            = [];

        // ── Per-item ownership tag ────────────────────────────────────
        // Slot positions are reused across bags: weapon-bags (HoboStick,
        // StaffFiresWizard) all live at the same y-strip because only one
        // can be wielded at a time, and shoulder bags shift y-strips up
        // when an earlier bag unequips. Position alone is not a reliable
        // ownership marker — items dropped at a backpack-tagged grid
        // position by anything other than DeserializeIntoBlock would get
        // claimed by whichever bag's slot block currently sits there.
        //
        // Each item we deserialize into a panel gets stamped with its
        // owning bag's prefab name. Capture then filters by tag, so a
        // bag never sucks in items that belong to a different bag — even
        // if they're sitting at this bag's grid positions because of an
        // earlier failed drain or a slot-shift. EvictForeignItems on
        // equip relocates any leftover foreign-tagged items out of the
        // new bag's slot positions before the panel renders.
        //
        // Untagged items are auto-adopted on capture: this is how items
        // dragged from main inventory into a panel get stamped (the
        // capture pass on the next sync sees an untagged item at our
        // slot position, treats it as ours, and writes the tag back).
        private const string OWNER_KEY = "VA_OwnerBackpack";

        private static string GetOwner(ItemDrop.ItemData item)
        {
            if (item?.m_customData == null) return null;
            return item.m_customData.TryGetValue(OWNER_KEY, out var s) ? s : null;
        }

        private static void SetOwner(ItemDrop.ItemData item, string prefab)
        {
            if (item == null || string.IsNullOrEmpty(prefab)) return;
            if (item.m_customData == null) return;
            item.m_customData[OWNER_KEY] = prefab;
        }

        /// <summary>
        /// True if the item carries no owner tag (free agent, e.g. just
        /// dragged in from main inventory) or is tagged for this prefab.
        /// </summary>
        private static bool IsOwnedByOrUntagged(ItemDrop.ItemData item, string prefab)
        {
            string owner = GetOwner(item);
            return owner == null || owner == prefab;
        }

        private static void HandleEquipped(Player player, ItemDrop.ItemData backpackItem)
        {
            if (player == null || backpackItem == null) return;

            // Cascading-equip dedupe. A single user-visible equip can fire
            // HandleEquipped more than once when companion patches or hotbar
            // tool reselection re-trigger EquipItem on the just-equipped
            // bag. Each redundant call's restore phase deserializes the
            // bag's blob into slot positions already occupied by the first
            // call's items, and DeserializeIntoBlock overflows the
            // duplicates into main inventory. HandleUnequipped clears the
            // entry, so a genuine unequip+re-equip still flows through.
            if (_deserializedBags.Contains(backpackItem))
                return;

            try
            {
                // Adding a second bag re-flows every later bag's y-range
                // (e.g. HoboStick goes from y=15-18 to y=19-22 when DayBag
                // is added on top of it). The actual items in the player's
                // Inventory stay at their OLD grid positions, so without
                // intervention they'd end up:
                //   - In the WRONG bag's panel (HoboStick's items at y=15-18
                //     are now under DayBag's slot block).
                //   - Blocking the new bag's deserialize, which then
                //     overflows its items into main inventory.
                //
                // The user perceives this as "everything duplicated" — items
                // appear in both panels (because the old bag's items linger
                // in the new bag's positions) AND scattered through main
                // inventory (DayBag's items overflowed).
                //
                // CAPTURE-REBUILD-RESTORE: serialize every existing bag's
                // current items via slot-ID parsing (slots in Slots.slots
                // still carry the OLD prefab-tagged IDs at this point, so
                // we can match items to their owning bag without depending
                // on the now-stale equippedBags index ordering). Remove
                // those items from the inventory. Then InitializeSlots
                // rebuilds with the new bag included. Finally, deserialize
                // EVERY equipped bag's customData into the NEW slot
                // positions — including the just-equipped one.
                var bagsAfter = BackpackBridge.GetEquippedBackpacks(player);

                // Capture phase: every bag that ALREADY had items laid out
                // (i.e. anything except the bag being equipped right now,
                // which has nothing in inventory yet — its contents live in
                // its customData blob). Use slot-ID parsing so the capture
                // sees the OLD slot allocation, not the index-based one.
                if (bagsAfter != null)
                {
                    foreach (var bag in bagsAfter)
                    {
                        if (bag == null || ReferenceEquals(bag, backpackItem)) continue;
                        string prefab = bag.m_dropPrefab?.name;
                        if (string.IsNullOrEmpty(prefab)) continue;
                        string captured = SerializeFromBlockByPrefab(player, prefab, removeItems: true);
                        if (!string.IsNullOrEmpty(captured))
                            BackpackBridge.WriteStorage(bag, captured);
                    }
                }

                Slots.InitializeSlots();

                // Evict any foreign-tagged items that now sit at one of
                // the freshly-allocated bag panels' grid positions. This
                // is the safety-net for cases where a previous unequip
                // didn't drain (slot-shift race, missed-event), or where
                // a different bag's items are still parked at the y-strip
                // a new bag just took over.
                EvictForeignItems(player);

                // Restore phase: every equipped bag deserializes its blob
                // into its NEW slot positions. The set is NOT cleared here
                // — entries persist across calls so the cascading-equip
                // guard at the top of this method can recognise a
                // redundant fire. HandleUnequipped removes the entry on a
                // genuine unequip.
                if (bagsAfter != null)
                {
                    foreach (var bag in bagsAfter)
                    {
                        if (bag == null) continue;
                        string blob = BackpackBridge.ReadStorage(bag);
                        if (!string.IsNullOrEmpty(blob))
                            DeserializeIntoBlock(player, bag, blob);
                        _deserializedBags.Add(bag);
                    }
                }

                EquipmentPanel.UpdatePanel();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[VAInventory] BackpackStorageSync.HandleEquipped failed: {ex}");
            }
        }

        private static void HandleUnequipped(Player player, ItemDrop.ItemData backpackItem)
        {
            if (player == null || backpackItem == null) return;

            try
            {
                // Mirror the capture-rebuild-restore pattern from
                // HandleEquipped. When a bag is unequipped, every later bag
                // in the slot allocation shifts up by this bag's row count;
                // we have to relocate their items the same way we do on
                // equip. Use slot-ID parsing to capture each bag at its
                // CURRENT (pre-rebuild) position — including the bag being
                // unequipped, whose items must end up in its customData
                // before the slots are torn down.
                string prefabUneq = backpackItem.m_dropPrefab?.name;

                // Capture phase: serialize every backpack-storage-tagged
                // slot's contents into the matching bag's customData.
                var bagsBefore = BackpackBridge.GetEquippedBackpacks(player);
                if (bagsBefore != null)
                {
                    foreach (var bag in bagsBefore)
                    {
                        if (bag == null) continue;
                        string p = bag.m_dropPrefab?.name;
                        if (string.IsNullOrEmpty(p)) continue;
                        string captured = SerializeFromBlockByPrefab(player, p, removeItems: true);

                        // Idempotency guard: OnUnequipItem can fire twice
                        // for the same bag in rapid succession (vanilla
                        // path + our cascading event handlers). The first
                        // pass saves a real blob and drains the items;
                        // the second sees an empty block and would clear
                        // the just-saved customData. Only write when we
                        // actually captured items, OR when the bag's
                        // existing blob is also empty.
                        string existing = BackpackBridge.ReadStorage(bag);
                        bool weHaveItems = !string.IsNullOrEmpty(captured);
                        bool existingHasItems = !string.IsNullOrEmpty(existing);
                        if (weHaveItems || !existingHasItems)
                            BackpackBridge.WriteStorage(bag, captured);
                    }
                }

                // Also explicitly handle the bag being unequipped, in case
                // it's already gone from BackpackBridge.GetEquippedBackpacks
                // by the time we got here (some unequip paths remove from
                // the tracking dict before firing the event).
                if (!string.IsNullOrEmpty(prefabUneq) &&
                    (bagsBefore == null || !bagsBefore.Contains(backpackItem)))
                {
                    string captured = SerializeFromBlockByPrefab(player, prefabUneq, removeItems: true);
                    string existing = BackpackBridge.ReadStorage(backpackItem);
                    bool weHaveItems = !string.IsNullOrEmpty(captured);
                    bool existingHasItems = !string.IsNullOrEmpty(existing);
                    if (weHaveItems || !existingHasItems)
                        BackpackBridge.WriteStorage(backpackItem, captured);
                }

                _deserializedBags.Remove(backpackItem);

                Slots.InitializeSlots();

                // Eviction sweep — same as HandleEquipped. Even on a pure
                // unequip the remaining bags' y-strips can shift up, and
                // any foreign-tagged items sitting at those new positions
                // need to move out before the panel renders.
                EvictForeignItems(player);

                // Restore phase: deserialize every STILL-equipped bag
                // (the just-unequipped one is gone from this list) into
                // its new slot positions.
                var bagsAfter = BackpackBridge.GetEquippedBackpacks(player);
                if (bagsAfter != null)
                {
                    foreach (var bag in bagsAfter)
                    {
                        if (bag == null) continue;
                        string blob = BackpackBridge.ReadStorage(bag);
                        if (!string.IsNullOrEmpty(blob))
                            DeserializeIntoBlock(player, bag, blob);
                        _deserializedBags.Add(bag);
                    }
                }

                EquipmentPanel.UpdatePanel();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[VAInventory] BackpackStorageSync.HandleUnequipped failed: {ex}");
            }
        }

        // ── Serialization helpers ─────────────────────────────────────

        /// <summary>
        /// Walk every backpack-storage slot, collect the items currently
        /// sitting at those grid positions, and pack them into a ZPackage.
        /// Then remove them from the player's Inventory so they don't
        /// linger at orphan grid positions after the block disappears.
        /// </summary>
        public static void SaveEquippedBackpackContents(bool removeItems = false)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Multi-bag: save each equipped bag's block independently.
            // Diagnostic logs were removed here — InventoryGui.Hide can
            // fire every frame in some Valheim builds, and the chatter
            // (`bagCount=0` per frame) drowned the actual log signal.
            var bags = BackpackBridge.GetEquippedBackpacks(player);
            if (bags == null) return;
            foreach (var bag in bags)
            {
                if (bag == null) continue;

                // Confidence check: only sync if the slot system actually
                // has a block allocated for this bag. If GetSlotsForBag
                // returns nothing, the slot rebuild hasn't run yet and
                // SerializeFromBlock would return empty for the wrong
                // reason — writing that would clear a real blob.
                var slotsForBag = GetSlotsForBag(player, bag);
                if (slotsForBag.Count == 0) continue;

                string base64 = SerializeFromBlock(player, bag, removeItems);
                // Write unconditionally — including empty. If the player
                // emptied the panel (drop hotkey, eat-from-panel, drag to
                // main inventory, etc.) the empty write clears the blob
                // so a later re-equip doesn't resurrect items that no
                // longer exist. The HandleUnequipped path keeps its own
                // empty-guard for the cascade-double-fire case; this path
                // is the explicit "panel is ground truth, sync it" save.
                BackpackBridge.WriteStorage(bag, base64);
            }
        }

        /// <summary>
        /// Serialize all items currently sitting at slots tagged with the
        /// given bag prefab name (slot-ID encoding, see
        /// Slots.GetBackpackPrefabFromSlot). Used during the capture phase
        /// of HandleEquipped/HandleUnequipped where the slot allocation
        /// still reflects the PRE-rebuild state — at that moment the
        /// reference-based <see cref="GetSlotsForBag"/> can't match,
        /// because the bag's index in equippedBags has already shifted to
        /// the post-event list.
        /// </summary>
        private static string SerializeFromBlockByPrefab(Player player, string prefab, bool removeItems)
        {
            var inv = player.GetInventory();
            if (inv == null) return string.Empty;
            if (string.IsNullOrEmpty(prefab)) return string.Empty;

            var blockSlots = Slots.AllSlots()
                .Where(s => s != null
                            && s.IsBackpackStorageSlot
                            && Slots.GetBackpackPrefabFromSlot(s) == prefab)
                .ToList();
            if (blockSlots.Count == 0) return string.Empty;

            var collected = new List<(ItemDrop.ItemData item, Vector2i localPos)>();
            int minX = blockSlots.Min(s => s.GridPosition.x);
            int minY = blockSlots.Min(s => s.GridPosition.y);

            foreach (var slot in blockSlots)
            {
                var item = inv.GetItemAt(slot.GridPosition.x, slot.GridPosition.y);
                if (item == null) continue;
                // Ownership filter: only capture items genuinely tagged
                // for this bag (or untagged items, which get auto-adopted).
                // A foreign-tagged item parked at our slot position means
                // we hit the bug from May 2026: HoboStick block allocated
                // over StaffFiresWizard's undrained items. Skipping here
                // keeps us from sucking those items into our blob; the
                // EvictForeignItems pass on equip moves them elsewhere.
                if (!IsOwnedByOrUntagged(item, prefab)) continue;
                // Auto-adopt: stamp the owner now, so a future capture
                // recognises this item as ours even if a slot-shift moves
                // the panel to a different y-strip.
                SetOwner(item, prefab);
                var localPos = new Vector2i(slot.GridPosition.x - minX,
                                            slot.GridPosition.y - minY);
                collected.Add((item, localPos));
            }

            if (collected.Count == 0) return string.Empty;

            int width = blockSlots.Max(s => s.GridPosition.x) - minX + 1;
            int height = blockSlots.Max(s => s.GridPosition.y) - minY + 1;
            var backpackInv = new Inventory("Backpack", null, width, height);
            var backpackItems = backpackInv.GetAllItems();

            foreach (var (item, pos) in collected)
            {
                item.m_gridPos = pos;
                backpackItems.Add(item);
            }

            var pkg = new ZPackage();
            backpackInv.Save(pkg);

            // Restore world positions on the items so the inventory's
            // tracking dictionary keeps pointing at them correctly until
            // we (optionally) RemoveItem them next.
            foreach (var (item, _) in collected)
            {
                item.m_gridPos = new Vector2i(item.m_gridPos.x + minX, item.m_gridPos.y + minY);
            }

            if (removeItems)
            {
                foreach (var (item, _) in collected)
                {
                    inv.RemoveItem(item);
                }
            }

            return pkg.GetBase64();
        }

        /// <summary>
        /// Compute the slot subset belonging to <paramref name="bag"/>
        /// based on its REFERENCE position in BackpackBridge.GetEquippedBackpacks
        /// and the cumulative row counts of bags listed before it. This
        /// avoids any prefab-name string comparison, so modded bags with
        /// non-standard naming (or transient null m_dropPrefab) still
        /// resolve to their own slots without bleeding into other bags'
        /// blocks.
        /// </summary>
        private static List<Slots.Slot> GetSlotsForBag(Player player, ItemDrop.ItemData bag)
        {
            var result = new List<Slots.Slot>();
            if (player == null || bag == null) return result;

            var bags = BackpackBridge.GetEquippedBackpacks(player);
            if (bags == null || bags.Count == 0) return result;

            int bagIdx = -1;
            for (int i = 0; i < bags.Count; i++)
            {
                if (ReferenceEquals(bags[i], bag)) { bagIdx = i; break; }
            }
            if (bagIdx < 0) return result;

            // The bag-storage blocks live at sequential Y-strips below the
            // misc block. Find the start-Y of the first bag block by
            // scanning existing IsBackpackStorageSlot entries — robust to
            // changes in the equipment-panel layout.
            var allBpSlots = Slots.AllSlots()
                .Where(s => s != null && s.IsBackpackStorageSlot)
                .ToList();
            if (allBpSlots.Count == 0) return result;
            int bpStartY = allBpSlots.Min(s => s.GridPosition.y);

            int minY = bpStartY;
            for (int i = 0; i < bagIdx; i++)
            {
                var (priorRows, _) = BackpackBridge.GetConfigDimensions(bags[i]);
                if (priorRows > 0) minY += priorRows;
            }
            var (thisRows, _) = BackpackBridge.GetConfigDimensions(bag);
            if (thisRows <= 0) return result;
            int maxY = minY + thisRows - 1;

            foreach (var s in allBpSlots)
            {
                if (s.GridPosition.y >= minY && s.GridPosition.y <= maxY)
                    result.Add(s);
            }
            return result;
        }

        private static string SerializeFromBlock(Player player, ItemDrop.ItemData backpackItem, bool removeItems)
        {
            var inv = player.GetInventory();
            if (inv == null) return string.Empty;

            // Collect items at THIS bag's storage grid positions, identified
            // by the bag's REFERENCE position in equipped-bags (no string
            // matching). We snapshot first because we'll mutate the
            // inventory while iterating.
            var blockSlots = GetSlotsForBag(player, backpackItem);
            if (blockSlots.Count == 0) return string.Empty;

            var collected = new List<(ItemDrop.ItemData item, Vector2i localPos)>();
            int minX = blockSlots.Min(s => s.GridPosition.x);
            int minY = blockSlots.Min(s => s.GridPosition.y);

            foreach (var slot in blockSlots)
            {
                var item = inv.GetItemAt(slot.GridPosition.x, slot.GridPosition.y);
                if (item == null) continue;
                // Local coordinates inside the block (0,0)-based — that's
                // what we serialize so items drop into the right grid
                // position when the block is reallocated on re-equip.
                var localPos = new Vector2i(slot.GridPosition.x - minX,
                                            slot.GridPosition.y - minY);
                collected.Add((item, localPos));
            }

            if (collected.Count == 0) return string.Empty;

            int width = blockSlots.Max(s => s.GridPosition.x) - minX + 1;
            int height = blockSlots.Max(s => s.GridPosition.y) - minY + 1;
            var backpackInv = new Inventory("Backpack", null, width, height);
            var backpackItems = backpackInv.GetAllItems();

            foreach (var (item, pos) in collected)
            {
                item.m_gridPos = pos;
                backpackItems.Add(item);
            }

            var pkg = new ZPackage();
            backpackInv.Save(pkg);

            foreach (var (item, _) in collected)
            {
                item.m_gridPos = new Vector2i(item.m_gridPos.x + minX, item.m_gridPos.y + minY);
            }

            if (removeItems)
            {
                foreach (var (item, _) in collected)
                {
                    inv.RemoveItem(item);
                }
            }

            return pkg.GetBase64();
        }

        /// <summary>
        /// Decode a ZPackage saved by SerializeFromBlock and place each
        /// item into the freshly allocated backpack-storage block. If the
        /// block is smaller than the saved layout, overflow items spill
        /// into the player's main inventory; if no space there either,
        /// they drop on the ground.
        /// </summary>
        private static void DeserializeIntoBlock(Player player,
                                                 ItemDrop.ItemData backpackItem,
                                                 string base64)
        {
            var inv = player.GetInventory();
            if (inv == null) return;

            // Identify THIS bag's slots by REFERENCE position in equipped-
            // bags (no prefab-name string match) so modded bags with
            // unusual naming still resolve to their own block and never
            // bleed into another bag's slots.
            var blockSlots = GetSlotsForBag(player, backpackItem)
                .OrderBy(s => s.GridPosition.y).ThenBy(s => s.GridPosition.x)
                .ToList();
            if (blockSlots.Count == 0) return;

            int minX = blockSlots.Min(s => s.GridPosition.x);
            int minY = blockSlots.Min(s => s.GridPosition.y);
            int maxLocalX = blockSlots.Max(s => s.GridPosition.x) - minX;
            int maxLocalY = blockSlots.Max(s => s.GridPosition.y) - minY;

            string ownerPrefab = backpackItem.m_dropPrefab?.name;

            ZPackage pkg;
            try { pkg = new ZPackage(base64); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[VAInventory] Backpack storage decode failed: {ex.Message}");
                return;
            }

            var backpackInv = new Inventory("Backpack", null, maxLocalX + 1, maxLocalY + 1);
            try { backpackInv.Load(pkg); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[VAInventory] Backpack storage load failed: {ex.Message}");
                return;
            }

            int alreadyThere = 0;
            foreach (var added in backpackInv.GetAllItems().ToList())
            {
                // Determine whether the saved local position fits in the
                // current block; if not (smaller backpack), let vanilla
                // AddItem find a spot.
                Vector2i pos = added.m_gridPos;
                bool inBounds = pos.x >= 0 && pos.x <= maxLocalX
                             && pos.y >= 0 && pos.y <= maxLocalY;

                if (inBounds)
                {
                    int worldX = pos.x + minX;
                    int worldY = pos.y + minY;
                    var occupant = inv.GetItemAt(worldX, worldY);
                    if (occupant == null || occupant == added)
                    {
                        added.m_gridPos = new Vector2i(worldX, worldY);
                    }
                    else if (IsSameBagItem(occupant, added, ownerPrefab))
                    {
                        // A save from before 1.0.69 kept this item twice: in the player's inventory at this cell
                        // (loaded already) and in the blob. The loaded copy is the item; the blob's is not added again.
                        alreadyThere++;
                        continue;
                    }
                    else if (Slots.TryMakeFreeSpaceInPlayerInventory(true, out var overflowPos))
                    {
                        added.m_gridPos = overflowPos;
                    }
                    else
                    {
                        Debug.LogWarning($"[VAInventory] Backpack overflow: no space for {added.m_shared?.m_name}×{added.m_stack}");
                        continue;
                    }
                }
                else if (!Slots.TryMakeFreeSpaceInPlayerInventory(true, out var overflowPos))
                {
                    Debug.LogWarning($"[VAInventory] Backpack overflow: no space for {added.m_shared?.m_name}×{added.m_stack}");
                    continue;
                }
                else
                {
                    added.m_gridPos = overflowPos;
                }

                // Stamp the owner tag so capture, eviction, and any
                // future slot-shift reorderings can recognise this item
                // as belonging to this specific bag — independent of the
                // grid position it currently sits at.
                SetOwner(added, ownerPrefab);

                inv.GetAllItems().Add(added);
            }

            if (alreadyThere > 0)
                Debug.LogWarning($"[VAInventory] bag restore: {alreadyThere} item(s) of {ownerPrefab} were already in their cells "
                    + "(a save from before 1.0.69 kept both copies); each kept once");
            inv.m_onChanged?.Invoke();
        }

        // The loaded copy of a bag item: same prefab, stack, quality, variant and durability, tagged for this bag.
        private static bool IsSameBagItem(ItemDrop.ItemData occupant, ItemDrop.ItemData saved, string ownerPrefab)
        {
            if (occupant == null || saved == null || string.IsNullOrEmpty(ownerPrefab)) return false;
            if (GetOwner(occupant) != ownerPrefab) return false;
            return occupant.m_dropPrefab != null && saved.m_dropPrefab != null && occupant.m_dropPrefab.name == saved.m_dropPrefab.name
                && occupant.m_stack == saved.m_stack && occupant.m_quality == saved.m_quality && occupant.m_variant == saved.m_variant
                && Mathf.Abs(occupant.m_durability - saved.m_durability) < 0.01f;
        }

        // ── Saving (the relog duplicate, [seasons] 10-07, VABACKPACKS_PAD_TEST C2) ──────────────────────────────────────
        // While a bag is worn its items sit in the player's inventory at the bag's cells AND in the bag's blob (written at
        // InventoryGui.Hide, and never cleared after the equip restore). Player.Save wrote both; on the next login vanilla's
        // Inventory.Load put the saved copies back (no position check), and the equip restore found their cells taken and
        // spilled the blob's copies into the main inventory: every stack doubled per relog. So the save writes each worn
        // bag's cells into its blob and leaves those items out of the saved inventory list; the same ItemData objects go
        // straight back after (no Changed(), nothing else sees it).
        [HarmonyPatch(typeof(Player), nameof(Player.Save))]
        private static class Player_Save_BagItemsOnlyInTheBag
        {
            private static void Prefix(Player __instance, out List<ItemDrop.ItemData> __state)
            {
                __state = null;
                if (__instance == null || __instance != Player.m_localPlayer) return;
                try { __state = PullBagItemsForSave(__instance); }
                catch (System.Exception ex) { Debug.LogError($"[VAInventory] save: bag items not separated: {ex}"); }
            }

            private static void Finalizer(Player __instance, List<ItemDrop.ItemData> __state)
            {
                if (__state == null || __state.Count == 0) return;
                var list = __instance?.GetInventory()?.GetAllItems();
                if (list == null) return;
                foreach (var item in __state)
                    if (!list.Contains(item)) list.Add(item);
            }
        }

        private static List<ItemDrop.ItemData> PullBagItemsForSave(Player player)
        {
            var pulled = new List<ItemDrop.ItemData>();
            var inv = player.GetInventory();
            var bags = BackpackBridge.GetEquippedBackpacks(player);
            if (inv == null || bags == null) return pulled;
            var list = inv.GetAllItems();
            foreach (var bag in bags)
            {
                if (bag == null) continue;
                // No block yet (the slot rebuild hasn't run): the bag's items live only in its blob, which is saved as is.
                var slots = GetSlotsForBag(player, bag);
                if (slots.Count == 0) continue;
                var items = slots.Select(s => inv.GetItemAt(s.GridPosition.x, s.GridPosition.y)).Where(i => i != null).Distinct().ToList();
                BackpackBridge.WriteStorage(bag, SerializeFromBlock(player, bag, removeItems: false));
                foreach (var item in items)
                    if (list.Remove(item)) pulled.Add(item);
            }
            if (pulled.Count > 0 && ConfigManager.IsVerbose)
                Debug.Log($"[VAInventory] save: {pulled.Count} bag item(s) saved inside their bag only");
            return pulled;
        }

        /// <summary>
        /// After Slots.InitializeSlots rebuilds the panel layout, walk
        /// every backpack-storage slot and look for items whose owner
        /// tag mismatches the slot's prefab. Those are foreign items
        /// parked at this bag's grid positions — they must be evicted
        /// or the panel will render them as belonging to the wrong bag
        /// (the May 2026 "empty HoboStick shows FiresStaff items" bug).
        ///
        /// Eviction strategy: relocate to the first free main-inventory
        /// position. If the player's main grid is full, leave the item
        /// in place — it'll get re-evicted next sync. We never drop on
        /// the ground here; that's too destructive for what is usually
        /// a transient mismatch during slot-shift.
        /// </summary>
        private static void EvictForeignItems(Player player)
        {
            var inv = player?.GetInventory();
            if (inv == null) return;

            var bpSlots = Slots.AllSlots()
                .Where(s => s != null && s.IsBackpackStorageSlot)
                .ToList();
            if (bpSlots.Count == 0) return;

            foreach (var slot in bpSlots)
            {
                var item = inv.GetItemAt(slot.GridPosition.x, slot.GridPosition.y);
                if (item == null) continue;

                string slotPrefab = Slots.GetBackpackPrefabFromSlot(slot);
                if (string.IsNullOrEmpty(slotPrefab)) continue;

                string owner = GetOwner(item);
                if (owner == null || owner == slotPrefab) continue;

                // Foreign-tagged item — owner is a different bag (probably
                // unequipped, with its blob holding the canonical copy).
                // Move it out of this bag's slot. RemoveItem first so the
                // inventory's tracking stays consistent, then re-add at a
                // free main-inventory position.
                if (!Slots.TryMakeFreeSpaceInPlayerInventory(true, out var freePos))
                {
                    Debug.LogWarning($"[VAInventory] EvictForeignItems: no free main-inventory slot for '{item.m_shared?.m_name}' (owner={owner}); leaving in place.");
                    continue;
                }
                Debug.Log($"[VAInventory] EvictForeignItems: relocating '{item.m_shared?.m_name}' (owner={owner}) out of {slotPrefab} slot at ({slot.GridPosition.x},{slot.GridPosition.y}) to ({freePos.x},{freePos.y}).");
                item.m_gridPos = freePos;
            }

            inv.m_onChanged?.Invoke();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDestroy))]
        private static class Player_OnDestroy_SaveBackpackStorage
        {
            private static void Prefix(Player __instance)
            {
                if (__instance == Player.m_localPlayer)
                    SaveEquippedBackpackContents(removeItems: false);
            }
        }
    }
}
