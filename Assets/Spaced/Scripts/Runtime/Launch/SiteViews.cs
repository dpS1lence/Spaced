using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// An Earth-fixed place in the local scene (launch pad, droneship). <see cref="Frame"/> is its east-north-up
    /// frame, re-placed every frame relative to the floating origin; particle systems simulate in it so smoke
    /// stays fixed to the ground. Detail is hidden when the camera is far away.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public abstract class EarthFixedView : MonoBehaviour
    {
        public double3 EcefKm { get; protected set; }
        public Transform Frame { get; private set; }
        protected GameObject Content { get; private set; }
        protected CameraRig Rig;
        public double VisibleRangeKm = 600;

        protected void CreateFrame(string name)
        {
            Frame = new GameObject(name + " Frame").transform;
            Frame.SetParent(transform, false);
            Content = new GameObject(name);
            Content.transform.SetParent(Frame, false);
        }

        protected virtual void LateUpdate()
        {
            if (Frame == null || Content == null) return;
            double jd = WorldFrame.JulianDate;
            double3 eci = Astro.EcefToEci(EcefKm, jd);
            Frame.SetPositionAndRotation(WorldFrame.ToLocal(eci), WorldFrame.SurfaceRotation(EcefKm, jd));
            bool visible = Rig == null || math.distance(Rig.CameraEciKm, eci) < VisibleRangeKm;
            if (Content.activeSelf != visible) Content.SetActive(visible);
        }

        /// <summary>Earth-fixed point offset from this place by east/north/up metres.</summary>
        public double3 OffsetEcefKm(double eastM, double northM, double upM)
        {
            Astro.EnuBasis(EcefKm, out double3 east, out double3 north, out double3 up);
            return EcefKm + (east * eastM + north * northM + up * upM) / 1000.0;
        }

        protected static GameObject AddMesh(Transform parent, SurfaceMesh mesh, MaterialLibrary materials, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh.Mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = materials.For(mesh);
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return go;
        }

        protected static GameObject AddGround(Transform parent, Shader shader, float radius, float shoreline, bool allSea, float y)
        {
            var go = new GameObject(allSea ? "Sea" : "Ground");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = SiteModels.GroundDisc(radius, 90, 128, y);
            var meshRenderer = go.AddComponent<MeshRenderer>();
            var material = new Material(shader) { name = go.name };
            material.SetFloat("_Shoreline", shoreline);
            material.SetFloat("_Radius", radius);
            material.SetFloat("_AllSea", allSea ? 1f : 0f);
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }
    }

    /// <summary>
    /// A SpaceX launch complex: pad, towers, propellant farm, surrounding land and sea, liftoff steam. At Starbase,
    /// the orbital launch mount and the catch tower, whose arms ride up and down and close around a returning
    /// Super Heavy.
    /// </summary>
    public sealed class LaunchSiteView : EarthFixedView
    {
        public LaunchSite Site { get; private set; }
        Material _groundMaterial;
        bool _imageryRequested;
        Transform _strongback;
        Transform _carriage, _armLeft, _armRight;
        readonly System.Collections.Generic.List<ParticleSystem> _steam = new System.Collections.Generic.List<ParticleSystem>();
        float _armHeight = 100f, _armHeightTarget = 100f, _armClosed, _armClosedTarget;

        public void Init(LaunchSite site, CameraRig rig, MaterialLibrary materials, Shader groundShader, Fx fx)
        {
            Site = site;
            Rig = rig;
            EcefKm = Astro.GeoToEcef(site.LatitudeDeg, site.LongitudeDeg, 0);
            CreateFrame(site.ShortName);
            bool starbase = site.Style == PadStyle.Starbase;
            float ground = starbase ? 0f : SiteModels.GroundLevel;

            _groundMaterial = AddGround(Content.transform, groundShader, 9000f, site.ShorelineOffsetM, false, ground - 0.6f)
                .GetComponent<MeshRenderer>().sharedMaterial;
            if (starbase)
            {
                AddMesh(Content.transform, SiteModels.StarbasePad(), materials, "Pad");
                BuildArms(materials);
                // Deluge steam blasts out sideways from under the launch mount.
                foreach (float heading in new[] { 90f, 270f, 0f })
                {
                    var vent = new GameObject("Deluge").transform;
                    vent.SetParent(Content.transform, false);
                    vent.localPosition = new Vector3(0, 3f, 0);
                    vent.localRotation = Quaternion.Euler(0, heading, 0) * Quaternion.Euler(-85f, 0, 0);
                    _steam.Add(fx.CreateSmoke(vent, Frame, fx.Smoke, 18f, 40f, 75f, 1800, new Color(0.95f, 0.95f, 0.95f, 1f), 7f));
                }
            }
            else
            {
                AddMesh(Content.transform, SiteModels.Pad(site.Style), materials, "Pad");
                _strongback = new GameObject("Strongback Hinge").transform;
                _strongback.SetParent(Content.transform, false);
                _strongback.localPosition = new Vector3(0, SiteModels.GroundLevel + 1f, -4.8f);
                var arm = AddMesh(_strongback, SiteModels.Strongback(), materials, "Strongback");
                arm.transform.localPosition = new Vector3(0, -SiteModels.GroundLevel - 1f, 4.8f) + new Vector3(0, SiteModels.GroundLevel, 0);

                // Steam and smoke billow out of the flame trench at liftoff.
                var trench = new GameObject("Flame Trench").transform;
                trench.SetParent(Content.transform, false);
                trench.localPosition = new Vector3(0, SiteModels.GroundLevel + 2f, 50f);
                trench.localRotation = Quaternion.Euler(0, site.FlameTrenchHeadingDeg, 0) * Quaternion.Euler(-80f, 0, 0);
                _steam.Add(fx.CreateSmoke(trench, Frame, fx.Smoke, 18f, 45f, 70f, 2500, new Color(0.95f, 0.95f, 0.95f, 1f), 9f));
            }
            Fx.SetLayer(gameObject);
        }

        void BuildArms(MaterialLibrary materials)
        {
            Vector3 toMount = (-SiteModels.StarbaseTower).normalized;
            _carriage = new GameObject("Arm Carriage").transform;
            _carriage.SetParent(Content.transform, false);
            _carriage.localRotation = Quaternion.LookRotation(toMount);
            AddMesh(_carriage, SiteModels.ArmCarriage(), materials, "Carriage");
            foreach (float side in new[] { -1f, 1f })
            {
                var hinge = new GameObject(side < 0 ? "Left Arm" : "Right Arm").transform;
                hinge.SetParent(_carriage, false);
                hinge.localPosition = new Vector3(side * SiteModels.ArmSpacing, 0, 1.6f);
                AddMesh(hinge, SiteModels.Arm(), materials, "Arm");
                if (side < 0) _armLeft = hinge; else _armRight = hinge;
            }
            PlaceArms();
        }

        void PlaceArms()
        {
            if (_carriage == null) return;
            Vector3 toMount = (-SiteModels.StarbaseTower).normalized;
            _carriage.localPosition = SiteModels.StarbaseTower + toMount * 5.6f + Vector3.up * _armHeight;
            float open = Mathf.Lerp(38f, 0f, _armClosed);
            _armLeft.localRotation = Quaternion.Euler(0, -open, 0);
            _armRight.localRotation = Quaternion.Euler(0, open, 0);
        }

        /// <summary>
        /// Moves the arms: <paramref name="heightM"/> above the ground, <paramref name="closed"/> 0 = open wide,
        /// 1 = closed around the booster. They travel at a few metres per second.
        /// </summary>
        public void SetArms(float heightM, float closed)
        {
            _armHeightTarget = heightM;
            _armClosedTarget = Mathf.Clamp01(closed);
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            if (Content == null || _groundMaterial == null) return;
            if (_carriage != null)
            {
                float dt = Time.deltaTime;
                _armHeight = Mathf.MoveTowards(_armHeight, _armHeightTarget, 6f * dt);
                _armClosed = Mathf.MoveTowards(_armClosed, _armClosedTarget, 0.35f * dt);
                PlaceArms();
            }
            // Fetch the real ~10 m imagery of the complex the first time anyone looks at it (≈80 small tiles, cached).
            if (!_imageryRequested && Content.activeSelf)
            {
                _imageryRequested = true;
                StartCoroutine(new ImageryMosaic().Load(Site.LatitudeDeg, Site.LongitudeDeg, 14, 9, 9, mosaic =>
                {
                    _groundMaterial.SetTexture("_ImageryTex", mosaic.Texture);
                    _groundMaterial.SetVector("_ImageryMapping", mosaic.LocalMapping(Site.LatitudeDeg, Site.LongitudeDeg));
                    _groundMaterial.SetFloat("_HasImagery", 1f);
                }));
            }
        }

        /// <summary>0 = strongback against the rocket, 1 = fully retracted (tilted ~2° away, as before launch).</summary>
        public void SetStrongback(float retracted)
        {
            if (_strongback != null) _strongback.localRotation = Quaternion.Euler(-2.2f * Mathf.Clamp01(retracted), 0, 0);
        }

        public void Steam(float rate, double warp)
        {
            foreach (var steam in _steam) Fx.Drive(steam, rate, warp);
        }
    }

    /// <summary>The droneship waiting downrange, on its patch of ocean.</summary>
    public sealed class DroneshipView : EarthFixedView
    {
        public string ShipName { get; private set; }
        ParticleSystem _spray;

        public void Init(string shipName, double3 ecefKm, CameraRig rig, MaterialLibrary materials, Shader groundShader, Fx fx)
        {
            ShipName = shipName;
            Rig = rig;
            EcefKm = ecefKm;
            CreateFrame(shipName);
            AddGround(Content.transform, groundShader, 7000f, 0f, true, -3.2f);
            AddMesh(Content.transform, SiteModels.Droneship(), materials, "Droneship");
            var deck = new GameObject("Deck").transform;
            deck.SetParent(Content.transform, false);
            deck.localPosition = new Vector3(0, 1f, 0);
            deck.localRotation = Quaternion.Euler(180f, 0, 0); // flip the emitter so it sprays upward and outward
            _spray = fx.CreateSmoke(deck, Frame, fx.Smoke, 6f, 14f, 35f, 1200, new Color(0.9f, 0.9f, 0.9f, 0.9f), 6f);
            var shape = _spray.shape;
            shape.angle = 80f;
            Fx.SetLayer(gameObject);
        }

        public void Spray(float rate, double warp) => Fx.Drive(_spray, rate, warp);
    }
}
