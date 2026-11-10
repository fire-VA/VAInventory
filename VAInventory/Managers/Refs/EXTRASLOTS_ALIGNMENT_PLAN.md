# ExtraSlots-aligned hotbar refactor — gameplan + status

> **Resume note:** Phase G cleanup has shipped after in-game verification
> of the cloned hotbar path. The legacy `ToolBar.cs`, `ToolBarDropHandler.cs`,
> and `PlayerPatches.cs` files are deleted, and `HotbarManager.cs` now only
> hosts the runtime-safe vanilla hotbar suppression patch.

## Why this plan exists

The previous tool hotbar implementation fought raycast priority,
canvas sort orders, and a +10 Y-coordinate gap that put tool slots
off-screen in the open-inventory grid view. shudnal-ExtraSlots solves
the exact same problem cleanly:

- Slot grid positions sit *adjacent* to vanilla inventory rows (no gaps).
- The cloned HotKeyBar is **purely visual** — vanilla's `HotkeyBar`
  component handles rendering, our patches just swap the bound items.
- All drag/drop interaction routes through **vanilla `InventoryGrid.DropItem`**
  on the height-extended player inventory grid — no bespoke pointer
  handlers, no canvas sort-order wars.
- Hotkey input conflicts are handled by patching `ZInput.TryGetButtonState`
  to suppress vanilla hotbar input when our keys match.

Adopting that architecture for VAInventory removes ~3000 LOC of custom
rendering / drag handling and produces a system that behaves correctly
with no raycast trickery. The one explicit deviation from ExtraSlots is
**preserving mouse-drag bar repositioning** (added back via a focused
component on the cloned bar root, gated on `m_dragItem == null` so it
never steals events from inventory item drops).

## Goals

1. ⚠️ **Three hotbars instead of one**: Tool, Ammo, Food — each a clone
   of vanilla's `HotKeyBar`, each bound to its own `Slots.Slot[]` set,
   each independently positionable. Code is present, but in-game render
   output is wrong: ammo displays as 4 slots, food displays as 3, and
   the tool bar is not reliably visible.
2. ⚠️ **Slots placed contiguously** with the rest of the inventory grid
   (no +10 gap). Tool hotbar at `InventoryHeightPlayer + 0`, equipment
   panel relative to `InventoryHeightPlayer + EquipmentPanelRowOffset`.
   Runtime migration is unstable when extra rows / equipment-panel rows
   are changed, and items are being moved/dropped unexpectedly.
3. ⚠️ **Drag-to-reposition the visual bars** — `HotBarDragHandle` on
   each bar root, gated on `InventoryGui.m_dragItem == null`. Position
   persists to config on EndDrag. Position changes are applying, but the
   visual bars still appear hidden/misaligned depending on slider state.
4. ⚠️ **Hotkey input** for each bar via the ExtraSlots
   `PreventSimilarHotkeys` pattern — Phase F code is now in place for
   tool/food/ammo modifier hotkeys and vanilla-hotbar suppression, pending
   in-game verification.
5. ⏳ **Delete the legacy paths** (`ToolBar.cs` ~2222 LOC, the legacy
   portions of `HotbarManager.cs`) — Phase G (not yet implemented;
   `configToolHotbarUseLegacy` default flipped to `false` so the new
   path is active by default but legacy code is still present as a
   safety net).

## Final slot Y-coord layout

Vanilla inventory: `y = 0..3` (`InventoryHeightPlayer = 4 + ExtraRowsPlayer`).

After Phase A all extra slots are contiguous starting at
`y = InventoryHeightPlayer`:

```
y = InventoryHeightPlayer + 0 : Tool hotbar slots   (8 cells, one row)
y = InventoryHeightPlayer + 1 : (reserved AmmoHotbarRowOffset)
y = InventoryHeightPlayer + 2 : (reserved FoodHotbarRowOffset)
y = InventoryHeightPlayer + 3 : Equipment row 0     (helmet, shoulder, food, ammo)
y = InventoryHeightPlayer + 4 : Equipment row 1     (chest, utility, food, ammo)
y = InventoryHeightPlayer + 5 : Equipment row 2     (legs, trinket, food, ammo)
y = InventoryHeightPlayer + 6 : Equipment row 3     (coin, backpack, ring, teleport)
y = InventoryHeightPlayer + 7+ : Misc block + backpack blocks
```

