using System.Reflection;

namespace SailawayRevival
{
    // Click a line to grab it, scroll to ease or pull, click again to let go.
    // The stock game drops a line as soon as the cursor leaves it or the mouse
    // button is released; press-and-drag still works as before.
    public static class LineGrab
    {
        static ControlBase grabbed;

        static readonly FieldInfo leftClickedFrame =
            typeof(UIWorld).GetField("leftClickedFrame", BindingFlags.Instance | BindingFlags.NonPublic);

        // Hooked in at the start of ControlMaster.Update.
        public static void PreUpdate(ControlMaster master)
        {
            if (grabbed != null && (!grabbed.isSelected || master.curControl != grabbed))
            {
                grabbed = null; // released elsewhere: Esc, not allowed any more, boat changed
            }
            UIWorld ui = UIWorld.instance;
            if (ui == null || !ui.leftClicked)
            {
                return;
            }
            if (grabbed != null)
            {
                grabbed.isSelected = false;
                grabbed.isHighlighted = false;
                master.curControl = null;
                grabbed = null;
                ConsumeClick(ui);
                return;
            }
            ControlBase control = master.curControl;
            if (control != null && control.isHighlighted)
            {
                control.isSelected = true;
                if (control.isSelected)
                {
                    grabbed = control;
                    ConsumeClick(ui); // otherwise ControlMaster deselects it this frame
                }
            }
        }

        // Hooked in at the start of ControlMaster._FindControlUnderCursor:
        // true keeps the grabbed line instead of picking whatever is under the cursor.
        public static bool KeepGrab(ControlMaster master)
        {
            return grabbed != null && master.curControl == grabbed && grabbed.isSelected;
        }

        static void ConsumeClick(UIWorld ui)
        {
            if (leftClickedFrame != null)
            {
                leftClickedFrame.SetValue(ui, -1);
            }
        }
    }
}
