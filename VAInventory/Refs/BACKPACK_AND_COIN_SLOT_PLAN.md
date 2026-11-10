# VAInventory — Backpack Slot, Coin Purse, & Dynamic Slot Expansion

**Goal.** Add two new equipment slots to the panel — a Backpack slot
and a Coin Purse slot — and let the panel grow dynamically with extra
storage rows when a backpack is equipped. The Coin Purse only accepts
the `Coins` prefab and stacks infinitely, so the player never has to
manage coin storage again.

Companion plans:
- `VABackpacks/Refs/BACKPACK_INVENTORY_REFACTOR_PLAN.md`
- `FiresNPCs/Refs/INVENTORY_BACKPACK_COMPAT_PLAN.md`

---

## 1. Current state (snapshot)

`Managers/Slots.cs` is well-architected:

- `Slot` class with id/validator/IsActive/grid-position.
- `Slots.slots[]` array, dynamically resized in `InitializeSlots()`.
- 3-row classic equipment block (Helmet+Shoulder, Chest+Utility,
  Legs+Trinket), optional Food/Ammo columns.
- Configurable Misc rows below for arbitrary items.
- Tool hotbar slots tracked in a separate `toolHotbarSlots` list.
- `EquipmentPanel.UpdatePanel()` rebuilds the visual UI from `slots[]`.
- `InventoryGrid_ExtendGridForExtraSlots.cs` extends the visible grid
  to cover the equipment-panel positions — already designed for
  dynamic counts.

What's missing:

- No Backpack slot (backpacks currently abuse the Shoulder/cape slot).
- No Coin Purse slot.
- No mechanism to expand the equipment panel further when a backpack
  is equipped — the panel rows are fixed at startup.

## 2. New slots

### Backpack slot
- New `backpackSlotID = "Backpack"` constant.
- Validator: `IsBackpackSlotItem(item)` — soft-references VABackpacks
  via reflection (`com.Fire.VA_Backpacks` `BackpackApi.IsBackpack`).
  If VABackpacks isn't loaded, the slot is hidden (`IsActive=false`).
- Placed in the classic equipment block — extend the existing 3-row
  layout to a 4th row, OR slot it next to Trinket. Recommend the 4th
  row to keep the column count clean.

### Coin Purse slot
- New `coinSlotID = "CoinPurse"` constant.
- Validator: `IsCoinSlotItem(item)` — `item?.m_dropPrefab?.name == "Coins"`.
- Placed adjacent to the Backpack slot in the new 4th row, or in the
  food/ammo column. Recommend a permanent slot in the equipment block
  so it's always visible.
- **Infinite stacking** — the slot accepts coins past `m_maxStackSize`.
  See §4.

## 3. Slot layout (proposed)

```
Row 1:  Helmet  | Shoulder | (Food) | (Ammo)
Row 2:  Chest   | Utility  | (Food) | (Ammo)
Row 3:  Legs    | Trinket  | (Food) | (Ammo)
Row 4:  Backpack | CoinPurse| (Food) | (Ammo)   ← NEW

[Misc rows — config-driven, unchanged]

[Backpack-extension rows — appear only when a backpack is equipped]
   (rows × cols sourced from BackpackApi.GetEquippedRows/Columns)
```

## 4. Infinite-stack coin purse

Valheim normally caps stacks at `ItemDrop.ItemData.m_shared.m_maxStackSize`.
Three options, in order of safety:

1. **Per-slot bypass (recommended).**
   Patch `Inventory.AddItem(ItemDrop.ItemData)` and the helpers
   `Inventory.CanAddItem`/`Inventory.FindFreeStackSpace` so that when
   the target slot is the coin purse and the item is `Coins`, the cap
   is treated as `int.MaxValue`. Existing items in non-purse slots are
   unaffected — they still hit the vanilla cap.
2. **Mutate the prefab's `m_maxStackSize`.** Simplest, but breaks vanilla
   coin behavior in chests / drops / shops. Reject.
3. **Custom item subclass.** Overkill; we don't need a runtime type.

Implementation outline for option 1:

