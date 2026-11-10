# ExtraSlots-aligned hotbar refactor — gameplan

## Why this plan exists

The current tool hotbar implementation has been fighting raycast priority,
canvas sort orders, and a +10 Y-coordinate gap that puts tool slots
off-screen in the open-inventory grid view. shudnal-ExtraSlots solves the
exact same problem cleanly:

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
component on the cloned bar root).

## Goals

1. **Three hotbars instead of one**: Tool, Ammo, Food — each a clone of
   vanilla's `HotKeyBar`, each bound to its own `Slots.Slot[]` set, each
   independently positionable.
2. **Slots placed contiguously** with the rest of the inventory grid
   (no +10 gap), so they render visibly when the user opens the
   inventory and drag-drop into them goes through vanilla.
3. **Drag-to-reposition the visual bars** when inventory is closed
   (Esc-mode). Position persists to config.
4. **Hotkey input** for each bar via the ExtraSlots
   `PreventSimilarHotkeys` pattern (vanilla `1..8` still work for the
   normal hotbar; our `Alt+1..N` etc. route to our bars without
   conflict).
5. **Delete the legacy paths** (`ToolBar.cs` ~2222 LOC, the legacy
   portions of `HotbarManager.cs` ~600 LOC, the `configToolHotbarUseLegacy`
   flag, the `UniversalDragHandler` instances no longer needed) once
   the new path is proven on all three bars.

## Final slot Y-coord layout

Vanilla inventory: `y = 0..3` (`InventoryHeightPlayer = 4 + ExtraRowsPlayer`,
so this rises if the user adds extra player rows).

After the refactor, all extra slots are **contiguous starting at y =
InventoryHeightPlayer** (matches ExtraSlots exactly):

```
y = InventoryHeightPlayer + 0 : Tool hotbar slots   (8 cells, one row)
y = InventoryHeightPlayer + 1 : Ammo hotbar slots   (3 cells; rest empty)
y = InventoryHeightPlayer + 2 : Food hotbar slots   (3 cells; rest empty)
y = InventoryHeightPlayer + 3 : Equipment row 0     (helmet, shoulder, ...)
y = InventoryHeightPlayer + 4 : Equipment row 1     (chest, utility, ...)
y = InventoryHeightPlayer + 5 : Equipment row 2     (legs, trinket, ...)
y = InventoryHeightPlayer + 6 : Equipment row 3     (coin/backpack, ...)
y = InventoryHeightPlayer + 7+ : Misc block + backpack blocks
```

Constants live on `Slots`:

```csharp
public const int ToolHotbarRowOffset = 0;
public const int AmmoHotbarRowOffset = 1;
public const int FoodHotbarRowOffset = 2;
public const int EquipmentPanelRowOffset = 3;
public const int MiscBlockRowOffset = 7;
```

`Slots.GridPosition` for any extra slot becomes
`new Vector2i(col, InventoryHeightPlayer + RowOffset + subRow)`.

## Phases

### Phase A — Foundation (Slots.cs only)

**No UI changes yet.** Just rearrange slot Y coordinates and verify the
existing inventory-grid extension still renders everything.

1. Replace the hardcoded `+10` and the per-block `*StartY` constants
   with the `*RowOffset` constants above, all expressed relative to
   `InventoryHeightPlayer`.
2. Move tool hotbar slots into the main `slots[]` array (currently in a
   separate `toolHotbarSlots` list). Keep an `IsToolHotbarSlot` accessor
   for code that filters by type. `toolHotbarSlots` stays as a derived
   `IEnumerable<Slot>` helper so callers don't break.
3. Add `Slots.GetToolHotbarSlots()`, `GetAmmoHotbarSlots()`,
   `GetFoodHotbarSlots()`. The food/ammo helpers initially return the
   existing food/ammo equipment-panel slots.
4. Verify with a save reload: open inventory, see all extra rows
   visible immediately below player rows.

**Acceptance:** Inventory grid renders rows for tool/ammo/food/equipment/
misc/backpack with no gaps, no off-screen rows. Existing items snap to
their new positions on first load via `ItemsSlotsValidation` (Phase B).

