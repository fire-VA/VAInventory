# Multi-Backpack Detached-Panel Plan

**Goal.** Refactor the backpack-storage system so:
- Each equipped backpack (Shoulder-type bag, HoboStick, future weapon-bag combos) has its own **detached, draggable panel** with config-saved position keyed by prefab name.
- Storage in each panel's grid persists to that bag's `BackpackInventory` customData blob — no cross-talk, no lost items on unequip, no empty deploys.
- The current architecture only supports one "the equipped backpack" — we are generalizing to N concurrent bags.
- A camp deployed at a zone border refreshes the connecting `TerrainComp`s so the dirt patch doesn't tear the mesh at zone edges.

This is a multi-step refactor across **VABackpacks**, **VAInventory**, and **VABackpacks/Camp**. Each phase below is a defensible commit on its own — land 1 → test → land 2 → test → etc.

Companion plans (existing): `VAInventory/Refs/BACKPACK_AND_COIN_SLOT_PLAN.md`, `VABackpacks/Refs/BACKPACK_INVENTORY_REFACTOR_PLAN.md`.

---

## Phase 1 — Single-bag persistence bug (foundation)

**Symptom.** After unequipping a bag with items, the bag's `BackpackInventory` customData reads empty on re-equip, and HoboStick deploy logs `customData with 0 entries`.

**Root cause hypotheses (in priority order).**
1. `OnUnequipItem` fires twice in succession (Postfix on hijack-prefix path + cascading change events). Pass 1 saves a real blob, pass 2 sees an empty block (already drained) and `WriteStorage("")` *clears the key*.
2. `SerializeFromBlock` runs after the items have already been removed from the storage-block grid positions by `inv.RemoveItem` cascades.

**Fix.**
- `BackpackApi.WriteStorage` keeps current "empty string clears" semantics (intentional).
- `BackpackStorageSync.HandleUnequipped` becomes idempotent: if `SerializeFromBlock` returns empty AND the bag already has a non-empty `BackpackInventory` blob, do **not** call `WriteStorage` — preserve what we wrote on the first pass.
- Add diagnostic log: `[VAInventory] Storage save: bag=<name> items=<count> blobBytes=<n>` — tells us in one line which pass actually saved.
- Test: equip bag, drop items in storage, unequip, re-equip → items must reappear.

**Acceptance.** After Phase 1 lands, the persistence round-trip works for one bag at a time. HoboStick camp deploy gets a non-empty `BackpackInventory` customData on the spawned drop. Camp chest still empty in this phase — that's Phase 2.

---

## Phase 2 — Camp-deploy chest hydrates from bag's customData

**Symptom.** Camp deployed from a full HoboStick has an empty chest. Reverse direction (pack-up) works.

**Likely outcome.** Phase 1 fixes the upstream — the customData on the spawned drop will already have the blob. The chest-hydration code (`LoadBackpackContentsIntoContainer`) already reads the blob; it's just been seeing an empty one.

**Verify.** With Phase 1 in place, deploy a full bag → chest items present.

**If still broken.** The capture happens *after* unequip but the `BackpackInventory` write-back races the post-unequip cascade. Fix: in the deploy coroutine, after `UnequipItem` and the one-frame yield, **explicitly** call `BackpackApi.WriteStorage(stick, base64FromSync)` using the most recent serialized blob (pull from the same place `HandleUnequipped` got it). This becomes belt-and-suspenders.

---

## Phase 3 — Multi-bag API + bridge

**`VABackpacks/Managers/BackpackApi`.**
- `public static IReadOnlyList<ItemDrop.ItemData> GetEquippedBackpacks(Player player)` — returns every currently-equipped registered backpack on the player, in stable order (e.g., shoulder first, then weapon slots, then armor slots, then coexistence dict). Empty list if none.
- Existing `GetEquippedBackpack` → keep, returns `[0]` or `null` for backwards compat.
- `BackpackManager.FindEquippedBackpack` → new sibling `FindAllEquippedBackpacks` that doesn't early-return on first hit.