Constants live on `Slots`:

```csharp
public const int ToolHotbarRowOffset = 0;
public const int AmmoHotbarRowOffset = 1;       // reserved (food/ammo currently still equipment-panel columns)
public const int FoodHotbarRowOffset = 2;       // reserved
public const int EquipmentPanelRowOffset = 3;
public const int EquipmentPanelRowCount = 4;
public const int MiscBlockRowOffset = EquipmentPanelRowOffset + EquipmentPanelRowCount; // 7
```

### Note on ammo/food placement

The Phase D AmmoHotBar and FoodHotBar are visual mirrors of the
*existing* equipment-panel ammo/food slots (which currently still live
inside the equipment panel rows as columns 2/3, not on their own
rows). The reserved offsets `AmmoHotbarRowOffset = 1` and
`FoodHotbarRowOffset = 2` are placeholders for a future slot move
(would let ammo/food show as their own contiguous rows in the open
inventory grid like ExtraSlots does for QuickSlots). Until that move
happens, ammo/food slots only render in the equipment panel; the
hotbars at the user's chosen screen position display the same items.

## Phase status

| Phase | Status | Files                                                                |
|-------|--------|----------------------------------------------------------------------|
| A     | ✅     | `Managers/Slots.cs` |
| B     | ✅     | `Managers/ItemSlotValidation.cs` |
| C     | ✅     | `Managers/HotBars/ToolHotBar.cs`, `Managers/HotBars/HotBarController.cs` |
| D     | ✅     | `Managers/HotBars/AmmoHotBar.cs`, `Managers/HotBars/FoodHotBar.cs`, `Managers/ConfigManager.cs` |
| E     | ✅     | `Managers/HotBars/HotBarDragHandle.cs`, `Managers/HotBars/*HotBar.cs` |
| F     | ✅     | `Managers/HotBars/HotkeyInput.cs`, `Managers/HotBars/HotBarController.cs`, `Managers/HotbarManager.cs` |
| G     | ✅     | Deleted legacy toolbar path; `HotbarManager.cs` reduced to vanilla-hotbar suppression |

## 2026-05-06 in-game test findings — new blockers

These findings supersede the earlier "Phases A–E complete" assumption.
The code creates the cloned bars and slot arrays, but the runtime behavior
is not acceptable yet.

### Observed UI/rendering failures

- Logs show all three cloned bars being created:
  - `[VAInventory] ToolHotBar.CreateBar: created 'VA_ToolHotBar'.`
  - `[VAInventory] AmmoHotBar.CreateBar: created 'VA_AmmoHotBar'.`
  - `[VAInventory] FoodHotBar.CreateBar: created 'VA_FoodHotBar'.`
- The **ammo bar renders as 4 slots** even though ammo should be 3 slots.
- The **food bar renders as the expected 3 slots**, but only the last
  placed food item is visually represented on the hotbar.
- The **tool bar is not reliably visible**. Depending on slider changes,
  the user may briefly see tool slots, but most positions/config states
  show no usable toolbar.
- The **tool hotbar binding labels are not being applied**. The cloned
  elements still show vanilla `1`, `2`, `3`, `4`, ... instead of the
  configured `Alt+1`, `Alt+2`, ... text.
- Moving the tool hotbar position slider writes config and logs the new
  position, but it does not make the bar reliably visible:
  - Example logged positions include `(34.75, 278.75)`, `(131.50, 278.75)`,
    and `(295.00, 323.75)`.

### Observed layout/migration failures

- Changing extra inventory rows or equipment-panel rows causes the
  equipment panel to stop lining up correctly.
- Changing those row counts triggers repeated inventory height changes,
  for example `23 -> 22`, `22 -> 21`, `21 -> 20`, then back up.
- During those changes, validation/migration repeatedly collects items
  from "hidden grid" positions and restricted slots, then re-adds them
  into different slots.
