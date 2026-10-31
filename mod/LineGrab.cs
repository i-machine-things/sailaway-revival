using System.Reflection;
using UnityEngine;

namespace SailawayRevival
{
    // Click a line to grab it, scroll to ease or pull, click again (or Esc /
    // right-click) to let go. The stock game drops a line as soon as the mouse
    // button is released or the cursor leaves it; holding and dragging still
    // pulls the line as before.
    public static class LineGrab
    {
        const float MaxClickSeconds = 0.35f;
        const float MaxClickPixels = 12f;

        static ControlBase grabbed;
        static bool releaseOnClick;
        static float downTime = -1f;
        static Vector3 downPosition;

        static readonly FieldInfo leftClickedFrame =
            typeof(UIWorld).GetField("leftClickedFrame", BindingFlags.Instance | BindingFlags.NonPublic);

        // Hooked in at the start of ControlMaster.Update.
        public static void PreUpdate(ControlMaster master)
        {
            UIWorld ui = UIWorld.instance;
            bool inWorld = ui != null && ui.cursorInWorld;

            if (grabbed != null && (Input.GetKeyDown(KeyCode.Escape) || (Input.GetMouseButtonDown(1) && inWorld)))
            {
                Release(master);
            }

            if (Input.GetMouseButtonDown(0) && inWorld)
            {
                downTime = Time.unscaledTime;
                downPosition = Input.mousePosition;
                releaseOnClick = grabbed != null;
                ControlBase control = master.curControl;
                if (grabbed == null && control != null && control.isHighlighted)
                {
                    grabbed = control; // grab on press; the game's own drag still works
                }
            }
            else if (Input.GetMouseButtonUp(0) && downTime >= 0f)
            {
                bool click = Time.unscaledTime - downTime < MaxClickSeconds
                    && Vector3.Distance(Input.mousePosition, downPosition) < MaxClickPixels;
                downTime = -1f;
                if (grabbed != null)
                {
                    ConsumeClick(ui); // the game deselects the line on click otherwise
                    if (click && releaseOnClick)
                    {
                        Release(master);
                    }
                }
                releaseOnClick = false;
            }

            if (grabbed != null)
            {
                // Whatever the game did this frame (mouse-up, idle timeouts, hover),
                // keep the grabbed line current and selected.
                master.curControl = grabbed;
                grabbed.isHighlighted = true;
                grabbed.isSelected = true;
                if (!grabbed.isSelected)
                {
                    grabbed = null; // no longer allowed to handle it
                }
            }
        }

        // Hooked in at the start of ControlMaster._FindControlUnderCursor:
        // true keeps the grabbed line instead of picking whatever is under the cursor.
        public static bool KeepGrab(ControlMaster master)
        {
            return grabbed != null;
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
            releaseOnClick = false;
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
