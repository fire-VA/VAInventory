# Slot group registry — Phase 1 step 3

Landed VAInventory 1.0.21, 2026-09-24. Closes the defect class behind gap X1.

## What the defect actually was

`Inventory.StackAll` moved items out of quick/food/ammo slots because nothing patched it. But the
reason nobody noticed is structural, and it is not specific to StackAll: **`toolHotbarSlots` lived
outside the `slots` array**, so every path that cared about "is this a slot" had to *remember* to
consult two collections. Six places did. The capacity paths did not.

A defect that requires every future author to remember a list is not fixed by patching one method.

## The registry

`Slots.slotGroups` is the single list of slot collections. Each group declares two policies at
registration, and both are required arguments rather than defaulted, so a new group has to answer
them instead of silently inheriting:

| flag | question it answers |
|---|---|
| `VisibleInMainGrid` | Do these cells draw in the player's own grid despite sitting past the player rows? `IsGridPositionHidden` must not hide them. |
| `CountsAsGenericCapacity` | May `GetEmptySlots` / `HaveEmptySlot` report these as generic free space? |

Registered today:

```
Main        visibleInMainGrid: false  countsAsGenericCapacity: true
ToolHotbar  visibleInMainGrid: true   countsAsGenericCapacity: false
```

Those values reproduce the pre-registry behaviour exactly. They are not a judgement about what is
correct — see the open question below.

## Three views, and which to use

- `AllSlots()` — every slot in every group. The default. Use it unless you specifically need one of
  the others.
- `CapacitySlots()` — groups that count as generic free space. **Item-agnostic.** Only the two
  capacity postfixes use it.
- `Slots.slots` — the raw main array. Positional access only; see below.

Item-AWARE capacity does not go through `CapacitySlots()`. It goes through
`TryFindFreeSlotForItem`, which sees every group and honours each slot's `ItemFits`. That split is
deliberate: `Inventory.CanAddItem`'s correction (guard G3) is item-aware and must stay that way, or
`Player.AutoPickup` resumes pulling loot that `AddItem` then refuses.

## What still reads the raw array, and why

Not oversights — each needs a decision that is not this change's to make:

| site | why | what it needs |
|---|---|---|
| `EquipmentPanel.cs:177-191` | Indexes `slots[InventorySizePlayer + i]` positionally to lay out the panel. | Genuinely array-coupled. Dies with the prefab rework, which addresses cells by transform name. |
| `ItemSlotValidation.cs:409` | `API.GetAllExtraSlotsItems()` is a public API other mods may call. | Widening it to `AllSlots()` would start returning tool-hotbar items. Probably right — it says "all" — but it is a published contract, so it is a decision, not a cleanup. |
| `UniversalDragHandler.cs:127` | Hit-tests by `slot.Position`, which only main-array slots have set. | Needs the hotbar's own hit-testing story first. |

`Slots.slots` is the grep target for "call sites still to review". It should shrink, never grow.

## Open question — `InventoryHeightFull`

It takes the max y of groups flagged `VisibleInMainGrid` only. The obviously-correct rule is the max
y of **every** slot: a slot past `InventoryHeightFull` is out of bounds to
`Humanoid.DropInvalidItems`, which drops it.

It was left narrow on purpose. Widening can only raise the height, and the height is what
`DropInvalidItems` bounds-checks against — so the change is safe in principle and unprovable in
practice without running the layout across a config matrix. Today the two agree for the default
config; whether they agree for every combination of `configExtraRows`, bought rows, food/ammo
toggles, misc rows and equipped bags is exactly what has not been tested.

**This is a live-check item, not a code-reading item.**

## Not done yet — the declarations themselves

The brief asked for every slot declaration to sit behind one registration call. The registry now owns
every *enumeration*; `InitializeSlots` still hand-rolls the five blocks, and two hazards remain:

1. `totalExtraSlots` is computed at the top of the method by re-deriving each block's size, parallel
   to the loops below that actually write them. Two expressions of one fact.
2. Each block chains its own y-offset off the previous one
   (`miscBlockStartY` -> `vaultStartY` -> `bpCurrentY`). Insert a block in the middle and every
   later block moves.

Collapsing those into group descriptors is the rest of the job. **It is gated on a position-identity
proof, because grid positions are persisted**: items live at `m_gridPos` inside the player's
`Inventory`, so a layout that assigns different y-strips silently relocates or drops every saved slot
item. The gate is an offline harness (`reference_offline_mod_code_harness`) dumping
`(slotId -> gridPos)` for both builds across a config matrix and diffing. Nothing ships on a code
reading alone.