- Items are being moved into invalid/restricted slots during migration,
  including examples from the logs where non-ammo items end up in ammo
  rows before being collected again:
  - `FiresFirstBackpack` in an `Ammo` slot
  - `$item_coins` in an `Ammo` slot
  - `$item_legs_wolf` / `$item_chest_wolf` in ammo-related positions
- Migration can duplicate-looking placements or preserve stale item
  references across row changes. Logs show repeated cases where the same
  ammo/food item appears in multiple post-migrate slots or only one of
  the expected slots remains represented.
- Migration is dropping user items when it cannot find valid destination
  slots, including arrows, backpacks, armor, food, and tools. This must
  be treated as data-loss risk until fixed.

### Current working theory

1. **Confirmed from the local ExtraSlots source:** cloned bars must be
   driven explicitly. ExtraSlots calls each cloned `HotkeyBar.UpdateIcons`
   from its `Hud.Update` patch; VAInventory was only creating/refreshing
   the clones and relying on the HUD path to update them.
2. **Confirmed from the local ExtraSlots source:** items used for cloned
   hotbar rendering may need temporary `m_gridPos` normalization before
   `HotkeyBar.UpdateIcons`, followed by restore in the postfix.
   ExtraSlots does this for food slots with `FoodSlotsHotBar.AdaptGridPos`
   / `RestoreGridPos`. VAInventory's ammo and food equipment-panel slots
   are vertical columns, so both bars need a stronger dense-x adaptation
   while rendering.
3. **The cloned `HotkeyBar` path is still using vanilla hotbar semantics.**
   `Inventory.GetBoundItems` returns a compact list of items, but vanilla
   `HotkeyBar.UpdateIcons` sizes/labels elements from item grid positions.
   This explains ammo showing as 4 slots when the item's grid x is `3`,
   labels falling back to `1..8`, and multiple vertical ammo/food items
   collapsing so only one appears reliably.
4. **`ApplyBindingLabels` is too narrow.** It only relabels tool slots
   that have items, and only after `ToolHotBar.IsOurBar(__instance)`.
   Empty tool slots therefore keep cloned vanilla labels, and if the bar
   is not rebuilding elements as expected, labels remain `1..8`.
5. **The slot-coordinate migration is not idempotent.** Repeated row-count
   changes cause collection/replacement passes to move items through
   stale coordinates and restricted slots. The migration must key off
   stable slot IDs / slot types, not only transient `x,y` positions.
6. **Ammo/food visual bars mirror equipment-panel slots, but the grid
   layout changes are also moving those slots.** Until ammo/food have
   stable slot IDs and deterministic ordering independent of current row
   offsets, their visual hotbars will remain fragile.

### ExtraSlots-derived fixes applied after this test pass

- `Managers/HotBars/HotBarController.cs`
  - Now explicitly calls `UpdateIcons(Player.m_localPlayer)` for each VA
    cloned bar every HUD update, matching ExtraSlots' `QuickBars` pattern.
  - Now applies labels after each cloned bar update instead of only
    touching tool bars.
  - Now restores temporary grid-position rewrites after each cloned bar
    update.
- `Managers/HotBars/ToolHotBar.cs`
  - Temporarily normalizes visible item `m_gridPos` to dense hotbar
    positions while vanilla `HotkeyBar` renders, then restores originals.
- `Managers/HotBars/AmmoHotBar.cs`
  - Temporarily normalizes vertical equipment-panel ammo items to dense
    hotbar positions while rendering, then restores originals.
- `Managers/HotBars/FoodHotBar.cs`
  - Temporarily normalizes vertical equipment-panel food items to dense
    hotbar positions while rendering, then restores originals.

These fixes follow the same principle as ExtraSlots' `FoodSlotsHotBar`
`AdaptGridPos` / `RestoreGridPos`, but VAInventory applies it to all
three visual bars because the VA slots can be sparse or vertical.

### 2026-05-06 follow-up fixes from screenshot review

- `Managers/InventoryGrid_ExtendGridForExtraSlots.cs`
  - The player `InventoryGrid` now allocates empty tool-hotbar row
    elements even when no tool item is already placed there. Previously
    the extension was driven only by existing item `m_gridPos.y`, so an
    empty toolbar row had no visible drop targets.