```csharp
// In a new file: Managers/CoinPurseStackPatch.cs
[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem),
    new[] { typeof(ItemDrop.ItemData) })]
static class Inventory_AddItem_CoinBypass
{
    static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
    {
        if (item == null || item.m_dropPrefab?.name != "Coins") return true;
        if (__instance != Player.m_localPlayer?.GetInventory()) return true;

        var purseSlot = Slots.AllSlots()
            .FirstOrDefault(s => s != null && s.IsCoinPurseSlot && s.IsActive);
        if (purseSlot == null) return true;  // no purse slot → vanilla path

        var existing = __instance.GetItemAt(purseSlot.GridPosition.x,
                                            purseSlot.GridPosition.y);
        if (existing != null && existing.m_dropPrefab?.name == "Coins")
        {
            existing.m_stack += item.m_stack;
            __result = true;
            return false;   // skip vanilla
        }
        return true;   // empty purse → vanilla AddItem will place it,
                       // then we trust later code not to clamp the stack
    }
}
```

Pair with a patch on `Inventory.GetMaxStackSize` (if it exists) or the
`StackAll` operations to never clamp when the slot is the purse.

## 5. Dynamic slot expansion (backpack mode)

`Slots.InitializeSlots()` needs to react to backpack equip / unequip
events from `BackpackApi`:

```csharp
// In Ascend.cs FinishInit, after VABackpacks is loaded:
BackpackApi.OnEquipped   += (player, item, cfg) => RebuildSlots();
BackpackApi.OnUnequipped += (player, item)      => RebuildSlots();

private static void RebuildSlots()
{
    Slots.InitializeSlots();      // already exists
    EquipmentPanel.UpdatePanel(); // already exists
}
```

In `Slots.InitializeSlots()`, after the existing first-block + misc-block
setup, append a "Backpack Storage" block when the player has a backpack
equipped:

```csharp
// === BACKPACK STORAGE BLOCK (only when equipped) ===
var bpItem = BackpackApi.GetEquippedBackpack(Player.m_localPlayer);
if (bpItem != null && BackpackApi.TryGetEquippedConfig(Player.m_localPlayer, out var bpCfg))
{
    int bpStartY = equipmentPanelStartY + 3 + miscRows + 1;  // gap row
    for (int row = 0; row < bpCfg.Rows; row++)
    {
        for (int col = 0; col < bpCfg.Columns; col++, index++)
        {
            string slotID = $"Backpack_{col}_{row}";
            slots[index] = new Slot(
                slotID, index,
                () => "Backpack",
                item => true,         // accepts anything (capacity-only check)
                () => true);
            slots[index].SetGridPosition(new Vector2i(col, bpStartY + row));
        }
    }
}
```

Items placed in these slots live in the player's normal inventory at
high grid positions. When the player **unequips** the backpack:

1. Read every item at grid positions inside the backpack block.
2. Pack them into a `ZPackage` (mirroring vanilla `Inventory.Save`).
3. Call `BackpackApi.WriteStorage(item, base64)`.
4. Remove those items from the player's inventory.
5. Rebuild slots → backpack block disappears.

When **re-equipping**:

1. `BackpackApi.ReadStorage(item)` returns the base64 string.
2. Rebuild slots → backpack block appears.
3. Decode the ZPackage and place each item at its saved grid position
   inside the backpack block.

If the new backpack is _smaller_ than the saved layout, overflow items
spill back into the main inventory; if no space, drop on the ground.

## 6. Files touched

| File | Change |
|------|--------|
| `Managers/Slots.cs` | Add `backpackSlotID`, `coinSlotID`; layout 4th row; backpack-extension block in `InitializeSlots()`; new `IsBackpackPurseSlot`/`IsCoinPurseSlot` properties on `Slot`. |
| `Managers/ItemSlotValidation.cs` | Add `IsBackpackSlotItem`, `IsCoinSlotItem`; wire into `GetValidatorForID`. |
| `Managers/EquipmentPanel.cs` | Render the new 4th row; render the dynamic backpack block when present. |
| `Managers/CoinPurseStackPatch.cs` (new) | Harmony patch to bypass `m_maxStackSize` on the coin purse slot. |
| `Managers/BackpackBridge.cs` (new) | Soft-reference VABackpacks via reflection (no hard assembly dep). Caches the API delegate handles. Exposes `Bridge.IsBackpack(item)`, `Bridge.GetConfig(item)`, etc. |
| `Managers/BackpackStorageSync.cs` (new) | Handles the equip/unequip round-trip described in §5. |
| `Ascend.cs` | Detect VABackpacks at startup; subscribe to events; call `BackpackBridge.Initialize()`. |
| `Managers/ConfigManager.cs` | Add `[CoinPurse] EnableCoinPurse` (default true) and `[Backpack] EnableBackpackSlot` (default true) so admins can disable individually. |

