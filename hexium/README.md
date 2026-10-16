# VAInventory

A character panel for your Valheim inventory: real slots for worn gear, a coin pouch, extra hotbars and room for
your bags. Part of the Fires mod family.

Open your inventory and a character panel sits beside the carry grid. Your armour and accessories get their own
slots around a live preview of your character instead of living mixed into the bag, and three extra hotbars let you
reach tools, food and ammo without opening anything.

## What you get

- **Equipment slots.** Helmet, Shoulder, Chest, Legs and Utility, plus Trinket and Ring accessory slots, a Coin Purse,
  a Backpack slot (used when VABackpacks is installed), a Torch slot and a Pet slot.
- **Live character preview.** Mouse wheel zooms, left-drag spins, right-drag moves, double-click resets. Drag the
  panel by its backdrop to move it.
- **Pouches.** Click the Tools, Food or Ammo pouch to open its slots in a small popup.
- **Teleport bypass slot.** One non-teleportable stack (ore, ingots) placed here goes through portals with you.
  Everything else still blocks the portal.
- **Torch slot.** Keep a torch or lantern ready and double-tap T to take it out or put it away.
- **Coin pouch.** A multi-currency wallet. Coins, Ruby, Amber and AmberPearl are deposited automatically from
  pickups, trader change and quest rewards. Right-click a loose coin stack to bank it, left-click the pouch to pick
  which currency is shown. Traders take payment from the pouch as normal, and stacks go far past the normal cap.
- **Three extra hotbars**, each draggable, scalable and with its own key per slot:
  - Tool hotbar: 4 slots (up to 8), Alt+1, Alt+2, ...
  - Food hotbar: 3 slots, Ctrl+1 to Ctrl+3. Eat without opening your inventory.
  - Ammo hotbar: 3 slots, Ctrl+Alt+1 to Ctrl+Alt+3. Swap arrows mid-fight.
- **Bag panels.** With VABackpacks, each equipped bag shows its contents as a section of the character panel. Wear a
  shoulder bag and carry the Hobo Stick and you get two. When they don't fit, the bag sections scroll.
- **More room (optional, off by default).** Up to 3 extra rows on the main inventory grid and up to 3 rows of free
  Misc slots on the panel.
- **Custom backgrounds (optional, off by default).** Give the inventory, chest, crafting, skills, texts, trophies and
  other screens your own PNG or JPG background.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore (optional) - when it is installed, VAInventory adds its pages to the in-game Fires help panel.
- VABackpacks (optional) - fills the Backpack slot and the bag panels.

## Installation

- Install with a mod manager, or copy the `VAInventory` folder into `BepInEx/plugins`.
- Client mod. Install it on the server too when you want the server to control the synced settings below.

## Configuration

Settings live in `BepInEx/config/com.Fire.VAInventory.cfg`.

- `[UI] Layout` (default `Unified`) - the character panel beside the carry grid. `Legacy` puts the equipment panel
  under the carry grid instead. Both use the same slots, so switching moves no items.
- `[UI] PlayerPanelScale` (default 1, 0.6 to 1.2) and `PanelOpacity` (default 0.92) - size and backdrop of the panel.
- `[UI] ResetContainerWindowPosition` - puts a dragged chest panel back below the carry grid.
- `[ExtraSlots] ExtraRows` (default 0, up to 3) and `EquipmentPanelRows` (default 0, up to 3) - extra room.
- `[ExtraSlots] ShowFoodSlots` / `ShowAmmoSlots` (default on) - the food and ammo columns on the panel.
- `[ExtraSlots] CurrencyPrefabs` (default `Coins,Ruby,Amber,AmberPearl`) - what the coin pouch holds. The first is
  the one shown. Add modded currencies by prefab name.
- `[ToolHotbar]`, `[FoodHotbar]`, `[AmmoHotbar]` - position, scale and a key per slot. `[ToolHotbar] Slots`
  (default 4, 0 to 8) and `ShowWhenEmpty` (default off). The food and ammo bars can be hidden with `ShowHotbar`.
- `[TorchSlot]` - `Key` (default T), `Double tap` (default on) and `Double tap seconds` (default 0.35).
- `[UI.Backgrounds]` - a toggle and a file name per screen. Put the images in
  `BepInEx/config/VAInventoryBackgrounds/`. All off by default; relaunch the game to apply.

## Multiplayer

- `[General] ServerAuthority` (default off). When it is on, the extra-row settings follow the server and only an
  admin can change them.
- Everything else is per player: each player arranges their own panel and hotbars.

## Console commands

`vainv_dumpui`, `ui_test`, `bag_resize_test` and `inventory_upgrade_test` are developer and test tools. They need
devcommands (cheat) and are not needed for normal play.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Licence

MIT. See the `LICENSE` file.

Recommended: FiresUnifiedCore (the Panels layout and its dock need it; without Core the panel falls back to the Unified layout).

## Tested and proven (1.0.177)
- Inventory panel test (vainventory_panel_test): 19 of 19 steps PASS on the Panels layout - misc slots in / out, swaps in misc and in the Tool, Food and Ammo pouches, equip by drop, unequip by right-click.
- Moving a bag item between slots: the cell map rebuilds, nothing unplaced, no other item moves (bag move step PASS).
- Panels layout shot test PASS: panels on screen, no overlaps.
- Tool / Food / Ammo hotbars start from your config positions and return there on Reset layout; they hide while the large map is open.

## Still being worked on
- Unified layout at small screens (1024x768): a fix that keeps the panel on screen is in, not yet proven in game.
- Recommended: FiresUnifiedCore (the Panels layout and its dock need it). Without Core the panel falls back to the Unified layout - checked in the code only, not in game.