**`VAInventory/Managers/BackpackBridge`.**
- New `IReadOnlyList<ItemDrop.ItemData> GetEquippedBackpacks(Player)` mirrors API.
- `OnBackpackEquipped` / `OnBackpackUnequipped` already pass the specific item — keep semantics, just allow N firings.

**No UI changes yet.** Phase 3 is API plumbing only; old single-bag code continues to work because `Slots.InitializeSlots` still calls `GetEquippedRows(player)` which (for now) returns the first bag's rows.

**Acceptance.** Existing single-bag flow unchanged. Equipping two bags fires two `OnBackpackEquipped` events, and the API/bridge can enumerate both.

---

## Phase 4 — Per-bag storage in the slot system

**Slot ID format.** `BackpackStorage_<bagPrefab>_<col>_<row>` instead of `BackpackStorage_<col>_<row>`. The prefab name lets every helper figure out which bag a slot belongs to.

**`Slots.InitializeSlots`.**
- Iterate `BackpackBridge.GetEquippedBackpacks(player)`. For each, allocate a `rows × cols` block at a *separate* logical region (since each panel is detached, the y-coordinate doesn't matter visually — but we still need unique grid positions in the player's `Inventory` so items round-trip through `Inventory.Save/Load`).
- Block 0 starts at y=15 (current behavior). Block 1 at y=15+block0Height, etc. These are off-screen-but-valid inventory positions; the panels render them at their own positions visually.

**`Slots.IsBackpackStorageSlot`** — keep predicate. Add `Slots.GetBackpackPrefabFromSlot(Slot)` extracting the prefab from the ID.

**`BackpackStorageSync`.**
- `HandleEquipped(player, item)` → allocate that *one* bag's block in the slot list, deserialize from `item.customData`. Don't blow away other bags' blocks.
- `HandleUnequipped(player, item)` → serialize *only that bag's* slots (filtered by prefab name in slot ID), write to `item.customData`, then remove that bag's block from the slot list.
- `SerializeFromBlock`/`DeserializeIntoBlock` take a `prefab` parameter and only touch slots matching it.
- `SaveEquippedBackpackContents` (the InventoryGui.Hide and Player.OnDestroy save hook) iterates all equipped bags.

**Acceptance.** Equip stick → grid block 1 appears, items round-trip. Equip shoulder bag while stick is on → grid block 2 appears, items round-trip independently. Unequip stick → only stick's block goes away, shoulder bag's items untouched.

---

## Phase 5 — Detached, per-bag UI panels

**`VAInventory/Managers/BackpackPanel.cs`** (new file).
- One `BackpackPanel` GameObject per equipped bag. Created in `HandleEquipped`, destroyed in `HandleUnequipped`.
- Parented to HUD root (sibling to the equipment panel — not a child).
- Anchored position persisted to a per-prefab config entry: `Backpack_panel_pos_<prefab>` (Vector2). Restored on creation.
- Header bar with bag name + drag handle. Drag updates anchored position; on `OnPointerUp`, write to config (`ConfigEntry<Vector2>.Value = newPos`).
- Body: `GridLayoutGroup` containing `rows × cols` slot Image objects, mirroring the underlying inventory grid positions for the bag's block.
- Slot click/drag forwards to the existing inventory grid system — they're just visual proxies for the same `Inventory` items.

**`Slots.cs` / `EquipmentPanel.cs`.**
- Remove the backpack block from the equipment panel's visual layout. The slot data still exists in `Slots.slots` (positions y≥15) so vanilla and other code see them; only the visual rendering moves.
- Equipment panel reverts to fixed 4-row layout (the row-15+ block is no longer rendered there).

**Padding.** `BackpackPanel.Body` uses a `GridLayoutGroup` with `padding = 8` (or configurable) and `spacing = 4` so cells aren't flush against the background.