- `Managers/EquipmentPanel.cs`
  - The open-inventory player grid now keeps tool-hotbar row cells
    visible instead of moving every element beyond `InventorySizePlayer`
    off-screen. Empty tool cells render with the quick-slot background
    and shortcut label so users can drag tools into them.
  - Equipment-panel slot positioning now uses
    `Slots.InventoryHeightPlayer + Slots.EquipmentPanelRowOffset` instead
    of stale hard-coded `y=8`. This keeps panel cells aligned with the
    background when changing main inventory rows.

Remaining item-migration safety work is still separate from these UI
alignment fixes.

### 2026-05-06 hotkey follow-up fixes

- `Managers/ToolBar.cs`
  - Removed the startup warning from `AccessTools.Method(typeof(InventoryGui), "BeginDragItem")`
    by using normal reflection for that optional legacy method. On game
    versions where the method is absent, the existing fallback drag-state
    path still runs without HarmonyX logging a warning.
- `Managers/HotBars/HotkeyInput.cs`
  - Added shared modifier-aware key parsing and vanilla-hotbar suppression
    for VA modifier-number hotkeys.
  - Suppression now covers tool `Alt+N`, food `Ctrl+N`, and ammo
    `Ctrl+Alt+N`, but only for slot indices that actually exist.
- `Managers/HotBars/HotBarController.cs`
  - Food/ammo hotkey activation now uses the shared parser.
  - Ammo hotkey activation temporarily suppresses the unequip-return
    relocation path while `Player.UseItem` swaps arrow types. This keeps
    the previously equipped arrow stack in its ammo slot instead of moving
    it back into the main grid.
- `Managers/HotbarManager.cs`
  - The runtime `ZInput.TryGetButtonState` prefix now delegates to
    `HotkeyInput.ShouldSuppressVanillaHotbarIndex`, replacing the old
    tool-only `Alt+N` suppression.

### Required fixes before Phase F

1. **Fix cloned hotbar rendering before adding more input.** Phase F
   hotkeys depend on the visual slots being stable and correctly indexed.
2. **Decide whether cloned `HotkeyBar` can support empty visible slots.**
   Current `GetItems` emits only non-null items, which matches ExtraSlots'
   purely visual quickbar pattern but does not satisfy VAInventory's
   `ShowWhenEmpty` expectation or the user's need to see all tool slots.
   If empty slots must be visible, we need either safe placeholders or a
   controlled element-rebuild step that creates N elements even when bound
   items are null.
3. **Relabel every visible tool element, not only filled elements.** The
   binding text must be written after each `UpdateIcons` pass for all
   visible tool slots so labels cannot fall back to vanilla `1..8`.
4. **Instrument `HotkeyBar.UpdateIcons` for each VA bar.** Add temporary
   logs for bar name, bound item count, `m_elements.Count`, configured
   slot count, and each item's `m_gridPos` before/after update.
5. **Make row migration deterministic and safe.** Before allowing config
   row sliders to mutate `InventoryHeightPlayer`, capture current items
   by `Slots.Slot.ID` / slot type, clear only VA-managed slots, rebuild
   slots, then restore by type with validation. Do not collect from or
   place into restricted coordinates by raw `x,y` alone.
6. **Add a migration dry-run/safety pass.** If no valid destination exists,
   prefer returning items to regular inventory or aborting the row change
   over dropping items. Drops should be last resort and clearly logged.
7. **Re-test without backpacks equipped, then with one backpack equipped.**
   Logs show backpack equip/unequip rebuilds the slot array and changes
   `GridSlot SUMMARY` from `28` to `52`; isolate whether backpack blocks
   are worsening row migration.

### Acceptance for unblocking Phase F

- Tool bar is visible with `ShowWhenEmpty=true` and shows the configured
  number of tool slots even when empty.
- Tool bar labels show configured shortcut text, not vanilla `1..8`.
- Ammo bar shows exactly 3 visual slots and each slot maps to the correct
  ammo slot in order.
- Food bar shows exactly 3 visual slots and each slot maps to the correct
  food slot in order.
- Placing multiple food/ammo items shows all placed items, not only the
  most recently placed item.
- Changing extra rows / equipment-panel rows does not move items into
  invalid slot types, duplicate visual entries, or drop items.
