# Spaced

A space company game in Unity, set in today's real sky. It loads the real catalog of tracked
objects from CelesTrak and draws them around a real, rotating Earth under today's real clouds. You
run a launch company: take contracts, fly them on a physically simulated Falcon 9 or Starship from
real pads, land the boosters on droneships or catch them with the tower, build constellations that
earn money, and work your way to the Moon. A sandbox mode keeps everything free and unlocked, so you
can fast-forward and see what your constellation does to low Earth orbit: drag decay, collisions,
debris cascades.

See [Docs/GAME_PLAN.md](Docs/GAME_PLAN.md) for the design, the physics and the roadmap.

## Getting started
<img width="1908" height="903" alt="Screenshot 2026-10-26 135049" src="https://github.com/user-attachments/assets/4507eb8c-2991-4288-bd8f-5c4b3460bcca" />

1. Open the project in Unity **6000.4.5f1** and let the scripts compile.
2. Run **Spaced → Set Up Project** from the menu bar (once). It switches URP to the 3D Universal
   Renderer, names the `SpacedLocal` layer and creates `Assets/Scenes/Spaced.unity`.
3. Press **Play** and pick **New career** (or **Sandbox**) in the main menu. The first run downloads about 7 MB of orbital data and about 13 MB of NASA
   imagery (Blue Marble for the current month, Black Marble city lights, clouds). Everything is cached
   in `Application.persistentDataPath`.

### Your first launch
<img width="1903" height="905" alt="Screenshot 2026-10-26 134843" src="https://github.com/user-attachments/assets/68b12cad-0d6b-4b78-9e65-871fb75c3e68" />

In the **Launch** panel keep *Falcon 9 from pad* and *SLC-40*, pick **Constellation → preset
"One Starlink launch (20 sats, 1 plane)"**, then press **Schedule**. The game finds the next launch
window (when the pad passes under the target orbit plane) and slows time down as it approaches. The
*Director* then cuts between shots:

pad tracking camera (with rumble) → chase through max-Q → the second-stage onboard camera
watching the booster fall away → booster entry and landing burn → droneship → coast →
circularisation → deployment → the new satellites, close up.

While you watch, **cinematic mode** (`C`) hides the satellites and panels and shows a
webcast-style overlay: mission clock, stage 1 and stage 2 speed and altitude, an event timeline,
and callouts (IGNITION, LIFTOFF, MAX-Q, MECO, STAGE SEPARATION, ENTRY BURN, THE FALCON HAS LANDED…).
Pad floodlights and the engine glow light up the rocket, the ground and the smoke, which is what
makes night launches look good. Engines light at T-3 s while the clamps hold the rocket down. Watch
for nitrogen puffs at staging and during the booster's flip, and the vapour collar around Mach 1.

Use **Mission Control** to pick a camera yourself (pad, chase, booster, droneship, stage 2
onboard, booster onboard), or **Skip to T-1 min** for later launches. Launches nobody is watching
at high time warp are resolved in one step: their satellites still appear in the right orbits.

## Career

The main menu offers a **career** (the game) and a **sandbox** (everything free). In a career you
start with $250M, one pad (SLC-40) and Falcon 9:

- **Contracts** — the board offers jobs from customers: deploy satellites to a given orbit, build a
  constellation over several planes and launches, photograph a place, or fly to the Moon. Accept one,
  pick the rocket and pad, check the cost against the reward, and book the launch. You're paid when
  the satellites reach orbit (or the picture is taken, or the spacecraft makes it). Miss the deadline
  and you pay a penalty and lose reputation. Offers expire, and urgent ones pay a premium.
- **Your own business** — satellites you launch yourself (Launch tab, cost = rockets + satellites)
  earn money every day: Comms satellites for the share of the globe they cover, Navigation ones for
  theirs, Imaging ones for each picture they sell.
- **Bills** — staff, facilities and satellite operations are charged monthly. Below $0 for 45 days
  and the company goes bankrupt.
- **Reputation** grows with every delivered contract, landed booster and milestone, and drops with
  failures. It brings bigger contracts (constellations, geostationary satellites, the Moon) and the
  right to buy upgrades in the **Company** tab: pad LC-39A, Vandenberg for polar orbits, and the
  Starship programme with its Starbase pad.
- **Milestones** (first launch, first landing, tower catch, 100 satellites, first Moon landing…) pay
  bonuses; random events (solar storms with brilliant aurorae, grants, investors, insurance bills)
  keep things moving.

A career runs forwards only: no reverse time and no jumping back to "now".

### Saving

