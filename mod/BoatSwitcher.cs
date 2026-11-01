using System.Collections;
using System.Reflection;
using UnityEngine;

namespace SailawayRevival
{
    // CoverFlowSelector's MyBoats/AddExtraBoat screens are broken in this EOL
    // build: scrolling is awkward, and AddExtraBoat is a hardcoded dead end that
    // shows "UpgradeFunctionNotAllowed" before ever contacting the backend. This
    // calls UserBoat.SetUbtNr directly (the same public method the UI eventually
    // calls), bypassing both screens entirely. Earlier attempts called
    // Backend.GetBoatInfo directly and never worked: SetUbtNr also destroys the
    // old boat model via Clear(true) and updates UserBoat.ubtNr before issuing
    // the request, both of which GetBoatInfo's caller is expected to have done.
    public class BoatSwitcher : MonoBehaviour
    {
        static bool created;
        static BoatSwitcher instance;
        bool visible;

        static readonly string[] BoatNames =
        {
            "Sailaway Cruiser 38", "Mini Transat", "Caribbean Rose",
            "52' Cruising Catamaran", "50' Performance Cruiser",
            "Nordic Folkboat", "Marieholm IF Folkboat",
            "32' Offshore Racer Figaro 3", "Sailaway Ketch", "Sailaway Imoca 60",
        };

        public static void EnsureCreated()
        {
            if (created)
            {
                return;
            }
            created = true;
            var go = new GameObject("SailawayRevivalBoatSwitcher");
            Object.DontDestroyOnLoad(go);
            instance = go.AddComponent<BoatSwitcher>();
        }

        // Called from Hooks.SkipShowSubMenuBoat, which replaces the main
        // menu's broken "Boat" button entirely (see patcher's hook on
        // MainMenu.ShowSubMenuBoat).
        public static void Show()
        {
            if (instance != null)
            {
                instance.visible = true;
            }
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.B))
            {
                if (visible)
                {
                    CloseAndRestoreMenu();
                }
                else
                {
                    visible = true;
                }
            }
            else if (visible && Input.GetKeyDown(KeyCode.Escape))
            {
                CloseAndRestoreMenu();
            }
        }

        const int Columns = 5;
        const int TileSize = 140;
        GUIStyle nameStyle;

        void OnGUI()
        {
            if (!visible)
            {
                return;
            }
            if (nameStyle == null)
            {
                nameStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    wordWrap = true,
                    fontSize = 11,
                };
            }

            // Layout math below is all in 1920x1080-reference pixels; scaling
            // (and re-centering on the real screen) happens in one place here
            // instead of threading Screen.width/height through every size.
            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 1920f, Screen.height / 1080f) * 2f, 0.5f, 4f);
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), Vector2.zero);
            float refScreenWidth = Screen.width / scale;
            float refScreenHeight = Screen.height / scale;

            int rows = Mathf.CeilToInt(BoatNames.Length / (float)Columns);
            float width = Columns * (TileSize + 10) + 20;
            float height = rows * (TileSize + 40) + 50;
            float x = (refScreenWidth - width) / 2f;
            float y = (refScreenHeight - height) / 2f;
            GUILayout.BeginArea(new Rect(x, y, width, height), GUI.skin.box);
            GUILayout.Label("Switch boat (B to close)");
            for (int row = 0; row < rows; row++)
            {
                GUILayout.BeginHorizontal();
                for (int col = 0; col < Columns; col++)
                {
                    int i = row * Columns + col;
                    if (i >= BoatNames.Length)
                    {
                        break;
                    }
                    int btpNr = i + 1;
                    GUILayout.BeginVertical(GUILayout.Width(TileSize));
                    var thumb = BoatThumbnails.Get(btpNr);
                    bool clicked = thumb != null
                        ? GUILayout.Button(thumb, GUILayout.Width(TileSize), GUILayout.Height(TileSize))
                        : GUILayout.Button("(loading)", GUILayout.Width(TileSize), GUILayout.Height(TileSize));
                    GUILayout.Label(BoatNames[i], nameStyle, GUILayout.Width(TileSize));
                    GUILayout.EndVertical();
                    if (clicked)
                    {
                        StartCoroutine(SwitchAndLog(btpNr));
                        CloseAndRestoreMenu();
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        // We replace MainMenu's "Boat" screen entirely (see Hooks.cs), but
        // never call the game's own MainMenu.Close(), which is what disables
        // the background blur/color-corrector it turns on while any main
        // menu screen is open (MainMenu.CloseWhenScreenBecameBlank). Without
        // this the screen stays permanently blurred after picking a boat.
        void CloseAndRestoreMenu()
        {
            visible = false;
            if (MainMenu.instance != null)
            {
                MainMenu.instance.Close();
            }
        }

        static object ReadPrivate(object target, string fieldName)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f == null ? "<no field>" : f.GetValue(target);
        }

        IEnumerator SwitchAndLog(int btpNr)
        {
            var ub = Global.userBoat;
            object protoBefore = ReadPrivate(ub, "boatPrototype");
            object boatBefore = ReadPrivate(ub, "boat");
            Debug.Log(string.Format(
                "[Revival] switch requested to btp {0}: before btpNr={1} btpName={2} boatDataLoaded={3} boatLoaded={4} prototype={5} boat={6}",
                btpNr, ub.btpNr, ub.btpName,
                StateMachine.instance == null ? "<no SM>" : StateMachine.instance.boatDataLoaded.ToString(),
                StateMachine.instance == null ? "<no SM>" : StateMachine.instance.boatLoaded.ToString(),
                protoBefore, boatBefore));

            // forceRefresh=true: without it, re-picking the boat type you're
            // already on takes SetUbtNr's "skip" branch, which leaves
            // boatDataLoaded/boatLoaded stuck false (it clears them
            // unconditionally up front, intending a reload that then never
            // happens) and the boat ends up half-loaded and uncontrollable.
            ub.SetUbtNr(btpNr, true, true);

            yield return new WaitForSeconds(1.5f);

            object protoAfter = ReadPrivate(ub, "boatPrototype");
            object boatAfter = ReadPrivate(ub, "boat");
            Debug.Log(string.Format(
                "[Revival] switch to btp {0} +1.5s: btpNr={1} btpName={2} boatDataLoaded={3} boatLoaded={4} prototype={5} boat={6} protoChanged={7} boatChanged={8}",
                btpNr, ub.btpNr, ub.btpName,
                StateMachine.instance == null ? "<no SM>" : StateMachine.instance.boatDataLoaded.ToString(),
                StateMachine.instance == null ? "<no SM>" : StateMachine.instance.boatLoaded.ToString(),
                protoAfter, boatAfter,
                !object.Equals(protoBefore, protoAfter), !object.Equals(boatBefore, boatAfter)));
        }
    }
}