- Equipment panel alignment remains correct across the min/max row-slider
  range.

## Phase F — Hotkey input (PreventSimilarHotkeys port)

Lift `ExtraSlots/HotBars/PreventSimilarHotkeys.cs` and the per-bar
`UpdateItemUse` pattern into VAInventory. Goal: pressing `1..8` uses
vanilla hotbar items; pressing `Alt+1..N` uses tool slot items;
configurable keybinds for ammo/food slots trigger food consumption /
ammo selection. No double-fire.

### Implemented pieces

1. **Per-slot keybind labels for ammo/food**.
   `Managers/Slots.cs:InitializeSlots` now creates ammo and food slots
   with the keybind-aware `Slot` constructor so `Slot.IsHotkeySlot` and
   `Slot.GetShortcutText()` work for those slots.
   `Managers/ConfigManager.cs` now exposes modifier-aware string entries:
   - `FoodHotbar.KeybindEx1..3` default to `Ctrl+1..3`.
   - `AmmoHotbar.KeybindEx1..3` default to `Ctrl+Alt+1..3`.
   The older `KeyboardShortcut` arrays were not added for food/ammo; the
   string form is the canonical path because it already supports multiple
   modifiers consistently with the tool hotbar.

2. **`Slot.IsHotkeySlot` already true when `_getShortcut != null`** —
   no change needed, just feed the shortcut at construction.

3. **`HotkeyInput.cs`** centralizes:
   - `TryParseKeybindString(string, out KeyCode, out wantAlt, out wantCtrl, out wantShift)`.
   - `IsShortcutDown(string)` for exact modifier matching.
   - `ShouldSuppressVanillaHotbarIndex(int)` for vanilla-hotbar conflict
     suppression. It only suppresses when the matching VA modifier-number
     shortcut is currently firing, so unrelated custom keys are left alone.

4. **Patch `ZInput.TryGetButtonState(string)` Prefix** —
   `Managers/HotbarManager.cs:ZInput_TryGetButtonState_Prefix` now parses
   `Hotbar1..8` requests and delegates to `HotkeyInput` for tool, food,
   and ammo suppression.

5. **Per-bar `UpdateItemUse`** — `HotBarController.UpdateItemUse` now
   walks tool, food, and ammo slots through a shared helper and calls
   `Player.UseItem(player.GetInventory(), slot.Item, false)` for the
   matching item.

### Acceptance

- Press `1..8`: vanilla hotbar acts as before.
- Press `Alt+1..8`: tool slot 1..8 activates (toggle equip / consume / select).
- Press the configured food/ammo keybinds: the matching item activates.
- No vanilla hotbar item double-fires when pressing modifier+digit.
- ZInput patches play nicely with mods that rebind `Hotbar1..8` (leave
  a comment in `HotkeyInput.cs` referencing this risk).

## Phase G — Cleanup / deletions

Completed after the cloned hotbar path was verified in game:

1. Deleted `Managers/ToolBar.cs`.
2. Deleted `Managers/ToolBarDropHandler.cs`; drops now go through grid
   cells / `InventoryGrid.DropItem`.
3. Deleted `Managers/PlayerPatches.cs`, which only existed to bridge
   vanilla hotbar input into the legacy toolbar UI.
4. Reduced `Managers/HotbarManager.cs` to the runtime-safe
   `ZInput.TryGetButtonState` suppression patch generalized by Phase F.
5. Deleted `configToolHotbarUseLegacy` and the legacy
   `configToolHotbarKeybinds` array. `configToolHotbarKeybindsEx` is now
   canonical.
6. Removed deleted files from `VAInventory.csproj`.
7. Kept `Managers/UniversalDragHandler.cs`; backpack panels still use it.

### Acceptance

- Build passes after deletions.
- No compiled code path references the deleted symbols.
- The mod loads, all three bars render, drag-to-reposition works,
  hotkey input fires correctly, items drop into slots via grid cells.

## What's already in place (Phases A–E summary)

### Phase A — Slot Y-coord layout

- `Managers/Slots.cs:55-72` — relative offset constants
  (`ToolHotbarRowOffset = 0`, `AmmoHotbarRowOffset = 1`,
  `FoodHotbarRowOffset = 2`, `EquipmentPanelRowOffset = 3`,
  `MiscBlockRowOffset = 7`).
