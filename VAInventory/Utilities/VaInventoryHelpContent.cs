using System.Runtime.CompilerServices;
using UnityEngine;

namespace VerdantsAscent.Utilities
{
    // VAInventory's sections for the shared FiresCore help panel. Core is a SOFT
    // dependency — TryRegister checks the Chainloader before touching any
    // FiresCore type, and DoRegister is NoInlining so the JIT never resolves
    // FiresCore when Core is absent.
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
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Hotbars", 1, BuildHotbars);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "The Coin Pouch", 2, BuildCoinPouch);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Customization & Config", 3, BuildConfig);
        }

        private static void BuildPanel(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("The Equipment Panel");
            w.Paragraph("VAInventory puts a character panel beside your inventory — your worn gear " +
                "gets real slots instead of living mixed into the bag, and your bags open right in it.");
            w.Divider();

            w.SubHeader("The Slots");
            w.Bullet("Armor: Helmet, Shoulder, Chest, Legs, Utility.");
            w.Bullet("Trinket and Ring — extra accessory slots.");
            w.Bullet("Coin Purse — your currency lives here (see The Coin Pouch).");
            w.Bullet("Backpack — active when VABackpacks is installed; the worn bag sits here.");
            w.Bullet("Food and Ammo columns — quick-glance slots, toggleable in config " +
                "(ShowFoodSlots / ShowAmmoSlots).");
            w.Bullet("Torch — a torch or lantern kept ready: double-tap T (TorchSlot.Key) to take it " +
                "out or put it away.");
            w.Bullet("Pet — the pet slot.");
            w.Bullet("Tools / Food / Ammo pouches — click a pouch to open its slots in a small popup.");
            w.Bullet("TeleportBypass — the smuggler's pocket: ONE non-teleportable item (ore, " +
                "ingots) placed here rides through portals with you. Everything else still blocks.");
            w.Divider();

            w.SubHeader("More Room");
            w.Bullet("ExtraSlots.ExtraRows — up to 3 extra rows on the MAIN inventory grid [Synced].");
            w.Bullet("ExtraSlots.EquipmentPanelRows — up to 3 rows of free Misc slots on the " +
                "equipment panel [Synced].");
            w.Divider();

            w.SubHeader("Backpack Panels");
            w.Paragraph("Each equipped bag shows its contents as a section of the character panel, " +
                "under Misc — wear a shoulder bag and carry the Hobo Stick and you get two. When " +
                "they don't fit on screen, the bag sections scroll with the mouse wheel. Drag the " +
                "character panel to move it.");
        }

        private static void BuildHotbars(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Hotbars");
            w.Paragraph("Three extra on-screen hotbars beyond vanilla's, each draggable and " +
                "scalable, each slot with its own rebindable key:");
            w.Divider();

            w.Bullet("Tool hotbar — 4 slots by default (up to 8), keys Alt+1, Alt+2, ...");
            w.Bullet("Food hotbar — 3 slots, defaults Ctrl+1 ... Ctrl+3. Eat without opening " +
                "your inventory.");
            w.Bullet("Ammo hotbar — 3 slots, defaults Ctrl+Alt+1 ... Ctrl+Alt+3. Swap arrow " +
                "types mid-fight.");
            w.Divider();

            w.Paragraph("Keybinds accept modifier syntax (Alt+, Ctrl+, Shift+, combined) in each " +
                "hotbar's config section. ToolHotbar.Slots sets how many tool slots show; " +
                "ShowWhenEmpty controls whether empty bars render.");
        }

        private static void BuildCoinPouch(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("The Coin Pouch");
            w.Paragraph("The Coin Purse slot is a multi-currency wallet with no practical cap.");
            w.Divider();

            w.SubHeader("How It Works");
            w.Bullet("Configured currencies (default: Coins, Ruby, Amber, AmberPearl) are " +
                "AUTO-DEPOSITED into the pouch — pickups, trader change, quest rewards, all of it.");
            w.Bullet("Right-click a loose coin stack in your inventory to bank it into the pouch.");
            w.Bullet("Left-click the pouch slot to open the currency picker — every currency " +
                "with its total; click one to make it the displayed stack. Esc closes.");
            w.Bullet("Spending works exactly like vanilla — traders and vendors pull from the " +
                "pouch automatically.");
            w.Bullet("Stacks merge far past the vanilla cap, and the balance is protected " +
                "against save/load clamping — your hoard survives.");
            w.Divider();

            w.Paragraph("Add modded currencies via CoinPouch.CurrencyPrefabs (comma-separated " +
                "prefab names, up to 8 beyond the displayed one).");
        }

        private static void BuildConfig(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("Customization & Config");
            w.SubHeader("Custom Backgrounds");
            w.Paragraph("Every major screen (inventory, container, crafting, skills, texts, " +
                "trophies...) can wear your own PNG background: drop images into " +
                "config/VAInventoryBackgrounds/ and set the matching [UI] toggle + filename " +
                "pair. Relaunch to apply.");
            w.Divider();

            w.SubHeader("Key Settings");
            w.Bullet("ExtraSlots.ExtraRows / EquipmentPanelRows — extra capacity [Synced]");
            w.Bullet("ExtraSlots.ShowFoodSlots / ShowAmmoSlots — the food/ammo columns");
            w.Bullet("CoinPouch.CurrencyPrefabs — the pouch's currency list");
            w.Bullet("ToolHotbar / FoodHotbar / AmmoHotbar sections — positions, scale, keybinds");
            w.Bullet("General.ServerAuthority — lock the synced entries to server admins");
            w.Paragraph("Everything except the extra-row counts is client-local — each player " +
                "arranges their own UI.");
        }
    }
}
