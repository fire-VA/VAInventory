# Inventory rework — Phase 0 audit

Static pass, 2026-09-24. Source of truth for vanilla behaviour: `assembly_valheimREF1.0` (Valheim 1.0.15).
Proposal canvas: https://claude.ai/artifact/XNDnm5sAWpjRo4rezTmgUH

Nothing here has been runtime-verified. Items marked **LIVE** need a game session before they are treated as settled.

---

## 1. Patch census

62 `[HarmonyPatch(typeof(...))]` declarations in `VAInventory\Managers`. Vanilla methods carrying more than one
patch class, where Harmony alone decides the order:

| Vanilla method | Classes | Where |
|---|---|---|
| `InventoryGrid.UpdateGui` | 5 | EquipmentPanel, SafeGuiPatches (x2), InventoryGrid_ExtendGridForExtraSlots, TeleportBypassPatch |
| `InventoryGui.Show` | 3 | EquipmentPanel, InventoryGuiBackgroundPatch, InventoryGuiContainerPositionPatch |
| `InventoryGrid.DropItem` | 3 | InventoryInteraction (x2), CoinPurseStackPatch |
| `InventoryGui.Update` | 2 | EquipmentPanel, SafeGuiPatches |
| `InventoryGui.Hide` | 2 | BackpackStorageSync, InventoryGuiContainerPositionPatch |
| `Inventory.CanAddItem` | 2 | InventoryInteraction, CoinPurseStackPatch |
| `Player.OnInventoryChanged` | 2 | ItemSlotValidation, InventoryInteraction |
| `Player.OnSpawned` | 2 | InventoryInteraction (string), CoinPurseStackPatch (nameof) |
| `InventoryGrid.OnRightDown` | 2 | InventoryInteraction, CoinPurseStackPatch |
| `ZNet.Awake` | 2 | ConfigSync, AdminSyncing |

`Inventory.RemoveItem` x2 is genuinely two overloads, not a duplicate.

**Correction — `Inventory.AddItem` x2 WAS a duplicate, and this document said otherwise.** Caught by [1954f5].
Vanilla does have two ItemData overloads (`Inventory.cs:98` and `:148`), which is what made the original sentence
look safe, but both VAInventory patches were on the 1-arg `AddItem(ItemDrop.ItemData)` at `:98` — CoinPurse's
prefix wrote `nameof` with an explicit signature and InventoryInteraction's postfix wrote the string form, so a
name-keyed read of the attributes saw two different things and I recorded "distinct overloads" without resolving
either to a method. Same root cause as the `DropItem` row, where CoinPurse's explicit 4-arg signature and two
bare-name patches were all one method. **A patch census keyed on the attribute text is not a census; resolve every
target to a method before counting.** That makes 11 duplicated methods, not 10.

Only one pair declares an explicit order: `Inventory_CanAddItem_CheckExtraSlots` carries `[HarmonyPriority(Priority.High)]`
so its correction lands before the coin-purse postfix upgrades the result. Every other collision above is unordered.

**Correction, from a probe by session [1954f5] against the actual BepInEx `0Harmony.dll` (HarmonyX 2.9.0.0):**
HarmonyX does not behave like stock Harmony here, and the difference changes what a merge is allowed to do.

1. A prefix returning `false` does **not** skip the remaining prefixes. Every other prefix on that method still
   runs; only the ORIGINAL is skipped. Postfixes and finalizers still run.
2. `runOriginal` is sticky-false and AND-ed — a later prefix returning true cannot undo an earlier false, even
   when it declares `bool __runOriginal` and sees `False`.
3. `[HarmonyPriority]` orders prefixes correctly regardless of apply order.

