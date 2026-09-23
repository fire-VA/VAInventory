# Inventory & Hotbar Refactor Plan — May 2026

**Goal.** Rebuild VAInventory's slot/hotbar/item-handling layer on the
patterns proven by `shudnal-ExtraSlots`, so we stop fighting the recurring
item-movement bugs (cross-bag bleed, ghost items at backpack-storage
positions, broken hotbars, post-respawn losses, drag-into-wrong-slot).

The current code grew organically: `ToolBar.cs` is 2,222 lines doing
custom rendering, drag handlers, sprite captures, polling loops, and a
recently-equipped state machine. ExtraSlots achieves the same UX in
~917 lines across five focused files by **letting Valheim's own
`HotkeyBar` component do the rendering** and patching its data sources.
We adopt that approach here.

This is the master plan. Each phase below is a defensible commit on its
own — land it, test it, then move on. Order is dependency-driven.

Companion plans:
- `VAInventory/Refs/MULTI_BACKPACK_PANEL_PLAN.md` (already landed)
- `VAInventory/Refs/BACKPACK_AND_COIN_SLOT_PLAN.md`
- `VABackpacks/Refs/BACKPACK_INVENTORY_REFACTOR_PLAN.md`

---

## Architectural principles (read these first)

These are the rules every phase below must respect. They are the lessons
of the bugs we've already shipped.

1. **Slot ID is the only authoritative ownership marker.** Grid position
   is a rendering detail; slots get re-allocated on every equip/unequip.
   When code asks "who owns this item", the answer must come from a slot
   ID parse or a per-item customData tag, never from grid position alone.
2. **Items carry their owner.** Every item placed in a backpack panel
   gets `m_customData["VA_OwnerBackpack"] = prefab`. Capture and
   eviction filter by this tag. Already in place — extend, don't undo.
3. **Mark dirty + validate-on-event.** No per-frame polling for state
   changes. Subscribe to events (`Inventory.m_onChanged`,
   `Player.OnInventoryChanged`, `Humanoid.SetupEquipment`,
   `EquipItem`/`UnequipItem`, `InventoryGui.Show`) and set a dirty flag.
   The validator runs at most once per frame.
4. **Vanilla owns rendering when we can let it.** Hotbar visuals,
   inventory grid icons, drag visuals, tooltips, stack counts — all of
   it works for free if we ride on the existing components instead of
   rebuilding them.
5. **Never reach for a render-time fix when the data layer is wrong.**
   If items appear in the wrong panel, the fix is in capture/eviction,
   not in the panel renderer.
6. **One save path per data type.**
   - Bag-storage blob (`m_customData["BackpackInventory"]`):
     `BackpackStorageSync.SaveEquippedBackpackContents` only.
   - Equipped-slot memory (`m_customData["VA_EquippedSlot"]`): a single
     `Slots.SaveLastEquippedSlotsToItems()` call on `Player.Save`.
   - Extra-slot grid backup: `InventoryBackup.GetExtraSlotsBackup` on
     `Player.Save`.
   No path writes to a customData key it doesn't own.

---

## Phase 0 — Inventory of what to delete

Before writing new code, document what each phase replaces. This keeps
the diff focused and prevents "wait, we still have the old code path
running in parallel".

**Deliverable.** A `DEPRECATED.md` in each affected folder that lists the
methods/classes to remove once the replacement lands, plus the commit
that finally removes them.

Files most affected:
- `VAInventory/Managers/ToolBar.cs` (2,222 LOC) — Phase 1 replaces ~80%
- `VAInventory/Managers/EquipmentPanel.cs::MigrateEquipmentPanelItems` —
  Phase 2 replaces with the validator pattern
- `VAInventory/Managers/Slots.cs` — gains `MarkDirty`/`Refresh` helpers
  (Phase 1) and a `TryFindFreeSlotForItem` helper (Phase 2)
- `VABackpacks/Managers/BackpackManager.cs` — touched only at the API
  surface; the legacy `Container` path stays gated by `LegacyMode`

No code change in Phase 0 — just the deprecation notes so reviewers can
trace what's being retired.

---

## Phase 1 — Hotbar rewrite (biggest win, biggest LOC reduction)

**Symptom we're killing.** Tool hotbar slots show stale icons, sprite
captures fail in some scene loads, slot-change polling chases inventory
state at 5 Hz, equip/unequip via hotkey races the recently-equipped
guard, drag-and-drop into the bar misbehaves on second equip-cycle.

