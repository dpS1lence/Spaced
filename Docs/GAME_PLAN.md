# Spaced — Game Plan

Spaced is a satellite simulation sandbox. It starts from the **real sky**: today's catalog of
tracked objects, pulled live from CelesTrak and drawn around a real, rotating Earth. Then you
start changing it: launch single satellites or whole mega-constellations, pick their size and
mass, blow things up, speed up time, and watch what happens to low Earth orbit over the years
(drag decay, collisions, debris cascades — Kessler syndrome).

## Core loop

1. **Observe**: the real catalog at the current time. Click any object to see its orbit and stats.
2. **Change**: launch satellites (single or Walker constellations), pick a satellite class
   (CubeSat → space station), or destroy something (ASAT test).
3. **Fast-forward**: time warp from real time up to a year per second.
4. **Measure**: watch object counts, collisions, debris, reentries and the projected collision rate.

## Architecture

```
CelesTrak (OMM JSON) ──► CelestrakClient (2 h disk cache) ──► ObjectCatalog (SoA NativeLists)
                                                                  │
                     SimClock (Julian date, warp) ──────────────► PropagationJob (Burst, parallel)
                                                                  │  mean elements + J2 + drag
                                                                  ▼
                       positions / velocities (km) ──► CollisionJob (Burst, "cube" method)
                                                  │                 └─► BreakupModel → new debris
                                                  └──► GPU buffer ──► Satellites.shader (procedural quads)
EarthRenderer (GMST rotation, day/night from sun position) · OrbitCamera · IMGUI HUD
```

| Area | Choice | Why |
|---|---|---|
| Data source | CelesTrak `gp.php` OMM JSON (`active` + 4 debris groups) | Free, no account, ~15k objects. CelesTrak asks for ≤ 1 download per 2 h, so every group is cached on disk. |
| Propagation | Brouwer mean elements + secular J2 + exponential-atmosphere drag | One model for real, user-launched and generated objects. Cheap enough for 500k objects in Burst. Positions agree with SGP4 to tens of km, which is invisible at planet scale. |
| Collisions | NASA LEGEND-style "cube" statistical method | Discrete per-frame distance checks never catch 10 km/s impacts. The cube method is how real debris-evolution models estimate collision rates, and it still works at high time warp. |
| Breakups | Simplified NASA Standard Breakup Model | Fragment count, size, area-to-mass and Δv follow the published power laws, so cascades behave believably. |
| Rendering | One `GraphicsBuffer` + `Graphics.RenderPrimitives` | One draw call for every object. Constant pixel size, so you can see objects at any zoom. |
| Units | 1 Unity unit = 1000 km. ECI (TEME) → Unity by swapping Y/Z | Keeps floats precise out to GEO (~42 units). |
| UI | IMGUI for the prototype | Fastest to iterate on. Move to UI Toolkit once the panels settle. |

## Physical model notes

- **Mean motion**: OMM mean motion is SGP4 (Kozai). It is converted to Brouwer mean motion and
  semi-major axis exactly as SGP4 does during initialisation, so along-track timing stays correct.
- **J2**: secular drift of RAAN, argument of perigee and mean anomaly. That covers node
  precession, sun-synchronous orbits and Molniya-style behaviour.
- **Drag**: `da/dt = -√(μa)·ρ(h)·B`, with B = Cd·A/m (m²/kg). For real objects B comes from the
  catalog's BSTAR (`B ≈ 12.74·B*`). For launched objects it comes from the chosen satellite class.
  Density comes from Vallado's exponential atmosphere table. Perigee below 100 km counts as reentry.
- **Collision probability** for each pair sharing a cube of volume U during Δt:
  `P = v_rel · π(r₁+r₂)² · Δt / U`.
- **Breakup**: an impact is catastrophic when the impact energy exceeds 40 J/g of the target.
  Fragments ≥ 10 cm: `N = 0.1·M^0.75·Lc^-1.71`. Δv is log-normal with μ = 0.9·log₁₀(A/m) + 2.9.
