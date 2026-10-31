namespace SailawayRevival
{
    // Entry points the patcher calls from the game's code. Keep these thin so the
    // patcher's hook list stays stable as features are added.
    public static class Hooks
    {
        // Start of ControlMaster.Update (runs every frame while a boat is loaded).
        public static void ControlMasterUpdate(ControlMaster master)
        {
            LineGrab.PreUpdate(master);
            AutoSave.Tick();
        }

        // Start of ControlMaster._FindControlUnderCursor; true skips the original.
        public static bool SkipFindControlUnderCursor(ControlMaster master)
        {
            return LineGrab.KeepGrab(master);
        }
    }
}