The game saves itself every three minutes, when you press **Save**, and when you quit, to
`persistentDataPath/saves/career.spaced` (or `sandbox.spaced`). A save holds the whole simulation:
every object in orbit (including your debris), your fleet, the gallery pictures, scheduled launches,
Moon missions in flight and the company. **Continue** in the main menu picks up where you left off;
launches that were in the air reach orbit straight away.

## The Moon

The **The Moon** side of the Launch tab flies real lunar trajectories under Earth and Moon gravity:
a parking orbit timed so its plane holds the Moon at arrival, trans-lunar injection by the Falcon 9
upper stage (or the refuelled Ship), mid-course corrections planned on a background thread, then
either a free-return **flyby** with re-entry, **lunar orbit** insertion, or orbit, deorbit and a
throttled powered descent to a **landing**. The director slows time for every burn and picks shots:
the spacecraft, the whole Earth–Moon trajectory, the spacecraft over the Moon, and the landing site
with its regolith and boulders.

While you follow a Moon mission (or fly the camera out past 150,000 km), the ~20,000 Earth-orbit
satellites are hidden and simulated in coarser steps (a few times a second, collisions once a
second), which takes load off the PC. Untick *Hide Earth-orbit satellites* in Mission Control to
keep them.

### Free roam

Press **G** (or **Free roam** in the top bar) to fly the camera yourself anywhere between the Earth and the
Moon: WASD or the arrows to fly, Q/E down and up, hold Shift to go faster, drag to look around and
scroll to zoom. The buttons jump to the Earth, the Moon or a view of both; the camera moves with
whichever body you're near, so the Moon doesn't run away at high time warp. Esc or G goes back.

## Starship

Starship launches from Starbase, Texas: 33 Raptors, hot staging, a boostback burn and a catch by the
tower's chopsticks. It lifts more than 100 tonnes, so constellations need far fewer launches, and it
can land on the Moon after refuelling in orbit.

## Real weather

- **Clouds** are rebuilt from today's satellite pictures: the geostationary infrared mosaic from
  GOES-East, GOES-West and Himawari (every 10 minutes, day and night) and, where those don't reach
  (Europe, Africa, the Indian Ocean), the latest daily VIIRS pass. Far from today the classic cloud
  map is used instead.
- **Launch weather**: every pad has an hourly Open-Meteo forecast, and droneship areas a wave
  forecast. Gusts over 15 m/s, thunderstorms, heavy rain or waves over 4 m at the droneship scrub the
  launch at T-60 s and it is rebooked for the next opportunity (turn this off in Mission Control).
- **Imaging**: pictures taken under cloud show it, and requested pictures (and imaging contracts) wait
  for a clear pass.

## Your satellites

Every launch or placement becomes a **fleet** with a payload role, picked in the Launch tab (the
default follows the satellite class):

- **Imaging**: takes pictures of the ground. Each picture covers the camera's real swath (field of
  view × slant range), so higher orbits and off-nadir shots see more but in less detail. Detail is
  limited by the 10 m Sentinel-2 source imagery, and night passes come back dark. Use *Take picture
  now*, *Auto* (every 10 sim-minutes over daylight), or Shift+click the globe to queue an **imaging
  request**: the first satellite to pass within 30° off-nadir in daylight takes it. The **Gallery**
  has a large viewer with a scale bar and metadata, *Show on globe*, and *Save PNG* (to
  `persistentDataPath/captures`).
- **Communications**: your coverage lights up on the globe in blue, counting ground within view above
  25° elevation, with the percentage of Earth covered.
- **Navigation**: green where at least four of your satellites are above 10°, which is what a GNSS
  position fix needs.

The **My Satellites** tab (`M`) lists your fleets. For each one you can step through its satellites
and view them up close, and move the whole fleet to a new altitude (electric-propulsion spiral,
about 100 km per day). You can also toggle station-keeping and collision avoidance, run the role's
tools, deorbit, or remove the fleet. *My missions* lists your launch campaigns, with a button to jump
to the next liftoff.

## Sound

Every sound is synthesised in code at startup, so there are no audio files and no licences:
- A soft ambient loop: drifting D-major chords, bell shimmers and reverb.
- Rocket roar that reaches you at the speed of sound, delayed and muffled with distance, and silent
  above the atmosphere except on the onboard cameras.
- Sonic booms before landing, staging thunks and nitrogen hiss, countdown ticks, a deployment chime, a
  camera shutter and UI clicks.

Volumes are under **Sound** in the top bar.

## Controls

**Globe** (the default view): it works like an online map and stays fixed to the Earth.

