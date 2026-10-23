using System.Runtime.CompilerServices;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAInventory's sections for the shared FiresCore help panel. Core is a SOFT
    // dependency — TryRegister checks the Chainloader before touching any
    // FiresCore type, and DoRegister is NoInlining so the JIT never resolves
    // FiresCore when Core is absent.
    // 1.0.254 (the lead's how-to audit, Tools\discord_howto_20261020\vainventory\CHANGES.md): brought up to date - Jewelry slot,
    // layouts incl. Panels (drag by header, corner resize, gentle docking, Shift = free), F1 Player > Stats, hotbar strip drag /
    // corner resize, ExtraSlots.CurrencyPrefabs, [UI.Backgrounds], vainv_overflow; no hidden keys named (config rule 1.0.252).
    internal static class VaInventoryHelpContent
    {
        private const string ModId = "VAInventory";
        private static bool _registered;

        public static void TryRegister()
        {
            if (_registered || Application.isBatchMode) return;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Fire.FiresUnifiedCore")) return;
            try { DoRegister(); _registered = true; }
            catch (System.Exception ex) { Debug.LogWarning($"[VAInventory] Help registration failed: {ex.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DoRegister()
        {
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "The Equipment Panel", 0, BuildPanel);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Layouts & Moving Panels", 1, BuildLayouts);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Hotbars", 2, BuildHotbars);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "The Coin Pouch", 3, BuildCoinPouch);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Settings & Commands", 4, BuildConfig);
        }

        private static void BuildPanel(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("The Equipment Panel");
            w.Paragraph("VAInventory puts a character panel beside your inventory — your worn gear " +
                "gets real slots around a live preview of your character, and your bags open right in it.");
            w.Divider();

            w.SubHeader("The Slots");
            w.Bullet("Armor: Helmet, Shoulder, Chest, Legs, Utility.");
            w.Bullet("Trinket, and Jewelry — click it for the Necklace, Left ring and Right ring slots.");
            w.Bullet("Coin Purse — your currency lives here (see The Coin Pouch).");
            w.Bullet("Backpack — active when VABackpacks is installed; the worn bag sits here.");
            w.Bullet("Torch — a torch or lantern kept ready: double-tap T to take it out or put it away.");
            w.Bullet("Pet — the pet slot.");
            w.Bullet("Tools / Food / Ammo pouches — click a pouch to open its slots in a small popup. " +
                "Right-click food in a pouch to eat it.");
            w.Bullet("TeleportBypass — ONE non-teleportable stack (ore, ingots) placed here rides " +
                "through portals with you. Everything else still blocks.");
            w.Bullet("The blue border on a slot means that item is equipped; the fill colour is its rarity.");
            w.Divider();

            w.SubHeader("Bags");
            w.Paragraph("Each equipped bag shows its contents as a section of the character panel. When " +
                "they don't fit, the bag sections scroll with the mouse wheel. If a bag gets fewer slots, " +
                "items that no longer fit are kept safe — type vainv_overflow in the console to get them back.");
            w.Divider();

            w.SubHeader("Player Stats");
            w.Paragraph("Press F1 and open Player > Stats: your character with their gear (hover an item " +
                "to inspect it) and every stat broken down — base, food, armour pieces, each class bonus " +
                "and your active effects.");
        }

        private static void BuildLayouts(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Layouts & Moving Panels");
            w.Paragraph("Three layouts (Settings: UI > Layout). All use the same slots, so switching moves no items.");
            w.Bullet("Panels — the carry grid, character panel, a companion's panel and crafting as separate panels.");
            w.Bullet("Unified — the character panel beside the carry grid.");
            w.Bullet("Legacy — the equipment panel under the carry grid.");
            w.Divider();

            w.SubHeader("Moving and Resizing (Panels)");
            w.Bullet("Drag a panel by its header to move it; drag its bottom-right corner to resize it.");
            w.Bullet("Panels dock gently to each other: grid, then your panel, then the companion's.");
            w.Bullet("Hold Shift while dragging to place a panel anywhere without docking.");
            w.Bullet("Drag a docked panel away to undock it. Docked, the corner scales the group; " +
                "undocked, only that panel.");
            w.Bullet("A chest opens in its own panel, moved and resized the same way.");
            w.Bullet("Your layout is saved per screen resolution. The layout editor's Reset puts panels back.");
        }

        private static void BuildHotbars(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Hotbars");
            w.Paragraph("Three extra on-screen hotbars beyond vanilla's, each slot with its own key:");
            w.Divider();

            w.Bullet("Tool hotbar — 4 slots by default (up to 8), keys Alt+1, Alt+2, ...");
            w.Bullet("Food hotbar — 3 slots, Ctrl+1 ... Ctrl+3. Eat without opening your inventory.");
            w.Bullet("Ammo hotbar — 3 slots, Ctrl+Alt+1 ... Ctrl+Alt+3. Swap arrows mid-fight.");
            w.Divider();

            w.Bullet("Move a hotbar by dragging the strip just above it (or any part of it in the layout editor).");
            w.Bullet("Resize it from its corner — the slots re-flow into rows. The bars dock to each other.");
            w.Bullet("Change a slot's key in F1 > Config settings (\"Slot N key\").");
        }

        private static void BuildCoinPouch(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("The Coin Pouch");
            w.Paragraph("The Coin Purse slot is a multi-currency wallet with no practical cap.");
            w.Divider();

            w.SubHeader("How It Works");
            w.Bullet("Coins, Ruby, Amber and AmberPearl are AUTO-DEPOSITED into the pouch — pickups, " +
                "trader change, quest rewards.");
            w.Bullet("Right-click a loose coin stack in your inventory to bank it into the pouch.");
            w.Bullet("Left-click the pouch slot to pick which currency is shown. Esc closes.");
            w.Bullet("Spending works exactly like vanilla — traders pull from the pouch automatically.");
            w.Bullet("Stacks merge far past the vanilla cap, and the balance survives save/load.");
            w.Divider();

            w.Paragraph("Modded currencies: add their prefab names to ExtraSlots > CurrencyPrefabs " +
                "(under Show advanced).");
        }

        private static void BuildConfig(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Settings & Commands");
            w.Paragraph("Open F1 > Config settings. Without \"Show advanced\" you see the keys and the basics: " +
                "UI > Layout, ExtraSlots > ExtraRows / ShowFoodSlots / ShowAmmoSlots, the hotbar show " +
                "toggles and the torch key. Panel and hotbar positions are saved when you drag them.");
            w.Divider();

            w.SubHeader("Custom Backgrounds (advanced)");
            w.Paragraph("Put PNG or JPG images in BepInEx/cache/VAInventory/Backgrounds/ and set the " +
                "matching toggle + file name in [UI.Backgrounds]. Relaunch to apply.");
            w.Divider();

            w.SubHeader("Multiplayer");
            w.Paragraph("General > ServerAuthority (advanced): the extra-row settings follow the server and " +
                "only an admin changes them. Everything else is per player.");
            w.Divider();

            w.SubHeader("Commands");
            w.Bullet("vainv_overflow — get back items kept safe when your inventory or a bag got smaller.");
        }
    }
}
