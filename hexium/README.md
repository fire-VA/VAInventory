# VAInventory

A character panel for your Valheim inventory: real slots for your worn gear, a coin pouch, three extra hotbars and
room for your bags. Part of the Fires mod family.

## What you get

- **Equipment slots** around a live preview of your character: Helmet, Shoulder, Chest, Legs, Utility, Trinket and
  Ring, plus a Coin Purse, Backpack slot (with VABackpacks), Torch slot and Pet slot.
- **Panels you arrange yourself.** Move any panel by its header and resize it from its corner. Panels snap together;
  hold Shift while dragging to place one freely. Your layout is saved.
- **Companion panel.** Open a companion's inventory and its panel sits beside yours, with Gear, Stats and Appearance
  buttons.
- **Chest panel.** Chests open in their own panel you can move and resize.
- **Stats page.** In the Fires hub (F1), Player > Stats shows every stat broken down: base, food, class bonuses,
  armour and active effects. Companions get the same page.
- **Pouches.** Click the Tools, Food or Ammo pouch to open its slots.
- **Teleport bypass slot.** One non-teleportable stack (ore, ingots) here goes through portals with you.
- **Torch slot.** Keep a torch or lantern ready.
- **Coin pouch.** Coins, Ruby, Amber and Amber Pearls go in automatically from pickups, traders and quests, with no
  stack limit. Traders take payment straight from it.
- **Three extra hotbars:** Tools (4 slots, up to 8), Food (3 slots) and Ammo (3 slots). Eat or swap arrows
  mid-fight without opening anything.
- **Bag panels.** With VABackpacks, each equipped bag shows its contents in the character panel.
- **More room (optional, off by default).** Up to 3 extra rows on the main inventory grid.

## How to use

- **Tool hotbar:** Alt+1, Alt+2, ...
- **Food hotbar:** Ctrl+1 to Ctrl+3.
- **Ammo hotbar:** Ctrl+Alt+1 to Ctrl+Alt+3.
- **Torch:** double-tap T to take it out or put it away.
- **Move a hotbar** by dragging anywhere on it; **resize** it from its corner (slots re-flow into rows). The Fires UI
  and the classic UI each keep their own hotbar layout.
- **Character preview:** mouse wheel zooms, left-drag spins, right-drag moves, double-click resets.
- **Coin pouch:** right-click a loose coin stack to put it in the pouch; left-click the pouch to choose which
  currency it shows.

## Install

- Install with a mod manager, or copy the `VAInventory` folder into `BepInEx/plugins`.
- Client mod. Also install it on the server if you want the server to control the extra-rows setting.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore 0.2.752 or newer
- VABackpacks (optional) - fills the Backpack slot and the bag panels.

## Good to know

- Settings are in the Fires hub (F1) config page or `BepInEx/config/com.Fire.VAInventory.cfg`. You can change every
  hotbar key, the torch key, extra rows, and show or hide the Food and Ammo slots and hotbars.
- Panel and hotbar positions are saved when you drag them; there's nothing to set in the config.
- Each player arranges their own panels and hotbars.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Licence

MIT. See the `LICENSE` file.
