# Deprecated — Inventory Refactor 2026-05

Tracks code in `VAInventory/Managers/` that is scheduled for removal by
the refactor in
[`Refs/INVENTORY_REFACTOR_PLAN_2026_05.md`](../Refs/INVENTORY_REFACTOR_PLAN_2026_05.md).

Each entry: **what** to remove, **which phase** removes it, **what
replaces it**, and **acceptance gate** (the test that must pass before
deletion). Don't delete any of these until the gate is green.

---

## Phase 1 — Hotbar rewrite

### `ToolBar.cs` — entire file (2,222 LOC)

Replaced by:
- `Managers/HotBars/HotBarBase.cs` (shared Refresh/Create/Clear)
- `Managers/HotBars/ToolHotBar.cs` (the actual bar — ~95 LOC)
- `Managers/HotBars/HotBarController.cs` (`Hud.Update` postfix +
  `HotkeyBar.UpdateIcons` patch)

The new files clone Valheim's vanilla `HotKeyBar` prefab and bind item
data through `Inventory.GetBoundItems`. Vanilla owns rendering. We do
not reimplement icons, stack counts, drag, tooltips, equipped-frame, or
selection.

Specific dead pieces inside `ToolBar.cs`:

| Method / field | Why dead in new design |
|---|---|
| `recentlyEquippedTimes`, `lastActivationTimes` dicts + helpers (`MarkRecentlyEquipped`, `IsRecentlyEquipped`, `ClearExpiredRecentEquips`, `TryStartActivation`) | Vanilla `HotkeyBar` handles equip-state without this state machine. |
| `cachedVanillaSlotSprite` + `CacheVanillaSlotSprite` | We instantiate the vanilla prefab — the sprite ships with it. |
| `CleanOverlayChildren`, `RemoveNonUIBehavioursRecursively` | We don't synthesize slot GameObjects anymore. |
| `EnsureDragBackground`, `OnBeginDrag`, `OnDrag`, `OnEndDrag`, `ClearSuppressFlagCoroutine` | Vanilla `HotkeyBar` is not draggable; if we want to keep the bar movable, the new `HotBarController` adds it as ~30 LOC. |
| `Update()` polling (slot-key tracking, overlay sync, recent-equip expiry) | Replaced by `Hud.Update` postfix + `HotkeyBar.UpdateIcons` postfix — event-driven, not polled. |
| `RefreshToolSlotElements`, `SetToolSlotElement`, `SyncOverlaysFromVanilla` | Vanilla element rendering. |
| `HideVanillaInventoryIconsForToolSlots`, `HideVanillaHotkeyIconsForToolSlots` | We're no longer parallel-rendering, so nothing to hide. |
| `PerformDropCoroutine`, `InvokeInventoryChangedAndRefreshNextFrame`, `InvokeOnSelectedItemNextFrame`, `EquipCustomSlotItemNextFrame` | Vanilla `OnSelectedItem` flow handles drops; coroutine workarounds become unnecessary. |
| `ActivateSlotByIndex`, `PerformSlotActivate`, `StartPickupFromSlot` | Replaced by `Player.UseHotbarItem(bar.m_selected + 1)` for vanilla bar slots, or our equivalent for custom slots. |
| `LogSlotChildSprites`, `PrintToolbarHierarchy`, `PrintTransformRecursive` | Diagnostic-only; gone with the file. |
| `IsPlayerItemEquipped` overloads, `OnPlayerInventoryChanged`, `CaptureSlotItemKeys`, `SubscribeToInventory`, `UnsubscribeFromInventory` | Replaced by `MarkDirty` + `Refresh` pattern. |

**Migration risk gate.** Phase 1 lands behind a config flag
`configToolHotbarUseLegacy` (default false = use the new bar). The
legacy `ToolBar.cs` stays in the repo for one release cycle. Removal
happens in Phase 6 only after:
- Hotbar has been on the new path for ≥1 week of normal play with no
  user-reported regression.
- Drag-from-grid-into-hotbar-slot, hotkey 1–N equip/unequip,
  selection-frame on selected slot, gamepad cycling all verified.

---

## Phase 2 — Validator pattern

### `EquipmentPanel.cs::MigrateEquipmentPanelItems` (line 1344, ~130 LOC)

Replaced by `Managers/ItemsSlotsValidation.cs`. The validator runs on
events (`OnInventoryChanged`, `Humanoid.SetupEquipment`,
`EquipItem`/`UnequipItem`, `Inventory.MoveAll`,
`TombStone.EasyFitInInventory`, `InventoryGui.Show`) and handles every
case `MigrateEquipmentPanelItems` covers, plus:
- Foreign-tagged items at backpack-storage positions (currently
  `EvictForeignItems` — folded into the validator).
