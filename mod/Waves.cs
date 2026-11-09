using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace SailawayRevival
{
    // The game protocol has no field for wave period at all - disassembling
    // Weather.BackendSucceeded shows it only ever reads "waveheight"/"wavedir"
    // from the server. Weather.waveLength instead comes from Sea.set_avgDepth,
    // purely a function of local seafloor depth and nothing to do with the
    // real sea state. get_waveHeight then renders
    // clamp(rawWaveHeight * waveLength/15, 0.005, waveLength/10) - real
    // significant wave height is almost always above 1.5m, which pins that
    // at maximum steepness regardless of what the real number is.
    //
    // Worse, Sea.Update only copies Weather's wave fields into Sea's own
    // cache (and the shader) when local depth changes past a threshold
    // (Sea.set_avgDepth's own gate) - in open ocean, where depth barely
    // changes, that fires once near boat spawn and then rarely again, so
    // whatever it grabbed (often before our own fetch below even completed)
    // sticks for the rest of the session. Sea.GetWaterLevelWave (actual
    // buoyancy/boat motion) reads that same stale Sea-side cache directly,
    // not Weather, so overriding Weather alone never reached physics either.
    //
    // This polls our own server directly (not part of the game's own
    // networking) for the real wave height/period at the boat's position,
    // and every frame pushes a wavelength derived from the real period
    // (deep-water dispersion) and a correspondingly clamped height straight
    // into Weather, Sea's cache, and the sea shader material - bypassing
    // Sea's own refresh gate entirely instead of hoping it reopens.
    public static class Waves
    {
        const float PollIntervalSeconds = 1800f; // matches the server's own weather cache TTL
        const float RetryDelaySeconds = 30f; // backoff after a failed fetch, instead of waiting out the full interval
        const float MinPeriodSeconds = 2f;

        static readonly FieldInfo seaWaveLengthField = typeof(Sea).GetField("_waveLength", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo seaWaveLengthLongField = typeof(Sea).GetField("_waveLengthLong", BindingFlags.Instance | BindingFlags.NonPublic);
        // The patcher no-ops Weather.set_waveLength (Sea.set_avgDepth was the
        // only other caller, racing it back to a fake depth-based value), so
        // this writes the backing field directly instead of going through
        // the now-inert property.
        static readonly FieldInfo weatherWaveLengthField = typeof(Weather).GetField("_waveLength", BindingFlags.Instance | BindingFlags.NonPublic);

        // Far more than a boat can cover in one frame (~0.3m/frame at a
        // fast 30kn), so any bigger jump between ticks is a teleport, not
        // sailing - about 1km at the equator.
        const double TeleportDegrees = 0.01;

        static string baseUrl;
        static float lastPoll = -PollIntervalSeconds;
        static bool havePosition;
        static double lastLat, lastLon;
        static volatile bool fetching;
        static volatile bool refetchPending; // a teleport landed while a fetch was already in flight
        static volatile bool retryPending; // the last fetch failed; retry sooner than the full interval
        static volatile float realHeight;
        static volatile float realPeriod = 8f;

        public static void Tick()
        {
            var weather = Global.weather;
            var sea = Global.sea;
            var gps = GPS.instance;
            if (weather == null || gps == null)
            {
                return;
            }

            // Same source Weather._UpdateWeather itself reads (confirmed by
            // disassembly) - UserBoat's own lat/lon fields are a save/sync
            // copy that lags behind right after a teleport (observed: wind
            // picked up the new location instantly, waves didn't), so this
            // is the one guaranteed to be live.
            double lat = gps.BoatCoordinates.latitude;
            double lon = gps.BoatCoordinates.longitude;
            bool teleported = havePosition && (Math.Abs(lat - lastLat) > TeleportDegrees || Math.Abs(lon - lastLon) > TeleportDegrees);
            lastLat = lat;
            lastLon = lon;
            havePosition = true;

            // Position-blind otherwise - the 30min timer alone would keep
            // serving the old location's sea state right after a teleport.
            // A teleport while a fetch is already running would otherwise be
            // lost outright: `teleported` is true for one frame only, and a
            // fetch in flight (10s server-side timeout) easily outlasts it.
            if (teleported)
            {
                refetchPending = true;
            }
            bool retryDue = retryPending && Time.unscaledTime - lastPoll > RetryDelaySeconds;
            if ((refetchPending || retryDue || Time.unscaledTime - lastPoll > PollIntervalSeconds) && !fetching)
            {
                refetchPending = false;
                retryPending = false;
                lastPoll = Time.unscaledTime;
                fetching = true;
                Task.Run(() => Fetch(lat, lon));
            }

            float period = Mathf.Max(realPeriod, MinPeriodSeconds);
            float waveLength = 1.56f * period * period; // deep-water dispersion: L = g*T^2/(2*pi)
            float waveLengthLong = waveLength * 2f; // matches the engine's own long/short ratio
            float effectiveHeight = Mathf.Min(realHeight, waveLength / 10f); // same steepness cap as get_waveHeight

            // Keeps the few other Weather.waveHeight/waveLength consumers
            // (DolphinCtrl, SeagullCtrl, WhaleCtrl spawn checks) consistent.
            weatherWaveLengthField?.SetValue(weather, waveLength);
            weather.waveLengthLong = waveLengthLong;
            weather.rawWaveHeight = effectiveHeight * 15f / waveLength;

            if (sea != null)
            {
                sea.waveHeight = effectiveHeight;
                seaWaveLengthField?.SetValue(sea, waveLength);
                seaWaveLengthLongField?.SetValue(sea, waveLengthLong);
                if (sea.seaMaterial != null)
                {
                    sea.seaMaterial.SetFloat("_WaveHeight", effectiveHeight);
                    sea.seaMaterial.SetFloat("_WaveLength", waveLength);
                    sea.seaMaterial.SetFloat("_WaveLengthLong", waveLengthLong);
                }
            }
        }

        static void Fetch(double lat, double lon)
        {
            try
            {
                string url = BaseUrl() + "waves.pl?lat=" + lat.ToString(CultureInfo.InvariantCulture)
                    + "&lon=" + lon.ToString(CultureInfo.InvariantCulture);
                using (var client = new WebClient())
                {
                    string json = client.DownloadString(url);
                    float? h = ExtractNumber(json, "waveheight");
                    float? p = ExtractNumber(json, "waveperiod");
                    if (h.HasValue) realHeight = h.Value;
                    if (p.HasValue) realPeriod = p.Value;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Waves: fetch failed: " + e.Message);
                retryPending = true; // let Tick (main thread) decide when to retry, not this background task
            }
            finally
            {
                fetching = false;
            }
        }

        static float? ExtractNumber(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.eE+-]+)");
            float v;
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return null;
        }

        // Reads the port install.sh picked (same file the Python server itself
        // reads); the patched game talks to 127.0.0.1 on that port already.
        static string BaseUrl()
        {
            if (baseUrl != null)
            {
                return baseUrl;
            }
            int port = 47652;
            try
            {
                string configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(configDir))
                {
                    configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                }
                string path = Path.Combine(configDir, "sailaway-revival", "config.toml");
                var m = Regex.Match(File.ReadAllText(path), @"^port\s*=\s*(\d+)", RegexOptions.Multiline);
                if (m.Success)
                {
                    port = int.Parse(m.Groups[1].Value);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Waves: could not read config.toml, defaulting to port " + port + ": " + e.Message);
            }
            baseUrl = "http://127.0.0.1:" + port + "/sw/";
            return baseUrl;
        }
    }
}
