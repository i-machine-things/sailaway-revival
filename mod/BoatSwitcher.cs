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
            go.AddComponent<BoatSwitcher>();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.B))
            {
                visible = !visible;
            }
        }

        void OnGUI()
        {
            if (!visible)
            {
                return;
            }
            GUILayout.BeginArea(new Rect(20, 20, 260, 320), GUI.skin.box);
            GUILayout.Label("Switch boat (B to close)");
            for (int i = 0; i < BoatNames.Length; i++)
            {
                int btpNr = i + 1;
                if (GUILayout.Button(BoatNames[i]))
                {
                    StartCoroutine(SwitchAndLog(btpNr));
                    visible = false;
                }
            }
            GUILayout.EndArea();
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