### Phase B — Item validation sweep (lifted from ExtraSlots)

Port `ItemsSlotsValidation.ValidateItems` so on inventory load, items
sitting at old Y coords (e.g. y=14 from the +10 gap era) are
automatically moved to the new equivalent slot or to the first free
inventory cell. This is the migration path the user accepted ("don't
care about invalidating existing players' tools").

1. New file: `Managers/ItemsSlotsValidation.cs`. Mirrors the ExtraSlots
   sweep — `Inventory.m_onChanged += ValidateItems`. For each item
   whose `m_gridPos` doesn't fit a known slot or sits in a removed
   slot, try `Slots.TryFindFreeSlotForItem` then fall back to vanilla
   `FindEmptySlot`.
2. Per ExtraSlots, also handle: equipped-item gridPos preservation,
   stack overflow into adjacent cells, "duplicate at same gridPos"
   (keep one, reroute the other).
3. Also lift their `Inventory.AddItem` Prefix that reroutes incoming
   pickups into the appropriate hotbar/equipment slot when applicable.

**Acceptance:** Old-save items auto-migrate. New `Inventory.AddItem`
calls land in the correct slot type when one is free.

### Phase C — Replace ToolHotBar with the contiguous-slot version

The current `ToolHotBar.cs` already uses the clone pattern but has
visibility hacks for the placeholder-icon empty-slot trick. With slots
now part of the grid (Phase A) the bar's `GetItems` collapses to:

```csharp
public static void GetItems(List<ItemDrop.ItemData> bound)
    => bound.AddRange(Slots.GetToolHotbarSlots()
                          .Select(s => s.Item)
                          .Where(i => i != null));
```

…matching ExtraSlots exactly. The empty-slot placeholder hack and
`IsSlotEmpty` API can be deleted: vanilla's `HotkeyBar` simply renders
N elements where N = active items.

If the user wants visible empty frames in the bar (the
`configToolHotbarShowWhenEmpty` config), keep a single static wrapper:
when ShowWhenEmpty is true, pad with a sampled placeholder
(current code) and hide alpha for empty cells. Otherwise, no padding.

**Acceptance:** Tool hotbar visible at bottom, mirrors slot contents,
no fallthrough drops because no click handlers on the bar at all.

### Phase D — AmmoHotBar + FoodHotBar (mirror of ToolHotBar)

Two new files: `Managers/HotBars/AmmoHotBar.cs`, `FoodHotBar.cs`.

Each is the same shape as `ToolHotBar.cs`:
- `barName` const (`VA_AmmoHotBar`, `VA_FoodHotBar`).
- `MarkDirty/Refresh/CreateBar/ClearBar/IsEnabled/IsOurBar`.
- `GetItems(List)` — pulls from `Slots.GetAmmoHotbarSlots()` /
  `GetFoodHotbarSlots()`.
- Position/Scale/Anchor read from new config entries
  (`configAmmoHotbarPosition`, `configFoodHotbarPosition`, plus scale
  and anchor knobs to match ExtraSlots).
- `IsEnabled()` reads `configShowAmmoHotbar` /
  `configShowFoodHotbar`.

`HotBarController.HotkeyBar_UpdateIcons_BindOurItems` Postfix and the
`Inventory.GetBoundItems` Postfix get a 3-way switch on
`__instance.name` → call the appropriate `*HotBar.GetItems`.

`HotBarController` also gains an enumerable of all our bars so that
`Hud.Update` postfix calls `Refresh()` on each.

**Acceptance:** All three bars render. Toggling the food/ammo enable
config shows/hides them. Position config independently moves each.

### Phase E — Drag-to-reposition the visual bars

This is our explicit deviation from ExtraSlots' "config offsets only"
design. Implementation lives in a single component
`Managers/HotBars/HotBarDragHandle.cs` attached to each cloned bar
root in `*HotBar.CreateBar`.

Constraints (these are what kept it simple in the legacy
`UniversalDragHandler`):

1. Only accepts drags **when no item is on the cursor**
   (`InventoryGui.m_dragItem == null`). This means while inventory is
   open and the user is moving items, the bar is *not* a drag target —
   so we don't fight item drops.
2. Persists the new `anchoredPosition` to the bar's config entry on
   `OnEndDrag`. Config save triggers no rebuild (we just write the
   position).
3. Uses a **separate Canvas with `overrideSorting=true,
   sortingOrder=200`** scoped to *just the drag-handle's invisible
   background image*, so the drag-handle outranks normal HUD pointer
   targets but doesn't block anything visually.

Because the drag handle only fires when the cursor is empty, and the
inventory grid only matters when the cursor holds an item, the two
modes don't conflict.

The component is small (~80 LOC) — much simpler than legacy
`UniversalDragHandler` because we don't have to compute slot drop
targets manually, only move the bar's anchored position.

**Acceptance:** With inventory closed, mouse-grab the bar and drag it
anywhere; release; it stays. Reload world; position persists. With
inventory open, dragging the bar does nothing (cursor item interaction
takes precedence).

### Phase F — Hotkey input (PreventSimilarHotkeys port)

Lift `ExtraSlots/HotBars/PreventSimilarHotkeys.cs` into our
`HotbarManager` (or a new `Managers/HotBars/HotkeyInput.cs`).

1. `Slot.GetShortcut()` reads either the legacy KeyboardShortcut or
   the new `*KeybindsEx[i]` string and returns a `KeyboardShortcut`.
2. `IsAnyVAHotkeyDown()` walks the union of tool/ammo/food slot
   shortcuts and returns true if the corresponding key is pressed
   *with* its modifiers held (and not other modifiers).
3. Patch `ZInput.TryGetButtonState(string)` Prefix: if the requested
   button is a vanilla hotbar key (`Hotbar1..8`) and `IsAnyVAHotkeyDown`
   matches the same digit AND a modifier is held, suppress vanilla.
4. Per-frame caching token (`Time.frameCount` based) avoids repeated
   work — copied from ExtraSlots verbatim.
5. Per-bar `UpdateItemUse()` walks the bar's slots, checks shortcut,
   calls `Player.UseItem` for the matching item.

**Acceptance:** Pressing `1..8` uses vanilla hotbar items. Pressing
`Alt+1..8` uses tool slot items. Configurable Ctrl+Shift+F etc. for
food/ammo. No double-fire, no leaked vanilla activation.

### Phase G — Cleanup / deletions

Once Phases A–F are verified end-to-end with the new path enabled by
default:

1. Delete `Managers/ToolBar.cs` (~2222 LOC).
2. Delete `Managers/ToolBarDropHandler.cs` (no longer used; drops go
   through `InventoryGrid.DropItem`).
3. Delete the legacy half of `Managers/HotbarManager.cs` — the
   custom-toolbar-creation, `RetryCreateLoop`, `HandleHotkeyInput`,
   `HotKeyBar_Update_Prefix`. Keep only the renamed
   `HotkeyInput`/`PreventSimilarHotkeys` parts.
4. Delete `configToolHotbarUseLegacy` and the legacy `configToolHotbarKeybinds`
   array (keep the modifier-aware string array as canonical).
5. Remove the deleted entries from `VAInventory.csproj` `<Compile Include>`.
6. Drop `Managers/UniversalDragHandler.cs` if no remaining components
   use it (the audit suggests it was used by backpack panels — if so,
   keep it; otherwise delete).

## Other ExtraSlots ideas to lift while we're refactoring

These were in the audit but aren't on the critical path. Each is a
single well-scoped lift; do whichever feel valuable:

- **Item state persistence in `customData`** — keys
  `ExtraSlotsEquippedSlot` / `ExtraSlotsEquippedBy` rename to
  `VAEquippedSlot` / `VAEquippedBy`. Enables "remember what slot a
  re-equipped item came from" without separate save state.
- **Inventory weight factor per slot type** — config knob
  `itemWeightFactor*` so quick/ammo/food/equipment slots can apply a
  multiplier to carried weight. ExtraSlots ships this; trivial to lift.
- **`Inventory.SlotsUsedPercentage` correction** — without it, the
  bottom-left "inventory full" gauge counts our extra slots toward the
  full bar and wedges at 100%. ExtraSlots' patch fixes this.
- **`Inventory.GetEmptySlots` correction** — same story; without the
  patch, auto-pickup misbehaves because the system thinks the
  inventory is fuller than the player perceives.
- **`Player.AutoPickup` `preventAutoPickup` flag** — config option to
  block auto-pickup into the hotbar/quick slots. ExtraSlots ships it.
- **`RectTransformExtensions.SetAnchor`** — small utility that maps a
  9-point anchor enum to `anchorMin/anchorMax`. Lets us expose
  `configAnchor = TopLeft/Top/.../BottomRight` in BepInEx config like
  ExtraSlots does. Cleaner UX than raw float anchor pairs.

## Testing checkpoints (manual smoke tests)

After each phase, before moving to the next:

1. **Phase A**: open inventory, see all extra rows visible. Existing
   tools may be lost — that's accepted.
2. **Phase B**: drop a tool item; it auto-routes into the tool slot
   row. Pick up; auto-pickup lands in inventory rather than slot
   (autopickup-prevention is later).
3. **Phase C**: open/close inventory; tool hotbar visible at bottom,
   reflects slot contents in real time. Drag tool from inventory grid
   into a slot cell — it sticks.
4. **Phase D**: same for ammo + food. Toggle configShowAmmoHotbar
   off/on, bar appears/disappears.
5. **Phase E**: with inventory closed, drag each bar to a new spot;
   reload, it persists.
6. **Phase F**: press `1`/`2`/`3` — vanilla hotbar. Press `Alt+1`/`+2`
   — tool slot 1/2 use. No double-fire.
7. **Phase G**: build still passes after deletions; legacy file
   removal doesn't break anything.

## Files touched

**New:**

- `Managers/ItemsSlotsValidation.cs`
- `Managers/HotBars/AmmoHotBar.cs`
- `Managers/HotBars/FoodHotBar.cs`
- `Managers/HotBars/HotBarDragHandle.cs`
- `Managers/HotBars/HotkeyInput.cs` (or rename of HotbarManager content)
- `Utilities/RectTransformExtensions.cs` (optional; lifts SetAnchor)

**Modified:**

- `Managers/Slots.cs` — Y-coord constants, contiguous layout,
  `GetToolHotbarSlots`/`GetAmmoHotbarSlots`/`GetFoodHotbarSlots`.
- `Managers/HotBars/HotBarController.cs` — multi-bar binding, drag
  handle attachment.
- `Managers/HotBars/ToolHotBar.cs` — collapse to ExtraSlots-style.
- `Managers/ConfigManager.cs` — new ammo/food hotbar config entries,
  remove legacy.
- `Managers/InventoryInteraction.cs` — adopt ExtraSlots'
  AddItem/FindEmptySlot patches.
- `VAInventory.csproj` — add new files, remove deleted files.

**Deleted (Phase G):**

- `Managers/ToolBar.cs`
- `Managers/ToolBarDropHandler.cs`
- legacy half of `Managers/HotbarManager.cs`
- `configToolHotbarUseLegacy`, legacy keybind array

## Risk register

- **Save migration**: Phase A breaks any character that previously
  had items in tool slots at y=14/y=17. User has explicitly accepted
  this — no test characters have working tool slots today.
- **Equipment panel coexistence**: equipment slots already live at
  y >= InventoryHeightPlayer in the same grid, so Phase A overlaps
  the row offsets if not done carefully. Verification: walk
  `Slots.slots` after init and assert no two slots share a
  `GridPosition`.
- **Backpack panel y**: backpack slots use a dynamically-computed Y
  starting from `bpCurrentY`. After Phase A this becomes
  `InventoryHeightPlayer + MiscBlockRowOffset + miscRows`. Re-test
  that all bag positions stack without overlap when multiple bags
  are equipped.
- **Hotkey conflicts with other mods**: the `PreventSimilarHotkeys`
  approach assumes vanilla button names like `Hotbar1..8`. Mods that
  rebind those will need the same patch ordering — leave a comment
  in `HotkeyInput.cs` referencing this.