So the duplicates above are less order-fragile than "Harmony decides" suggests, but they are **side-effect
coupled**: today, when `InventoryGrid_DropItem_BlockInvalid` vetoes a drop, `InventoryGrid_DropItem_Robust` still
runs and still calls `EquipmentPanel.ClearDragState()`. Same on `InventoryGrid.UpdateGui` — SafeGuiPatches vetoing
does not stop ExtendGridForExtraSlots from building elements. A naive merge into one prefix that early-returns on
the first veto would silently drop those side effects. The consolidation has to preserve the run-all-then-veto
shape explicitly.

---

## 2. Guards that already exist — keep them

**G1 — Row purchase/sale.** Vanilla `Player.SetInventorySize` (`Player.cs:3783`):

```
rows = Mathf.Clamp(rows, 0, 9)
m_inventory.SetHeight(rows)          <- every slot is now out of bounds
AddUniqueKeyValue("invrows", rows)
InventoryGui.instance.SetInventorySize(rows)
DropInvalidItems()                   <- drops anything with m_gridPos.y >= GetHeight()
```

`Humanoid.DropInvalidItems` (`Humanoid.cs:562`) drops on `m_gridPos.x/y` outside `GetWidth()/GetHeight()`.
Two prefixes stand between those lines and the ground:
- `PlayerInventoryRows.cs:74` — prefix on `SetInventorySize`, shifts every slot-area item by the row delta and hands
  anything in a disappearing row to `InventoryInteraction.TryRelocateOrDropItem`. The row count the layout was built
  on is kept per character in `m_customData["VAInventory.VanillaPlayerRows"]`, clamped 0..9.
- `PlayerInventoryRows.cs:84` — prefix on `DropInvalidItems`, calls `UpdatePlayerInventorySize()` to restore
  `m_height` to the full slot layout first. This also covers the `inventoryclean` console command
  (`Terminal.cs:1366`), which calls `DropInvalidItems` directly.

**G2 — `Inventory.FindEmptySlot`** (`InventoryInteraction.cs:98`). Prefix shrinks `m_height` to the visible player
area so vanilla only scans real cells; restore is in a **Finalizer**, not a Postfix, so a throw anywhere in the chain
cannot leave the inventory stuck short. This is the pattern any new height juggling must copy.

**G3 — `Inventory.CanAddItem`** (`InventoryInteraction.cs:158`). Vanilla's math uses the full extended height, so
every empty reserved cell counted as generic free space and `Player.AutoPickup` pulled loot that `AddItem` then
refused — the drop hovered and followed the player forever. The postfix recomputes against the visible area, then
lets typed slots opt an item back in.

**G4 — `GetEmptySlots` / `HaveEmptySlot`** — same recomputation, visible cells plus free typed slots.

**G5 — Death.** `TombStone.EasyFitInInventory` is patched, and `UpdatePlayerInventorySize` raises the tombstone
container's `m_height` to the full layout. Vanilla helps here: `Inventory.MoveInventoryToGrave` (`Inventory.cs:995`)
copies `m_width`/`m_height` straight off the player inventory and moves items at their existing `m_gridPos`.

**G6 — Teleport.** `Inventory.IsTeleportable` patched in `TeleportBypassPatch.cs`.

---

## 3. Gaps found

### X1 — `Inventory.StackAll` is unpatched. HIGH.

`Inventory.cs:216`. Reached from the UI by the "stack all" button:
`InventoryGui.OnStackAll` (`InventoryGui.cs:549`) calls
`m_currentContainer.GetInventory().StackAll(Player.m_localPlayer.GetInventory())`, so `fromInventory` is the player.
There is a second entry at `InventoryGui.cs:466` (`m_currentContainer.StackAll()`).

The loop:

```
foreach (item in fromInventory.GetAllItems())
    if (this.ContainsItemByName(item.m_shared.m_name)
        && !Player.m_localPlayer.IsItemEquiped(item)
        && this.AddItem(item))
        fromInventory.RemoveItem(item);
```

`GetAllItems()` returns the whole backing list, slot items included. The only exclusion is `IsItemEquiped`.
An **unequipped** item parked in a quick, food, ammo or extra slot whose name already exists in the chest is
therefore eligible to be moved out. No patch on `StackAll` exists anywhere in the tree — grep returns only two
planning documents.