- Items overlapping each other's grid positions.
- Items out of grid bounds.
- Equipped items not in equipment slots.

### `EquipmentPanel.cs::OnConfigChanged` (line 1287)

Becomes a thin wrapper:
```csharp
public static void OnConfigChanged(bool gridSizeReduced, bool equipmentPanelSizeReduced, bool mainGridRowsHidden) {
    Slots.InitializeSlots();
    if (gridSizeReduced || equipmentPanelSizeReduced || mainGridRowsHidden)
        ItemsSlotsValidation.Validate();
    Slots.UpdateSlotsGridPosition();
    UpdatePanel();
}
```

### `EquipmentPanel.cs::RefreshSlotsAndPanel` (line 1306)

Same wrapper treatment — calls validator instead of
`MigrateEquipmentPanelItems` directly.

### `BackpackStorageSync.cs::EvictForeignItems` (line 658, ~50 LOC)

Folded into `ItemsSlotsValidation`. The current call sites
([line 189](BackpackStorageSync.cs:189) and
[line 284](BackpackStorageSync.cs:284)) get replaced by
`ItemsSlotsValidation.MarkDirty()`. The validator runs on the next
frame's tick.

**Migration risk gate.**
- Drop-from-bag-via-hotkey + unequip + re-equip cycle still empties
  the panel correctly (the May 2026 scenario).
- Cross-bag bleed scenario (FiresStaff items + new HoboStick) still
  shows empty panel for the new bag.
- Two-bag swap (DayBag + ExplorerBag) doesn't duplicate items between
  panels.
- Death/respawn doesn't lose backpack items.

---

## Phase 3 — Drop validator

No deletions in Phase 3. New file `Managers/InventoryInteraction.cs`
adds patches; existing piecemeal validators in `EquipmentPanel.cs`
slot-handlers can shrink to no-ops once the central gate is in place,
but actual removal happens in Phase 6.

---

## Phase 4 — Inventory backup

No deletions in Phase 4. New file `Managers/InventoryBackup.cs`.

---

## Phase 5 — `FindEmptySlot` patches

No deletions in Phase 5. Patches added to `InventoryInteraction.cs` (or
a sibling file).

---

## Phase 6 — Final cleanup

When all of the above gates are green:

1. Delete `Managers/ToolBar.cs`.
2. Delete `Managers/EquipmentPanel.cs::MigrateEquipmentPanelItems`,
   `OnConfigChanged` becomes the wrapper above (in-place edit, no
   separate file).
3. Delete `Managers/BackpackStorageSync.cs::EvictForeignItems`.
4. Remove the `configToolHotbarUseLegacy` config entry and its
   `ConfigManager` field.
5. Update `auto-memory/reference_backpack_storage.md` — the ownership
   model now references `ItemsSlotsValidation` as the relocate path,
   not `EvictForeignItems`.
6. Update `MULTI_BACKPACK_PANEL_PLAN.md` and
   `BACKPACK_AND_COIN_SLOT_PLAN.md` to mark deprecated entries as
   superseded by this plan.
7. Delete this `DEPRECATED.md`.

---

## What's NOT being deprecated

These stay as-is — they work and are out of scope for this refactor:

- `Managers/Slots.cs` — the slot definitions themselves. We *add*
  helpers (`TryFindFreeSlotForItem`,
  `TryFindFreeEquipmentSlotForItem`,
  `TryMakeFreeSpaceInPlayerInventory`, `MarkDirty`, `Refresh`) but
  don't replace the existing slot enumeration.
- `Managers/EquipmentPanel.cs` — the per-bag detached panel rendering
  (covered by `MULTI_BACKPACK_PANEL_PLAN.md`, already shipped).
- `Managers/BackpackBridge.cs` — the VABackpacks API bridge.
- `Managers/BackpackStorageSync.cs` — the capture-rebuild-restore
  flow stays. Only `EvictForeignItems` moves out.
- `Managers/InventoryGuiBackgroundPatch.cs`,
  `Managers/UIBackgroundManager.cs` — UI styling, unrelated.
- `Managers/LightenedSlots.cs` — visual indicator system, unrelated.
- All existing Harmony patches in `BackpackManager.cs` (VABackpacks).
  The refactor doesn't touch the equip/unequip event flow itself.