**Root cause.** We rebuild what Valheim already builds. Vanilla's
`HotkeyBar` MonoBehaviour handles every visual concern (icons, stacks,
quality stars, equipped-frame, drag, tooltips, selection, gamepad
focus). Our `ToolBar.cs` reimplements those by walking sprites, hiding
vanilla overlays, and polling for state.

**Fix — adopt the ExtraSlots architecture.**

1. **Instantiate the vanilla bar.** Single helper:
   ```csharp
   public static RectTransform InstantiateHotKeyBar(string barName) {
       var src = Hud.instance.m_rootObject.transform.Find("HotKeyBar")
                                          .GetComponent<RectTransform>();
       var clone = UnityEngine.Object.Instantiate(src, Hud.instance.m_rootObject.transform, true);
       clone.name = barName;
       clone.localPosition = Vector3.zero;
       clone.SetSiblingIndex(src.GetSiblingIndex() + 1);
       return clone;
   }
   ```
   Reference: `ExtraSlots/HotBars/QuickBars.cs:34-42`.

2. **One hotbar class per slot type.** Each is ~95 lines. Pattern:
   `barName` const, `isDirty` flag, `hotBar`/`hotBarRect`/`hotBarSlots`
   fields, `MarkDirty()`/`Refresh()`/`CreateBar()`/`ClearBar()` methods.
   See `QuickSlotsHotBar.cs` for the canonical 95-line shape.

   For VAInventory we need exactly one bar at first: the **tool
   hotbar** (the existing `Slots.toolHotbarSlots`). Future bars (food,
   ammo) can reuse the same pattern.

3. **Bind item data through `Inventory.GetBoundItems`.** When vanilla
   `HotkeyBar.UpdateIcons` runs for our bar, our patch swaps in the
   slot's items. Reference: `QuickBars.cs:331-352`. Vanilla then
   handles every visual element. Zero custom rendering code.

4. **Single update entry point.** Postfix on `Hud.Update` that calls
   `ToolHotbar.Refresh()`. If it returns true (something rebuilt), reset
   the joypad bar list. Reference: `QuickBars.cs:217-258`.

5. **`HotkeyBar.UpdateIcons` postfix for our bar names only.** Used to
   override `m_amount`/element position based on config (rows/columns,
   fill direction, element spacing). Reference: `QuickBars.cs:273-329`.

**Files added:**
- `VAInventory/Managers/HotBars/HotBarBase.cs` (~50 LOC, shared
  Refresh/Create/Clear logic factored out)
- `VAInventory/Managers/HotBars/ToolHotBar.cs` (~95 LOC, the actual bar)
- `VAInventory/Managers/HotBars/HotBarController.cs` (~150 LOC, owns
  the `Hud.Update` postfix and `HotkeyBar.UpdateIcons` patch)

**Files deleted:**
- `VAInventory/Managers/ToolBar.cs` (2,222 LOC) — entirely.

**Net LOC.** −1,927.

**Acceptance.**
- Tool hotbar renders icons, stacks, quality stars, equipped-frames,
  tooltips identical to the vanilla hotbar.
- Hotkey 1–N equips/unequips the slot's item via vanilla
  `Player.UseHotbarItem`.
- Drag from inventory grid to hotbar slot works once and stays.
- No sprite-capture warnings in logs across 5 minutes of normal play.
- Hotbar appears/disappears on the same triggers as the existing one
  (config flag + inventory-visible + slot-has-item).

**Migration risk.** High visibility (hotbars are constantly on screen).
Land behind a `configToolHotbarUseLegacy` flag — default to **new**, but
a single config flip reverts to the old `ToolBar.cs` for one release
cycle in case of regressions. Remove the legacy path on the next
release.

---

## Phase 2 — Validator pattern for item placement

