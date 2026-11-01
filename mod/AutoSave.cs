using UnityEngine;

namespace SailawayRevival
{
    // The game saved the boat (position, trim) every few seconds only through the
    // multiplayer connection, and otherwise only when quitting from the menu, so a
    // crash lost the whole session. Trigger the same save once a minute.
    public static class AutoSave
    {
        const float IntervalSeconds = 60f;

        static float nextSave = IntervalSeconds;

        public static void Tick()
        {
            if (Time.unscaledTime < nextSave)
            {
                return;
            }
            nextSave = Time.unscaledTime + IntervalSeconds;
            if (Global.userBoat != null && StateMachine.instance != null && StateMachine.instance.boatLoaded)
            {
                Global.userBoat.SaveData();
            }
        }
    }
}