**Acceptance.** Each equipped bag shows its own panel. Drag panel → position remembered across logout. Items in panels round-trip. Two bags = two panels, both independently movable.

---

## Phase 6 — Zone-border terrain refresh on camp deploy

**Symptom.** Camp deployed with a dirt-patch radius that crosses a zone boundary leaves a visible mesh gap at the boundary; the patched zone's `TerrainComp` got the height changes, but the neighbor zone's didn't, so their meshes don't match at the seam.

**Approach.**
- In `BackpackCampManager.DeployFromItemDrop` after `CampTerrain.Level(...)` runs, compute the world-space axis-aligned bounds of the patch (`center ± radius`).
- Convert bounds corners to zone IDs via `ZoneSystem.instance.GetZone(Vector3)`.
- For each unique zone touched (typically 1, but up to 4 at a corner intersection), find the `Heightmap` covering that zone and call `m_buildData = null; Regenerate();` (or whatever the public refresh entry-point is — verify against `assembly_valheim` source).
- For each `TerrainComp` in those zones, call `Save()` then force `m_hmap.Regenerate()`.

**Edge cases.**
- The patched zone is the one we're standing in — already refreshed by vanilla after `Level`, but defensive re-call is harmless.
- Neighbor zone is unloaded → skip; it'll regenerate with correct heights when streamed in (TerrainComp persists its delta).

**Acceptance.** Deploy camp 1m from a zone border → no visible mesh tear at the seam.

---

## Sequencing & risk

| Phase | Lines touched | Risk | Test | Status |
|-------|---------------|------|------|--------|
| 1 | ~30 | Low | Equip→fill→unequip→re-equip, items present | ✅ landed |
| 2 | ~10 | Low (likely free with #1) | Deploy full HoboStick, chest has items | ⏳ verify after build |
| 3 | ~80 | Low (additive API) | Equip 2 bags, two events fire | ✅ landed |
| 4 | ~150 | Medium (slot grid logic) | 2 bags round-trip independently | ✅ landed |
| 5 | ~250 | High (new UI) | Drag panels, persist positions | ✅ landed |
| 6 | ~50 | Low (additive) | Deploy at zone border, no tears | ✅ landed (3×3 search + InActiveArea filter dropped) |

Land in order. Don't skip Phase 1 — every later phase depends on the persistence round-trip being correct.

### Notes on the Phase 5 implementation

- Per-bag panels live on `EquipmentPanel.backpackPanels` (Dict<prefab, RectTransform>); old `backpackBackground` field is gone.
- Each panel parented to `InventoryGui.instance.m_player`, sibling of `equipmentBackground`, rendered with `SetAsLastSibling()` so the draggable header sits above the inventory grid.
- Position persisted via single `ConfigEntry<string>` `BackpackPanelPositions` (format: `prefab=x,y|prefab=x,y`) plus `ConfigManager.{Try}{Get,Set}BackpackPanelPosition` helpers.
- `UniversalDragHandler.OnPositionChanged` callback fires only when the panel actually moved (not on plain clicks or item drops over the panel), so cell-drop interactions don't accidentally re-save the panel position.
- Cells inside each panel are inset by `BackpackPanelPadding = 8f` so the grid doesn't touch the background edge.
- Default first-run layout: panels cascade vertically to the right of the equipment panel (`baseX = EquipmentPanelWidth + 32`, `baseY = -i*(panelH + 16)`).

---

## Open questions (none blocking)

- **Slot click forwarding (Phase 5).** Current equipment-panel slots forward clicks via `EquipmentPanel.RefreshPanelOnly` and the inventory grid patches. The detached panel needs the same wiring. May share a common slot-cell prefab.
- **Save format.** Per-bag panel position is a `ConfigEntry<Vector2>` per prefab — no migration needed since this is new state.
- **Backwards compat.** Old saves with `BackpackInventory` blobs continue to work — the blob format itself doesn't change.