- **Collision avoidance**: a tunable efficiency for pairs where at least one object can
  manoeuvre (launched satellites; real satellites flagged active).

## Roadmap

### Phase 1 — Foundation
- [x] Project setup tool (switches URP to the 3D Universal Renderer and creates the scene)
- [x] CelesTrak fetch with disk cache, offline fallback to stale cache
- [x] Burst propagation of every object (J2 + drag), reentries
- [x] Earth with NASA Blue Marble and Black Marble textures, real GMST rotation, sun-lit terminator, atmosphere rim
- [x] All objects rendered in one draw call, coloured by type
- [x] Time control (pause, reverse, warp up to 1 year/s, jump to now)
- [x] Orbit camera, click-to-select, orbit path and info for the selected object
- [x] Launch panel: single orbit (perigee/apogee) or Walker constellation, satellite classes
- [x] Statistical collision model + breakup debris, ASAT "destroy selected"
- [x] Stats: counts, collisions, reentries, projected collisions per year

### Phase 2 — Launches and realism (this commit)
- [x] Physically flown Falcon 9 from SLC-40, LC-39A and SLC-4E: gravity turn, max-Q throttle bucket,
      closed-loop second stage, coast + circularisation, GTO with self-raising payloads
- [x] Launch windows: liftoff when the pad passes under each target plane (J2 node drift included)
- [x] Payload capacity from the rocket equation, calibrated against the flight model; campaigns split
      constellations into missions with pad turnaround
- [x] Booster flip, entry burn, aerodynamic descent and predictive hoverslam onto a droneship parked at
      the predicted touchdown point; fairing halves fall back to the sea
- [x] Deployment trains that raise and phase into their slots; station-keeping for active satellites
- [x] Cleanup: controlled deorbit, instant removal, cancel launches, clear generated debris
- [x] Procedural 3D models at real scale: Starlink, OneWeb, smallsat, comsat, GPS, ISS-like station,
      CubeSat, spent stages and tumbling debris; arrays track the Sun; GPU instanced
- [x] Procedural Falcon 9 (sooty booster, octaweb, grid fins, legs, MVac glow, two-piece fairing),
      pads with towers, lightning masts and propellant farm, droneship, ground and sea
- [x] Plumes that widen with altitude, exhaust trail, liftoff steam, landing spray (particle systems)
- [x] Seasonal Blue Marble, city lights under clouds, cloud shadows, ray-marched Rayleigh/Mie
      atmosphere (blue sky from the pad, red sunsets, limb glow), HDR Sun, bloom, ACES tonemapping
- [x] Earth's shadow on satellites, earthshine, sky light
- [x] Two-scale camera rig with floating origin; director cameras and warp holds during launches
- [x] Asset Store / NASA model overrides per satellite kind

### Phase 3 — Make it a game
- Launch costs, a budget and contracts; Starship from Starbase; rideshare missions
- Scenarios and challenges: "Deploy 40,000 Starlinks without a cascade", "Clean up LEO"
- Satellite end-of-life and failure rates; active debris removal missions
- History graphs (objects and collision rate over time), heatmaps of orbital density
- Colour modes: by type, altitude, inclination, operator, launch year
- Ground tracks, coverage footprints, link-to-ground visualisation
- Sound: engine roar delayed by distance, sonic booms on landing

### Phase 4 — Fidelity and polish
- Full SGP4/SDP4 for real objects (accuracy mode for the selected object)
- Space-Track.org import (full catalog including debris and rocket bodies; needs an account)
- Real star catalog, Moon, terrain elevation and higher-resolution imagery tiles near the pads
- UI Toolkit interface, save/load scenarios, settings
- Time-varying solar activity in the atmosphere model (solar max shortens debris lifetimes)

## Data etiquette

CelesTrak updates GP data every ~2 hours. It refuses (HTTP 403) a second download of the same group
from the same network before the next update, and **any** request counts, including a `curl -I`
while debugging. `CelestrakClient` only downloads a group when its cache file is older than 2 hours,
and swaps `active` for smaller groups if it is refused. Keep it that way, never fetch on a timer, and
don't probe the game's groups by hand.