- `Managers/Slots.cs:626` — tool hotbar slot grid pos changed from
  `InventoryHeightPlayer + 10` to `InventoryHeightPlayer`.
- Equipment panel start Y now derived: `InventoryHeightPlayer + EquipmentPanelRowOffset`.

### Phase B — Item validation sweep

- `Managers/ItemSlotValidation.cs` already had the ExtraSlots-style
  sweep ported. Existing logic at lines 128–150 migrates items at
  stale grid positions (e.g. old `y=14` from the +10 era) to free
  slots. No new code needed.

### Phase C — Tool hotbar collapsed to ExtraSlots-style

- `Managers/HotBars/ToolHotBar.cs:55-78` — `GetItems` returns only
  filled slots (no placeholder hack). `IsSlotEmpty` and
  `SamplePlaceholder` deleted.
- `Managers/HotBars/HotBarController.cs:ApplyBindingLabels` — maps
  element-index → slot-index correctly when there are gaps in slot
  fill (e.g. element[0]'s label corresponds to whichever slot owns
  the first non-null item, not necessarily slot[0]).

### Phase D — Ammo + Food hotbars

- `Managers/HotBars/AmmoHotBar.cs` — clones HotKeyBar, binds to
  `Slots.GetAmmoSlots()`. Independent enable/position/scale config.
- `Managers/HotBars/FoodHotBar.cs` — same for food.
- `Managers/HotBars/HotBarController.cs` — multi-bar dispatch in
  - `Hud_Update_BarController` (calls `Refresh()` on all three each frame)
  - `Hud_OnDestroy_ClearBar` (clears all three)
  - `Hud_Awake_MarkDirty` (marks all three dirty)
  - `HotkeyBar_UpdateIcons_BindOurItems.Prefix` — recognizes any of
    the three bar names
  - `Inventory_GetBoundItems_OurItems.Postfix` — dispatches by
    `barName` to the matching `*HotBar.GetItems`.
- `Managers/ConfigManager.cs` — new sections `[AmmoHotbar]` and
  `[FoodHotbar]` with `ShowHotbar`, `Position`, `Scale`. SettingChanged
  handlers call `MarkDirty()` on the corresponding bar.
- `VAInventory.csproj` — added `<Compile Include>` entries for the
  two new files.

### Phase E — Drag-to-reposition handle

- `Managers/HotBars/HotBarDragHandle.cs` (new) — `IBeginDrag/IDrag/
  IEndDrag` MonoBehaviour. `Init(RectTransform, ConfigEntry<Vector2>)`
  binds it to a bar and its position config. Critical: gates on
  `InventoryGui.m_dragItem == null` via a reflected field — when the
  user is moving an inventory item the handle yields, so slot drops
  via the grid still work.