Expected symptom: pressing stack-all at a chest that already holds, say, cooked meat or arrows silently empties
the matching food/ammo slots.

**FIXED in VAInventory 1.0.19, 2026-09-24, by [c68b48].** `Inventory_StackAll_KeepSlotItems` in
`Managers\InventoryInteraction.cs`, beside the other capacity gates. Prefix pulls slot items out of the player's
backing list into `__state`; Finalizer puts them back and re-fires the private `Inventory.Changed` via a cached
`MethodInfo`, because vanilla already fired its own while they were hidden and would otherwise leave weight and the
grid reporting the hidden state. Items are selected by `Slots.IsItemInSlot`, which keys off
`m_gridPos.y >= InventoryHeightPlayer` — configured extra rows are inside that height, so they stay stackable and
only real slots are held back. It is the only patch on `StackAll`, and the prefix returns void, so none of the
HarmonyX `runOriginal` semantics above apply to it.

**STILL LIVE-UNVERIFIED.** Both the bug and the fix are reasoned from the code path only. The check is: stack-all
at a chest holding a copy of something sitting in a food, ammo or quick slot, and confirm the slot keeps its item
and the weight readout is right immediately afterward.

### X2 — `Inventory.SlotsUsedPercentage` is unpatched, and a plan doc says otherwise. LOW.

`Inventory.cs:509` divides by `m_height * m_width` using the extended height, so the readout under-reports how
full the player is. `Managers\Refs\EXTRASLOTS_ALIGNMENT_PLAN.md:469` states that a patch named
`Inventory_SlotsUsedPercentage_ExcludeRedundantSlots` "fixes this". **No such patch exists.** Treat that document
as stale for anything it claims is already done.

### X3 — `Inventory.MoveItemToThis`. **NOT A DEFECT.** Read to a verdict 2026-09-24. Do not patch.

Two overloads, and every caller is already covered.

- **5-arg** (`Inventory.cs:257`) calls `AddItem(item, amount, x, y)` with an explicit position. Its only callers
  are `InventoryGrid.cs:462`, `:464` and `:465` — all three inside `InventoryGrid.DropItem`, which VAInventory
  already gates with prefixes. Covered by the existing drop gate.
- **2-arg** (`Inventory.cs:249`) calls `AddItem(item)`, the free-slot-search overload that VAInventory patches and
  whose `FindEmptySlot` is gated to the visible area. Its callers are `InventoryGui.cs:669` and `:671`, both inside
  the `InventoryGrid.Modifier.Move` branch — the shift/middle-click quick-move. That path calls
  `RemoveEquipAction` then `UnequipItem` on the item first, and acts on the ONE item the player targeted.

The distinction that matters versus X1: this is a targeted single-item move the player aimed at a specific item,
not a bulk sweep. Moving a slot item out when the player explicitly clicks it is the intent, not a defect.

### X4 — `Inventory.AddItem(ItemDrop.ItemData, Vector2i pos)`. **NOT A DEFECT, and patching it would cause one.**

`Inventory.cs:148`. It does place at an explicit position with no slot-type validation, and the bounds check that
guards it uses `m_height` — which under VAInventory is the EXTENDED height, so slot rows read as in-bounds. That
is why it looked dangerous. But the reachable callers say otherwise.

There are **no callers anywhere outside `Inventory.cs`**. Inside it there is exactly one, at `Inventory.cs:873`,
in the long `AddItem(name, stack, quality, variant, crafterID, crafterName, Vector2i position, cheated, ...)`
overload:

```
position.x < 0 || position.y < 0 || position.x >= m_width || position.y >= m_height
    ? this.AddItem(component2.m_itemData)          // the safe, gated branch
    : this.AddItem(component2.m_itemData, position) // explicit placement
```

And `position` comes from `InventoryGui.cs:1355`:

```
Vector2i position = new Vector2i(-1, -1);
if (<upgrading>) position = this.m_craftUpgradeItem.m_gridPos;
```

So it is `(-1,-1)` for every ordinary craft, which takes the safe branch. The explicit branch fires only when
**upgrading an item**, and the position it uses is the grid cell the item being upgraded already occupied.
Upgrading a chestpiece that sits in the chest slot therefore puts the upgraded chestpiece back in the chest slot —
which is exactly right. A slot-validation gate here would send upgraded gear to the main inventory instead, or
refuse the placement and hand the item to `dropIfFullInv`. **Leave it alone.**

Residual risk, noted not fixed: a third-party mod calling the 2-arg overload directly gets no validation. Nothing
in the stack does today.

### X6 — right-clicking the coin purse evacuates it, and the code that tries to prevent that does nothing. MEDIUM.

Raised by [1954f5] from the HarmonyX semantics above; mechanism confirmed independently here by reading both
prefixes. Two bool prefixes sit on `InventoryGrid.OnRightDown`, in apply order CoinPurseStackPatch (TypeDef #32)
then `InventoryGrid_OnRightClick_TransferOrDrop` (#111). Under HarmonyX the first returning false does **not**
stop the second.

Right-clicking coins **in a normal grid cell** is safe: CoinPurse moves them into the purse and returns false,
then TransferOrDrop re-reads `GetItemAt(pos)`, finds the cell empty, and returns true. Nothing happens twice.

Right-clicking the coins **already sitting in the purse slot** is the problem. CoinPurse hits
`CoinPurseStackPatch.cs:406` — `if (purseSlot == null || item.m_gridPos == purseSlot.GridPosition) return false;` —
which plainly means "this is the purse, do nothing". It does not do nothing. TransferOrDrop runs next, sees a
non-null item, sees `Slots.IsGridPositionASlot(pos)` is true because the purse IS a slot, and moves the coins out
into the main inventory.

Under stock Harmony that `return false` would have suppressed the second prefix and the purse would be inert on
right-click, which is evidently what its author expected.

**DECIDED by Fire, 2026-09-24: do nothing — the inert behaviour was the intent.**
**FIXED in VAInventory 1.0.20 by [1954f5]**, as part of the consolidation. The merged
`InventoryGrid_OnRightDown_Hook` in `Managers\VaInventoryVanillaHooks.cs` short-circuits on the first refusal:

```
if (!CoinPurseStackPatch.OnRightDownDepositCoins(__instance, element)) return false;
return InventoryInteraction.OnRightDownTransferOrDrop(__instance, element);
```

so CoinPurse's "this is the purse, do nothing" now genuinely stops the chain. `DropItem` got the same treatment.
`UpdateGui` deliberately did **not**: there the veto means "vanilla state is broken", and skipping EquipmentPanel's
prefix while its postfix still ran would leave `__state` at 0 and mark the panel dirty every suppressed frame, so
it keeps run-all-then-veto. Short-circuiting is the fix for a refusal that means "handled"; it is the wrong answer
for one that means "unsafe".

### X7 — `InventoryGrid.DropItem` side effects run after a veto. LOW.

Same cause. CoinPurse (#32) vetoes a drop, then `BlockInvalid` (#108) and `Robust` (#112) both still run, and
Robust can call `EquipmentPanel.ClearDragState()` on a drop CoinPurse already handled. Benign as far as anyone has
observed, but it is the same shape as X6 and belongs in the same sweep.

Both X6 and X7 want the same live check: right-click a coin stack in the purse slot, and drag coins onto the purse
cell, with a slot item adjacent.

### X8 — `InventoryHeightFull` holds by slack, not by construction. LATENT. Raised by [1954f5]; mechanism read here.

Not a live defect today, and not proven safe either. Worth stating precisely because the failure mode is item loss.

`Slots.cs:95` computes:

```
int baseFull = InventoryHeightPlayer + GetTargetInventoryHeight(slots.Length, InventoryWidth);
int maxVisibleY = <max GridPosition.y over VisibleInMainGrid groups, else InventoryHeightPlayer - 1>;
return Math.Max(baseFull, maxVisibleY + 1);
```

The stake: the `DropInvalidItems` guard (G1) restores `m_height` to exactly this value, and vanilla then drops
every item with `m_gridPos.y >= GetHeight()`. **Any slot whose y reaches `InventoryHeightFull` is dropped on the
ground.** So this expression is load-bearing for item safety, not just layout.

Why it currently holds: `slots.Length` is `requiredTotalSlots + 16`, and `requiredTotalSlots` already includes
`startIndex = InventoryHeightPlayer * InventoryWidth`. So `GetTargetInventoryHeight(slots.Length, ...)` is itself
about `InventoryHeightPlayer` rows plus the extras, and `baseFull` then adds `InventoryHeightPlayer` **again**.
The result overshoots the real maximum by roughly a full player height plus the 16-slot buffer. That margin is
what has been absorbing every layout change so far.

Why that is not a guarantee: actual positions do not come from that arithmetic at all. They come from explicit
`SetGridPosition` calls — `equipmentPanelStartY + row` at `Slots.cs:705`, `vaultStartY + v / InventoryWidth` at
`:745` — while the `Slot` constructor's own fallback at `:920` uses a third convention
(`_index / InventoryWidth + InventoryHeightPlayer`, on an `_index` that already starts past the player rows).
Three conventions, none derived from the others. Nothing makes `max(actual y) < InventoryHeightFull` true; it is
true by margin.

Correct rule, and it removes the mismatch rather than widening the margin: take `max(GridPosition.y) + 1` over
**every** registered slot, not over `slots.Length` and not over visible groups only. [1954f5] left it narrow
deliberately because the two rules agree on the default config and the config matrix — bought rows, extra rows,
food/ammo toggles, misc rows, equipped bags — is untested. That matrix is the work, not the one-line change.

`Inventory.cs:478` scans the whole list. The CanAddItem postfix calls it **on purpose**
(`InventoryInteraction.cs:185`) because `AddItem`'s stacking loop also tops up stacks sitting in slots. Noted so
nobody "fixes" it into a regression.

---

## 4. Open questions — decisions, not bugs

**O1.** `MoveInventoryToGrave` excludes `m_equipped` items (`Inventory.cs:1000`). With the weapon-bar slots holding
the currently equipped weapon, what reaches the grave and what stays on the corpse needs to be stated deliberately
rather than discovered.

**O2.** A character saved with slot items and then loaded on a client **without** VAInventory: its items sit past
the vanilla height, and the first row purchase or `inventoryclean` drops them. Park, refuse, or accept — pick one.

**O3.** `General.ServerAuthority` exists as a key. What it should mean for slot layout, and what a client does when
its saved layout is wider than a server allows, is undefined.

---

## 5. Live checks still owed

1. Save and reload a character with every slot filled, single player and dedicated.
2. Buy a row, then sell one, with all slots full. Nothing on the ground, nothing moved.
3. Die with the bar slots filled; recover the tombstone; check positions. Covers O1.
4. **Stack-all at a chest holding a copy of something in a food/ammo/quick slot.** Covers X1.
5. Same character with and without VABackpacks.
6. Drop every item type onto every slot, including the refusals.
7. Join a server on the current build with a client on the new one.

---

## 6. What Phase 1 should do

Consolidate before adding. In order:

1. Merge the 5 `InventoryGrid.UpdateGui` classes into one, then `InventoryGui.Show` (3) and `DropItem` (3).
   One hook per vanilla method, ordering explicit and in one file.
2. Close X1, and read X3 and X4 to a verdict.
3. Move every slot declaration behind a single registration call, so a new slot group cannot miss one of the
   five capacity paths the way `StackAll` was missed.
4. Only then the Unity prefab work.

No visible change should come out of Phase 1. That is the point.