**Symptom we're killing.** `MigrateEquipmentPanelItems` is a 130-line
"relocate everything that doesn't fit" routine that has been the source
of two distinct duplication bugs (the May 2026 "items teleport to main
inventory" and the cross-bag bleed). It runs only on config change but
its logic is brittle. We don't have a generalized "item is in the wrong
place — find it a home" routine that the rest of the code can call.

**Fix — port the ExtraSlots validator.**

1. **`ItemsSlotsValidation` class** in `VAInventory/Managers/`. Two
   nested static classes (`SlotsValidation`, `ItemsValidation`), each
   with a `MarkDirty()` and a `Validate()`. Reference:
   `ExtraSlots/ItemsSlotsValidation.cs`.

2. **Triggers (Harmony patches):**
   - `Inventory.MoveAll` postfix — items got bulk-moved, recheck.
   - `TombStone.EasyFitInInventory` postfix — death loot just merged in.
   - `Humanoid.EquipItem` / `UnequipItem` prefix — gear changed.
   - `Player.OnInventoryChanged` postfix — generic inventory mutation.
   - `Humanoid.SetupEquipment` postfix — equipment rebuilt.
   - `InventoryGui.Show` postfix — opening the panel revalidates.

3. **Validation steps (per `ItemsValidation.Validate`):**
   - Walk every item in `Slots.PlayerInventory`.
   - **If item is equipped but not in an equipment slot** → find a free
     equipment slot or swap with an unequipped item in one.
   - **If item overlaps another item's grid position** → relocate via
     `PutIntoFirstEmptySlot`.
   - **If item is out of grid bounds** → relocate via
     `PutIntoFirstEmptySlot`.
   - **If item is in a backpack-storage slot but its `VA_OwnerBackpack`
     tag doesn't match the slot prefab** → relocate (this is the
     existing `EvictForeignItems` logic, folded into the validator).

