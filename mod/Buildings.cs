using UnityEngine;

namespace SailawayRevival
{
    // OSMBuildings fetches real building footprints from the public Overpass
    // API (overpass-api.de) directly - nothing routes through our server, so
    // it should just work offline. But nothing in the game ever calls
    // OSMBuildings.ToggleBuildings (confirmed: zero callers in the whole
    // assembly), so whatever the component's enabled state is in the scene
    // is permanent. Force it on once we find it.
    public static class Buildings
    {
        static bool done;

        public static void EnableIfFound()
        {
            if (done)
            {
                return;
            }
            var osm = Object.FindObjectOfType<OSMBuildings>();
            if (osm == null)
            {
                return;
            }
            osm.enabled = true;
            done = true;
        }
    }
}