- `Managers/HotBars/HotBarController.cs:InstantiateHotKeyBar` adds:
  - A `Canvas` with `overrideSorting=true, sortingOrder=50` (above
    HUD's default 0, below the open `InventoryGui` canvas at ~100).
  - A `GraphicRaycaster` so the override-sorted canvas processes
    pointer events.
  - A transparent `Image` on the bar root (alpha ~0.001, raycastTarget
    true) so PointerDown bubbles from the empty space between
    elements.
- `Managers/HotBars/{Tool,Ammo,Food}HotBar.cs:CreateBar` — attaches
  `HotBarDragHandle` after clearing the cloned bar's elements:
  ```csharp
  var drag = hotBarRect.gameObject.GetComponent<HotBarDragHandle>()
           ?? hotBarRect.gameObject.AddComponent<HotBarDragHandle>();
  drag.Init(hotBarRect, ConfigManager.Instance?.config*HotbarPosition);
  ```

### Default flipped to new path

- `Managers/ConfigManager.cs:160` — `configToolHotbarUseLegacy`
  default changed from `true` to `false`. New installs use the
  ExtraSlots-style path; existing installs with the config saved as
  `true` still fall back to legacy until they reset the config or we
  delete the legacy in Phase G.

## Other ExtraSlots ideas to lift (optional, low priority)

These came out of the audit (see git history of this doc — they were
in the original plan) but are independent of the F/G work and can be
done in any order:

- **Item state persistence in `customData`** — slot ID + player ID
  stamped on the item so on re-pickup it returns to the same slot.
  Already partially in `Slots.TryGetSavedPlayerSlot` / `customData`
  keys `ExtraSlotsEquippedSlot`, `ExtraSlotsEquippedBy`. Rename keys
  to `VAEquippedSlot`/`VAEquippedBy` if we want to drop the
  ExtraSlots brand from save data.
- **`Inventory.SlotsUsedPercentage` correction** — without it, the
  bottom-left "inventory full" gauge counts our extra slots. ExtraSlots'
  `Inventory_SlotsUsedPercentage_ExcludeRedundantSlots` patch fixes this.
- **`Inventory.GetEmptySlots` correction** — same story; affects
  auto-pickup. ExtraSlots'
  `Inventory_GetEmptySlots_CheckRegularInventoryAndQuickSlots`.
- **`Player.AutoPickup` `preventAutoPickup` flag** — config to block
  auto-pickup into hotbar/quick slots. ExtraSlots ships it.
- **`RectTransformExtensions.SetAnchor`** — small utility mapping a
  9-point anchor enum to `anchorMin/anchorMax`. Lets us expose
  `configAnchor = TopLeft/Top/.../BottomRight` in BepInEx config UI.
  Cleaner UX than raw float anchor pairs.
- **Inventory weight factor per slot type** —
  `itemWeightFactor*` configs (Quick/Ammo/Food/Equipment) so each
  slot type can apply a multiplier to carried weight.

## Testing checkpoints (first in-game pass failed)

First pass performed 2026-05-06. Keep these checkpoints, but current
results are failures until the blockers above are fixed.

1. ❌ **Tool hotbar** appears at the screen position from
   `configToolHotbarPosition` (default `(115, 200)`).
   - Observed: clone is created and config positions update, but the bar
     is usually not visible; only occasional glimpses during slider moves.
2. ⚠️ **Open inventory** — see one new row of cells at
   `y=InventoryHeightPlayer` (= y=4 with default `configExtraRows=0`,
   y=7 with default `configExtraRows=3`). Drop a tool item there;
   it should land in tool hotbar slot N.
   - Observed: tool slots are created at rows such as `gridPos=(0,7)`,
     `(0,6)`, `(0,5)`, `(0,4)` as height changes, but row changes cause
     item migration instability.
3. ❌ **Ammo hotbar** appears at default position
   `(115, 270)`. Mirrors the equipment panel ammo slots.
   - Observed: renders as 4 slots and only the last placed ammo item is
     represented reliably.
4. ❌ **Food hotbar** appears at default `(115, 340)`. Mirrors food slots.
   - Observed: renders 3 slots, but only the last placed food item is
     represented reliably.
5. ⚠️ **Toggle `ShowAmmoHotbar` / `ShowFoodHotbar`** off/on in the
   config UI — bar should appear/disappear.
   - Observed: create logs fire after config reloads, but visual output
     remains wrong.
6. ⚠️ **Close inventory, mouse-grab any bar** and drag it. Release;
   reload world; position persists.
   - Observed: position config updates and logs, but visibility/alignment
     still fails.
7. ⚠️ **Open inventory, try to drag a bar** — should not move (drag
   handle yields when `m_dragItem` is null check… wait, actually it
   yields when `m_dragItem != null`, so dragging the bar with no
   inventory item on the cursor still works while the inventory is
   open. That's fine. Item drops on slot cells continue to work
   because the drag handle releases the pointer for them.)
   - Observed: item movement through the grid is unsafe during row
     slider changes; fix migration before treating drag behavior as
     accepted.

## Risk register

- **Data-loss risk from row migration (new, 2026-05-06)**: current test
  logs show `MIGRATE: No valid slot found... dropping` for food, ammo,
  bags, armor, and tools after changing row counts. Treat row sliders as
  unsafe until migration is made idempotent and type-aware.
- **Cloned HotkeyBar assumptions (new, 2026-05-06)**: vanilla `HotkeyBar`
  may not be suitable for VAInventory's "show empty configured slots"
  requirement without extra element management. ExtraSlots emits only
  real items for a visual quickbar; VAInventory needs stable empty slot
  visuals and labels.
- **Config slider churn (new, 2026-05-06)**: moving config sliders causes
  repeated config reloads, repeated `Slots.InitializeSlots` passes, and
  repeated migration while inventory contents are in flux. Debounce or
  guard row-changing config handlers before further UI work.
- **Save migration**: Phase A breaks any character that previously
  had items in tool slots at y=14/y=17. User has explicitly accepted
  this — no test characters had working tool slots.
- **Equipment panel coexistence**: equipment slots at
  `y >= InventoryHeightPlayer + EquipmentPanelRowOffset` are in the
  same grid as tool hotbar at `y = InventoryHeightPlayer`. They don't
  collide as long as `EquipmentPanelRowOffset >= 1`. Currently it's
  `3`, so there's slack for future slot rows.
- **Backpack panel y**: dynamic Y starting from `bpCurrentY = miscBlockStartY + miscRows`.
  After Phase A this becomes `EquipmentPanelStartY + EquipmentPanelRowCount + miscRows`,
  with EquipmentPanelStartY relative to `InventoryHeightPlayer`. Bag
  slots stack as before; verify when multiple bags equipped.
- **Hotkey conflicts with other mods (Phase F)**: `PreventSimilarHotkeys`
  pattern assumes vanilla button names like `Hotbar1..8`. Mods that
  rebind those need the same patch ordering. Leave a comment in
  `HotkeyInput.cs` referencing this.

## Files touched in Phases A–E

**New:**

- `Managers/HotBars/AmmoHotBar.cs`
- `Managers/HotBars/FoodHotBar.cs`
- `Managers/HotBars/HotBarDragHandle.cs`

**Modified:**

- `Managers/Slots.cs` — Y-coord constants + tool hotbar position.
- `Managers/HotBars/HotBarController.cs` — multi-bar dispatch +
  Canvas/Raycaster/Image setup in `InstantiateHotKeyBar` + drag
  handle wiring + binding label index mapping.
- `Managers/HotBars/ToolHotBar.cs` — collapsed `GetItems` +
  attached drag handle.
- `Managers/ConfigManager.cs` — new ammo/food hotbar config entries +
  flipped `UseLegacyRenderer` default to false.
- `VAInventory.csproj` — added new files to `<Compile Include>` block.

**Not yet modified (planned for F/G):**

- `Managers/HotbarManager.cs` — extend `ZInput.TryGetButtonState` patch
  to cover ammo/food slots; eventually delete the legacy half.
- `Managers/ToolBar.cs` — delete (Phase G).
- `Managers/ToolBarDropHandler.cs` — delete (Phase G).

## How to resume from this doc

1. Read this file end to end.
2. Verify the current build state: `dotnet build VAInventory.csproj -c Debug` from
   `<valheimRip2>/VAInventory/VAInventory/`.
   Should be `0 Error(s)` with ~12 pre-existing warnings.
3. Treat the 2026-05-06 test pass as the current source of truth:
   Phases A–E are code-complete but not runtime-accepted. Fix the
   blockers in "2026-05-06 in-game test findings" before starting
   Phase F.
4. Re-run the testing checkpoints and only continue once all A–E
   acceptance criteria pass in-game.
5. Implement Phase F as described in the "Phase F" section.
   Recommended sequence:
   - Add ammo/food keybind config entries to `ConfigManager.cs`.
   - Update `Slots.InitializeSlots` to construct ammo/food slots with
     keybinds.
   - Lift `TryParseKeybindString` and the per-frame caching pattern
     into a new `Managers/HotBars/HotkeyInput.cs`.
   - Generalize `HotbarManager.ZInput_TryGetButtonState_Prefix` to
     check ammo/food slots too.
   - Generalize `HotBarController.UpdateItemUse` to walk all three
     bars.
6. Implement Phase G after verifying F works in-game. Order:
   delete files, remove `<Compile Include>` entries, fix build,
   verify in-game.
7. Update this doc as Phase F/G land — flip the ⏸️ to ✅ in the
   Phase status table.
