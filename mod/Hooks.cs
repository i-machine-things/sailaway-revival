using System.Reflection;
using UnityEngine;

namespace SailawayRevival
{
    // Entry points the patcher calls from the game's code. Keep these thin so the
    // patcher's hook list stays stable as features are added.
    public static class Hooks
    {
        // Start of ControlMaster.Update (runs every frame while a boat is loaded).
        public static void ControlMasterUpdate(ControlMaster master)
        {
            BoatSwitcher.EnsureCreated();
            LineGrab.PreUpdate(master);
            AutoSave.Tick();
            Waves.Tick();
        }

        // Start of ControlMaster._FindControlUnderCursor; true skips the original.
        public static bool SkipFindControlUnderCursor(ControlMaster master)
        {
            return LineGrab.KeepGrab(master);
        }

        // Start of MainMenu.ShowSubMenuBoat; true skips the original, which
        // would otherwise open CoverFlowSelector's broken MyBoats screen
        // (see BoatSwitcher.cs). Mirrors what the original does at its end
        // (hide mainMenuScreen) since we're replacing everything before that.
        public static bool SkipShowSubMenuBoat(MainMenu mainMenu)
        {
            var field = typeof(MainMenu).GetField("mainMenuScreen", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field?.GetValue(mainMenu) is GameObject screen)
            {
                screen.SetActive(false);
            }
            BoatSwitcher.EnsureCreated();
            BoatSwitcher.Show();
            return true;
        }
    }
}
