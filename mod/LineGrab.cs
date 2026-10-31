using System.Reflection;
using UnityEngine;

namespace SailawayRevival
{
    // Click a line to grab it, scroll to ease or pull, click again to let go.
    // The stock game drops a line as soon as the cursor leaves it or the mouse
    // button is released; press-and-drag still works as before.
    public static class LineGrab
    {
        const float MaxClickSeconds = 0.35f;
        const float MaxClickPixels = 12f;

        static ControlBase grabbed;
        static float downTime = -1f;
        static Vector3 downPosition;

        static readonly FieldInfo leftClickedFrame =
            typeof(UIWorld).GetField("leftClickedFrame", BindingFlags.Instance | BindingFlags.NonPublic);

        // Hooked in at the start of ControlMaster.Update.
        public static void PreUpdate(ControlMaster master)
        {
            if (grabbed != null && (master.curControl != grabbed || Input.GetKeyDown(KeyCode.Escape)))
            {
                Release(master);
            }
            if (grabbed != null)
            {
                // Refresh the selection so the game's idle timeouts never drop it.
                grabbed.isSelected = true;
                if (!grabbed.isSelected)
                {
                    grabbed = null; // no longer allowed to handle it
                }
            }

            // UIWorld's own click flag is set during UI event processing, which can
            // run after this Update in the same frame, so read the mouse directly.
            UIWorld ui = UIWorld.instance;
            if (Input.GetMouseButtonDown(0))
            {
                downTime = Time.unscaledTime;
                downPosition = Input.mousePosition;
            }
            if (!Input.GetMouseButtonUp(0) || downTime < 0f)
            {
                return;
            }
            bool click = Time.unscaledTime - downTime < MaxClickSeconds
                && Vector3.Distance(Input.mousePosition, downPosition) < MaxClickPixels
                && ui != null && ui.cursorInWorld;
            downTime = -1f;
            if (!click)
            {
                return;
            }
            if (grabbed != null)
            {
                Release(master);
                ConsumeClick(ui);
                return;
            }
            ControlBase control = master.curControl;
            if (control != null && (control.isHighlighted || control.isSelected))
            {
                control.isSelected = true;
                if (control.isSelected)
                {
                    grabbed = control;
                    ConsumeClick(ui); // otherwise ControlMaster deselects it
                }
            }
        }

        // Hooked in at the start of ControlMaster._FindControlUnderCursor:
        // true keeps the grabbed line instead of picking whatever is under the cursor.
        public static bool KeepGrab(ControlMaster master)
        {
            return grabbed != null && master.curControl == grabbed;
        }

        static void Release(ControlMaster master)
        {
            if (grabbed != null)
            {
                grabbed.isSelected = false;
                grabbed.isHighlighted = false;
                if (master.curControl == grabbed)
                {
                    master.curControl = null;
                }
            }
            grabbed = null;
        }

        static void ConsumeClick(UIWorld ui)
        {
            if (leftClickedFrame != null && ui != null)
            {
                leftClickedFrame.SetValue(ui, -1);
            }
        }
    }
}