## 7. Soft-reference vs hard dependency on VABackpacks

VAInventory should remain usable without VABackpacks. The BackpackBridge
uses reflection at startup:

```csharp
var asm = AccessTools.AllAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "VABackpacks");
if (asm == null) { _isAvailable = false; return; }
var apiType = asm.GetType("VerdantsAscent.Managers.BackpackApi");
_isBackpack       = AccessTools.Method(apiType, "IsBackpack", new[]{typeof(ItemDrop.ItemData)});
_getEquipped      = AccessTools.Method(apiType, "GetEquippedBackpack");
_getRows          = AccessTools.Method(apiType, "GetEquippedRows");
_getCols          = AccessTools.Method(apiType, "GetEquippedColumns");
_readStorage      = AccessTools.Method(apiType, "ReadStorage");
_writeStorage     = AccessTools.Method(apiType, "WriteStorage");
// + event subscription via reflection
_isAvailable = true;
```

When `_isAvailable` is false, the Backpack slot's `IsActive` returns
false, and the dynamic-expansion block is skipped. Only the Coin Purse
slot remains, since it doesn't depend on backpacks.

## 8. Edge cases & notes

- **Hot-swapping backpacks of different sizes.** Always
  unequip-serialize → equip-deserialize. Spilling overflow back into
  main inventory uses the existing `TryFindFreeSlotForItem` logic.
- **Death drop.** When a player dies, their tombstone container should
  contain the equipped backpack item (which carries its `customData`),
  not the unpacked storage rows. Implementation: hook
  `Player.CreateTombStone` (or the death-loot codepath) to call the
  unequip serialization _before_ the inventory is moved to the tombstone.
- **Quick-transfer (Shift+click).** Should work without changes — items
  in the backpack-block grid positions are still in the player's
  `Inventory`, so vanilla Move-to-other-inventory logic finds them.
- **Tool hotbar collision.** Tool-hotbar slots use grid positions far
  below the equipment panel (`y = InventoryHeightPlayer + 10`). Backpack
  block sits above that. Verify no overlap during `InitializeSlots()`.
- **Coin slot during taming.** Companion `tamingItemPrefab = "Coins"`
  in FiresNPCs reads from `player.GetInventory()`, so coins in the purse
  are still counted (no change needed). Documented in
  `FiresNPCs/Refs/INVENTORY_BACKPACK_COMPAT_PLAN.md`.
- **Save format.** No new save data on the player side — the backpack's
  `customData["BackpackInventory"]` keeps everything. Same field used
  by VABackpacks's legacy Container path, so cross-compat is automatic.

## 9. Test plan

- [ ] Coin purse: pick up coins → land in purse, stack past 999.
- [ ] Coin purse: drop coins out → splits into vanilla-cap stacks again.
- [ ] Coin purse refuses a non-Coins item.
- [ ] Backpack slot refuses non-backpack items.
- [ ] Equip backpack → expansion rows appear, populated from previous
      `customData`.
- [ ] Unequip backpack → expansion rows disappear, items written back
      to the backpack item's `customData`.
- [ ] Hot-swap larger → smaller backpack: overflow spills back into
      main inventory or drops on ground.
- [ ] Player death: tombstone has the backpack item with its data
      preserved; world has no orphan items.
- [ ] Quick-transfer to/from chests works for backpack-block items.
- [ ] No VABackpacks present: backpack slot hidden, coin purse works.
- [ ] No VAInventory present (running VABackpacks alone): legacy
      Container path still works (covered by VABackpacks's own tests).