| Input | Action |
|---|---|
| Drag | Move the map (speed follows your altitude, with a little momentum) |
| Right-drag, middle-drag or Ctrl+drag | Rotate the heading and tilt towards the horizon |
| Scroll | Smooth zoom towards the cursor, from 150,000 km down to ~150 m |
| Double-click | Fly to that spot |
| `W` `A` `S` `D` / arrows | Pan |
| `Q` / `E` | Rotate |
| `Z` / `X` | Tilt / untilt |
| `PgUp` / `PgDn` | Zoom |
| `Home`, or the **N** button | North up, straight down |
| Shift+click | Ask your imaging satellites for a picture of that spot |

The bar at the bottom shows the cursor's latitude/longitude, camera altitude, heading, tilt and a
scale bar. Cities and the world's spaceports are labelled. A selected satellite shows its orbit and
its **ground track** (where it will pass over the Earth).

**Everything else**

| Input | Action |
|---|---|
| Click an object | Select it |
| `F` | View the selected object up close (drag to orbit it, scroll to zoom) |
| `Esc` / **Earth view** | Back to the globe |
| `Space` | Pause |
| `+` / `−` (or `.` / `,`) | Time warp, from real time up to 1 year per second (held lower during launches) |
| `R` | Reverse time |
| `N` | Jump to the current time |
| `L` | Lock the globe to Earth's rotation (on) or to the stars (off) |
| `C` | Cinematic launch mode on/off |
| `M` | My Satellites panel |
| `H` | Hide the interface |

### Imagery as you zoom in
The whole planet streams in detail as you zoom, anywhere you look. Below ~14,000 km a quadtree of
Web-Mercator tiles (zoom 5 to 16) carrying Sentinel-2 imagery on real terrain fades in over the base
map. Every frame the blurriest visible tile is split first, until each imagery texel covers about one
screen pixel or 900 tiles are on screen, so the detail always goes where you are looking: down to the
10 m source resolution (rendered up to ~2 m/px) from any altitude or tilt. Mountains, valleys and
coastlines get their real elevation and hillshading. A tile is only replaced by its four children once
all of them have loaded, so zooming never opens holes. Tiles are cached on disk (downloads that fail
are retried); the cache in memory keeps the 2,000 most recently used. Clouds fade out below a few
hundred km so they don't blur the ground, and the camera stays at least 40 m above the terrain under it.

With the cursor on the globe below 2,000 km, the map bar shows the ground elevation there. Code can ask
for it with `GlobeTiles.Instance.HeightAt(latDeg, lonDeg)` (metres above sea level, oceans 0). It is
exact once that area's elevation tile has loaded, coarser before that, and 0 if nothing nearby has
loaded yet. The upcoming aircraft simulation will use it for runways and terrain.

### Night
The night side is lit by what is really there:

- **City lights** from NASA's Black Marble 2016 (VIIRS), streamed as tiles down to ~600 m alongside the
  daytime imagery. Dim towns glow sodium orange and big city cores burn white. Close to the ground the
  lights follow the built-up areas in the daytime imagery and break into individual points, so a city
  from 2 km up is a field of lights, not a blur. Under thick cloud they are dimmed, and the clouds above
  them glow orange from below. In the haze, city light spreads into a glow over the ground and, from
  inside the atmosphere, a dome of light pollution.
- **The Moon** is at its real position, with its real size and phase (low-precision lunar theory,
  ~0.1°). Its light is blue-shifted the way night vision sees it and falls off steeply with the phase:
  on a full moon the land, snow and clouds show faintly and the sea has a moon glint. It also lights the
  clouds.
- **Twilight**: the ground keeps a little sky light for a while after sunset.
- **Airglow**: a faint green layer 95 km up that outlines the night limb, as in astronaut photos.
- **Aurora**: green curtains with red tops on ovals around the geomagnetic poles, ~67° magnetic latitude
  at midnight and ~76° towards noon, in both hemispheres. They drift and flicker. The level is
  `CameraRig.AuroraActivityLevel` (0 = none, 1 = active night, 2 = geomagnetic storm).
- **Lightning** flickers in thick tropical and mid-latitude storm clouds.

## What's simulated

- **Orbits**: CelesTrak mean elements with J2 and atmospheric drag. Operational satellites keep
  station; dead ones decay. Deployed satellites raise and phase into their slots over several days.
- **Falcon 9**: pressure-dependent Merlin thrust, Mach-dependent drag, a throttle bucket at max-Q,
  a gravity turn on stage 1, and closed-loop guidance on stage 2 with a coast and circularisation burn.
  For GTO the satellites raise their own orbit. Payload capacity comes from the rocket equation,
  calibrated against the flight model.