4. **`PutIntoFirstEmptySlot` helper** with the four-step fallback:
   1. Saved last-slot from `m_customData["VA_LastSlot"]` (if active and
      fits).
   2. `Inventory.FindEmptySlot(true)` (vanilla's main-grid slot).
   3. `Slots.TryFindFreeSlotForItem` (any active extra slot the item
      fits).
   4. `Slots.TryMakeFreeSpaceInPlayerInventory` (worst-case grow).

   Reference: `ExtraSlots/ItemsSlotsValidation.cs:18-47`.

**Files added:**
- `VAInventory/Managers/ItemsSlotsValidation.cs` (~250 LOC).
- Helpers `Slots.TryFindFreeSlotForItem`,
  `Slots.TryFindFreeEquipmentSlotForItem`,
  `Slots.TryMakeFreeSpaceInPlayerInventory`,
  `Slots.GetItemSlot`, `Slots.GetSlotInGrid` if not present
  (some already exist, audit and add what's missing).

**Files modified:**
- `VAInventory/Managers/EquipmentPanel.cs` — `MigrateEquipmentPanelItems`
  becomes a thin wrapper that just calls `ItemsSlotsValidation.Validate()`
  on config change. Eventually deleted.
- `VAInventory/Managers/BackpackStorageSync.cs::EvictForeignItems` —
  inlines into the validator's per-item checks. The standalone method
  goes away.

**Acceptance.**
- All scenarios in `MULTI_BACKPACK_PANEL_PLAN.md` Phase 1 still pass.
- The May 2026 "drop from bag panel via hotkey, then unequip, then
  re-equip" scenario is handled by the validator catching the empty
  panel state on `OnInventoryChanged`.
- No more bespoke "search for orphan items at backpack-storage
  positions" code — the validator handles it.

---

## Phase 3 — Central drop validator (`PassDropItem`)

**Symptom we're killing.** Players can drop equipped items into wrong
slot types (e.g. a chestplate onto a food slot), swap items between
incompatible slots, or drop into occupied equipment slots. We have
piecemeal checks but no single gate.

**Fix.** Port `InventoryInteraction.PassDropItem`. Single function,
patched on:
- `InventoryGui.OnSelectedItem` Prefix — covers vanilla drag-drop in
  the inventory window.
- `InventoryGrid.DropItem` Prefix — covers programmatic drops.

Logic:
- Same-position drop → allow (no-op).
- Equipped item dropping out of equipment slot → block unless dest is
  same slot type.
- Drop into a slot the item doesn't fit → block.
- Drop into occupied equipment slot when source isn't an equipment
  slot → block.
- Swap target needs to fit source's slot too → block if not.

Reference: `ExtraSlots/InventoryInteraction.cs:47-131`.

**Files added:**
- `VAInventory/Managers/InventoryInteraction.cs` (~180 LOC).

**Acceptance.**
- Cannot drop chest armor onto food slot.
- Cannot break the equipment grid by drag-dropping equipped item to a
  random main-inventory cell while it's still `m_equipped`.
- Backpack-storage slot drop validation flows through the same gate.

---

## Phase 4 — Inventory backup on save

**Symptom we're hedging against.** Items occasionally vanish on
respawn. We've patched specific instances, but a player-wide safety net
costs almost nothing and would have saved us the past three "items
gone" bug reports.

**Fix.** Port `InventoryBackup`. On `Player.Save` Prefix, JSON-stash a
compressed `ZPackage` of the extra-slot grid into
`m_customData["VAInventoryBackup"]`. On `Player.Load` Postfix, if the
extra slots are *all* empty AND the backup exists AND row count hasn't
changed in a way we can't reconcile, restore the backup.

Skip auto-restore if any extra slot already has items (player legitimately
emptied them).

Reference: `ExtraSlots/InventoryBackup.cs`.

**Files added:**
- `VAInventory/Managers/InventoryBackup.cs` (~165 LOC).
- Config flag `configBackupEnabled` (default true).

**Acceptance.**
- After a fresh save+load, no behavior change.
- After a synthetic test that wipes the extra-slot grid mid-session
  then reloads from disk, the items are restored from backup.
- Backup is gated off automatically on world-character (server-side
  characters) since the server is authoritative there.

---

## Phase 5 — `FindEmptySlot` / `CanAddItem` / `GetEmptySlots` patches

**Symptom we're killing.** When NPCs (or commands like `additem`) call
`Inventory.AddItem(name, count)`, vanilla's `FindEmptySlot` doesn't
know our extra rows are reserved. Items can land in food/ammo slots, or
the call falsely fails because vanilla counts our extra slots as empty.

**Fix — port the height-trick patches.** `Inventory_FindEmptySlot`
prefix sets `m_height` to `InventoryHeightPlayer` for the duration of
the call (so vanilla only searches main inventory), then postfix
restores `InventoryHeightFull` and routes to the equipment-slot finder
or quick-slot finder. Same trick for `GetEmptySlots`, `CanAddItem`,
`HaveEmptySlot`, `AddItem(ItemData,...)`.

Reference: `ExtraSlots/InventoryInteraction.cs:200-470`.

**Files modified:**
- `VAInventory/Managers/InventoryInteraction.cs` (extends Phase 3).

**Acceptance.**
- `additem boar_meat 50` lands in main inventory, not food slots.
- NPC trade dialogs that run vanilla `CanAddItem` give correct yes/no.

---

## Phase 6 — Cleanup & deletion

After phases 1–5 land and stabilize:

- Delete `ToolBar.cs` (replaced by Phase 1).
- Delete `MigrateEquipmentPanelItems` (replaced by Phase 2).
- Delete `EvictForeignItems` standalone (folded into Phase 2).
- Delete every dead reflection helper in the legacy ToolBar (slot key
  polling, sprite cache, recently-equipped state machine).
- Remove the `configToolHotbarUseLegacy` flag.
- Update `BACKPACK_AND_COIN_SLOT_PLAN.md` to mark items moved into
  this plan.
- Update auto-memory `reference_backpack_storage.md` with the new
  ownership-tag + validator design.

---

## Out of scope (deferred to later refactor)

- Rebuilding the detached per-bag panels themselves
  (`MULTI_BACKPACK_PANEL_PLAN.md` already covers them and they work).
- VABackpacks legacy `Container` path. Stays as-is, gated by
  `LegacyMode`. Removing it is a separate plan.
- Equipment-and-quick-slots compat shims and the other compatibility
  files in `ExtraSlots/Compatibility/`. Most don't apply to us.
- Localization. Our strings stay where they are.

---

## Risk register

| Risk | Mitigation |
|---|---|
| Phase 1 hotbar regression breaks user UX | Behind a config flag for one release; revert by flipping the flag |
| Validator (Phase 2) finds items it shouldn't relocate | Strict skip-list: backpack-storage slots with matching owner tag, always-visible equipment slots, items the player is actively dragging |
| `FindEmptySlot` height-trick races a parallel access | Vanilla `Inventory` is single-threaded on the main thread; this is the same trick ExtraSlots has shipped for two years without incident |
| Backup grows too large and bloats save file | Already compressed (`ZPackage.WriteCompressed`); ExtraSlots reports ~1KB compressed for full extra-slot grid. If we want a hard cap, add a `nrOfItems` ceiling check |
| Two mods both patch `Inventory.FindEmptySlot` | Both ExtraSlots and us already use `[HarmonyPriority(800)]`; Harmony composes them in priority order. Test with ExtraSlots also installed |

---

## Order of operations

```
Phase 0 (deprecation notes)        ← 1 day
   ↓
Phase 1 (hotbar rewrite)           ← 2-3 days
   ↓
Phase 2 (validator pattern)        ← 2 days
   ↓
Phase 3 (drop validator)           ← 1 day
   ↓
Phase 4 (inventory backup)         ← 1 day
   ↓
Phase 5 (FindEmptySlot patches)    ← 1 day
   ↓
Phase 6 (cleanup)                  ← 1 day
```

Each phase ends with a tested commit. No phase blocks if the next one
slips — they compose, but each delivers value standalone.