- **Booster recovery**: flip, three-engine entry burn, aerodynamic descent and a single-engine
  hoverslam computed by forward prediction. The droneship is parked exactly where the booster will
  come down.
- **Collisions**: NASA's statistical "cube" method; breakups follow the NASA Standard Breakup Model.

## Project layout

```
Assets/Spaced/
  Scripts/Runtime/
    Core/         constants, time, Earth orientation, Sun, floating origin (WorldFrame)
    Orbits/       orbital mechanics: Kozai→Brouwer, J2, drag, Kepler, state vectors, transfers
    Data/         CelesTrak client with a 2-hour disk cache and fallback groups
    Simulation/   catalog, Burst propagation, collisions, breakups, launches/cleanup, clock
    Launch/       Falcon 9 data, flight physics, booster landing, launch windows, mission manager, views
    Rendering/    Earth, atmosphere, satellites (dots + instanced 3D models), procedural models/textures, FX
    Globe/        whole-planet imagery + terrain tile streaming (quadtree LOD), elevation queries
    View/         two-scale camera rig, post-processing, picking
    UI/           IMGUI HUD
  Scripts/Editor/ Spaced menu (project setup, cache clearing)
  Resources/Shaders/   URP HLSL shaders (+ SpacedCommon.hlsl lighting)
  Tests/Editor/   orbital-math and launch tests (Window → General → Test Runner)
```

**Rendering at two scales.** Planet-scale things use 1 unit = 1000 km on the base camera. Rockets,
pads and satellite models use 1 unit = 1 m on an overlay camera (layer 30). Everything is placed
relative to a floating origin that follows what you look at, so close-ups don't jitter.

## Using your own models (Asset Store, NASA 3D Resources, …)

Every model in the game is generated in code, so the project needs no art assets. To use better
ones:

1. Import a model, e.g. from the Unity Asset Store (search "satellite", "space station") or a
   public-domain glTF from [NASA 3D Resources](https://science.nasa.gov/3d-resources/) (glTF needs the
   `com.unity.cloud.gltfast` package).
2. Select the `Spaced` GameObject in `Assets/Scenes/Spaced.unity`, and in **Satellite Models** add an
   entry: choose the *Kind* (Starlink, Station, Comsat…), assign the *Mesh* and *Materials*, and
   adjust *Rotation*/*Scale*. Model axes: +Y away from Earth, +Z along the direction of travel.

Standard URP Lit materials work: a directional light follows the real Sun.

## Data sources and credits

- Orbital elements: [CelesTrak](https://celestrak.org) GP data (OMM JSON). CelesTrak answers
  "403 — data has not updated since your last download" if the same group is fetched twice between
  its 2-hourly updates. The game caches every group and falls back to smaller groups when it's
  refused. Don't shorten the cache. **Spaced → Clear Downloaded Data Cache** forces a fresh download.
- Earth imagery: NASA Visible Earth: Blue Marble Next Generation (12 monthly images), Black Marble 2016
  night lights, and cloud cover. Public domain. To use your own, assign textures on `SpacedBootstrap`.
- Night lights tiles: NASA Black Marble 2016 (VIIRS) from [NASA GIBS](https://earthdata.nasa.gov/gibs),
  zoom 0–8. Cached in `persistentDataPath/night`.
- Moon: NASA LRO / LROC colour map, NASA Scientific Visualization Studio.
- High-resolution imagery: [Sentinel-2 cloudless 2016](https://s2maps.eu) by EOX IT Services GmbH
  (contains modified Copernicus Sentinel data 2016), CC BY 4.0. Streamed for the whole globe at up to
  10 m/px. Tiles are cached in `persistentDataPath/imagery`.
- Elevation: [Terrain Tiles on AWS](https://registry.opendata.aws/terrain-tiles/) (Mapzen "Terrarium"
  encoding; SRTM, GMTED2010, ETOPO1 and other sources). Cached in `persistentDataPath/terrain`.
- Falcon 9 figures: SpaceX Falcon User's Guide and public flight telemetry.
- Live clouds: GOES-East / GOES-West ABI and Himawari AHI band 13 (clean infrared), and NOAA-20 VIIRS
  corrected reflectance, from [NASA GIBS](https://earthdata.nasa.gov/gibs). Cached in
  `persistentDataPath/weather`.
- Forecasts and waves: [Open-Meteo](https://open-meteo.com) weather and marine APIs, CC BY 4.0.
