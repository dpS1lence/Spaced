using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Spaced
{
    /// <summary>
    /// The superhero's body: a simple procedural figure (about 1.85 m) in the local scene with jointed arms, legs and
    /// head and a cloth-like cape. <see cref="HeroController"/> says where he is and how he is flying; this places and
    /// poses him after the camera rig has moved the floating origin, so he never lags the view.
    /// <para>Body space: +Y towards the head, +Z the way he faces, origin at the hips.</para>
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class HeroModel : MonoBehaviour
    {
        const int CapeColumns = 7, CapeRows = 12;
        const float CapeLength = 1.15f, CapeTopWidth = 0.44f, CapeBottomWidth = 0.66f;
        static readonly Vector3 CapeTop = new Vector3(0f, 0.49f, -0.13f);

        /// <summary>Hips, inertial (ECI) km.</summary>
        public double3 PositionKm;
        public Quaternion Rotation = Quaternion.identity;
        /// <summary>0 hovering upright … 1 the flying pose.</summary>
        public float Fly;
        /// <summary>How hard the air is pulling at the cape (0–1).</summary>
        public float Speed01;
        /// <summary>World direction the cape trails in (down when still, behind him when flying).</summary>
        public Vector3 CapeFlowWorld = Vector3.down;

        MaterialLibrary _materials;
        Transform _armLeft, _armRight, _legLeft, _legRight, _head;
        Mesh _cape;
        Vector3[] _capeVertices;
        Vector3 _capeFlow = new Vector3(0f, -1f, -0.2f);

        public void Init(MaterialLibrary materials)
        {
            _materials = materials;

            var torso = new MeshBuilder();
            torso.Cylinder(new Vector3(0, 0.02f, 0), new Vector3(0, 0.52f, 0), 0.16f, 0.205f, 16, Surface.HeroSuit);
            torso.Cylinder(new Vector3(0, -0.13f, 0), new Vector3(0, 0.03f, 0), 0.15f, 0.16f, 16, Surface.HeroCape);
            torso.Cylinder(new Vector3(0, -0.005f, 0), new Vector3(0, 0.055f, 0), 0.166f, 0.166f, 16, Surface.HeroBelt);
            Sphere(torso, new Vector3(-0.2f, 0.47f, 0), 0.075f, Surface.HeroSuit);
            Sphere(torso, new Vector3(0.2f, 0.47f, 0), 0.075f, Surface.HeroSuit);
            torso.Cylinder(new Vector3(0, 0.5f, 0), new Vector3(0, 0.63f, 0), 0.052f, 0.048f, 10, Surface.HeroSkin);
            // Flattened front to back, as a body is.
            Part("Torso", torso, transform, Vector3.zero).localScale = new Vector3(1f, 1f, 0.72f);

            var head = new MeshBuilder();
            Sphere(head, Vector3.zero, 0.105f, Surface.HeroSkin);
            Sphere(head, new Vector3(0, 0.022f, -0.014f), 0.11f, Surface.HeroHair, 0f, 105f);
            head.Box(new Vector3(0, -0.01f, 0.1f), new Vector3(0.025f, 0.04f, 0.03f), Surface.HeroSkin);
            _head = Part("Head", head, transform, new Vector3(0, 0.74f, 0.005f));
            _head.localScale = new Vector3(0.92f, 1.08f, 1f);

            var arm = new MeshBuilder();
            arm.Cylinder(Vector3.zero, new Vector3(0, -0.3f, 0), 0.058f, 0.05f, 10, Surface.HeroSuit);
            Sphere(arm, new Vector3(0, -0.3f, 0), 0.05f, Surface.HeroSuit);
            arm.Cylinder(new Vector3(0, -0.3f, 0), new Vector3(0, -0.56f, 0), 0.05f, 0.043f, 10, Surface.HeroSuit);
            Sphere(arm, new Vector3(0, -0.605f, 0), 0.052f, Surface.HeroSkin);
            _armLeft = Part("Left Arm", arm, transform, new Vector3(-0.245f, 0.47f, 0));
            _armRight = Part("Right Arm", arm, transform, new Vector3(0.245f, 0.47f, 0));

            var leg = new MeshBuilder();
            leg.Cylinder(Vector3.zero, new Vector3(0, -0.43f, 0), 0.085f, 0.064f, 12, Surface.HeroSuit);
            Sphere(leg, new Vector3(0, -0.43f, 0), 0.062f, Surface.HeroSuit);
            leg.Cylinder(new Vector3(0, -0.43f, 0), new Vector3(0, -0.6f, 0), 0.062f, 0.055f, 12, Surface.HeroSuit);
            leg.Cylinder(new Vector3(0, -0.6f, 0), new Vector3(0, -0.85f, 0), 0.058f, 0.05f, 12, Surface.HeroCape);
            leg.Box(new Vector3(0, -0.865f, 0.045f), new Vector3(0.09f, 0.05f, 0.22f), Surface.HeroCape);
            _legLeft = Part("Left Leg", leg, transform, new Vector3(-0.095f, -0.09f, 0));
            _legRight = Part("Right Leg", leg, transform, new Vector3(0.095f, -0.09f, 0));

            BuildCape();
            Fx.SetLayer(gameObject);
        }

        /// <summary>A sphere (or a cap of one, from the bottom at 0° to the top at 180°) around <paramref name="center"/>.</summary>
        static void Sphere(MeshBuilder builder, Vector3 center, float radius, Surface surface, float fromDeg = 0f, float toDeg = 180f)
        {
            var profile = new List<Vector2>();
            for (int i = 0; i <= 8; i++)
            {
                float a = Mathf.Lerp(fromDeg, toDeg, i / 8f) * Mathf.Deg2Rad;
                profile.Add(new Vector2(-radius * Mathf.Cos(a), Mathf.Max(radius * Mathf.Sin(a), 1e-4f)));
            }
            builder.Lathe(center, Vector3.up, profile, 14, surface);
        }

        Transform Part(string partName, MeshBuilder builder, Transform parent, Vector3 localPosition)
        {
            var mesh = builder.Build(partName);
            var go = new GameObject(partName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.AddComponent<MeshFilter>().sharedMesh = mesh.Mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = _materials.For(mesh);
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return go.transform;
        }

        void BuildCape()
        {
            _capeVertices = new Vector3[CapeColumns * CapeRows];
            var uvs = new Vector2[_capeVertices.Length];
            var triangles = new List<int>();
            for (int r = 0; r < CapeRows; r++)
                for (int c = 0; c < CapeColumns; c++)
                {
                    uvs[r * CapeColumns + c] = new Vector2(c / (CapeColumns - 1f), r / (CapeRows - 1f));
                    if (r == CapeRows - 1 || c == CapeColumns - 1) continue;
                    int i = r * CapeColumns + c;
                    triangles.AddRange(new[] { i, i + CapeColumns, i + 1, i + 1, i + CapeColumns, i + CapeColumns + 1 });
                }
            _cape = new Mesh { name = "Cape" };
            _cape.MarkDynamic();
            UpdateCape(0f, 0f);
            _cape.vertices = _capeVertices;
            _cape.uv = uvs;
            _cape.SetTriangles(triangles, 0);
            _cape.RecalculateNormals();
            var go = new GameObject("Cape");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _cape;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _materials[Surface.HeroCape];
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        void LateUpdate()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f), time = Time.unscaledTime;
            transform.SetPositionAndRotation(WorldFrame.ToLocal(PositionKm), Rotation);

            // Hover: arms loose by the sides, legs dangling and swaying. Flying: right fist out in front (above the
            // head in body space), left arm along the side, legs trailing, head raised to look ahead.
            float f = Mathf.SmoothStep(0f, 1f, Fly);
            float sway = Mathf.Sin(time * 1.3f), breathe = Mathf.Sin(time * 2.1f);
            _armRight.localRotation = Quaternion.Slerp(Quaternion.Euler(4f + breathe * 2f, 0f, 9f), Quaternion.Euler(-172f, 0f, -6f), f);
            _armLeft.localRotation = Quaternion.Slerp(Quaternion.Euler(4f - breathe * 2f, 0f, -9f), Quaternion.Euler(12f, 0f, -7f), f);
            _legRight.localRotation = Quaternion.Slerp(Quaternion.Euler(6f + sway * 5f, 0f, 3f), Quaternion.Euler(-4f + sway * 2f, 0f, 2f), f);
            _legLeft.localRotation = Quaternion.Slerp(Quaternion.Euler(-3f - sway * 5f, 0f, -3f), Quaternion.Euler(9f - sway * 2f, 0f, -2f), f);
            _head.localRotation = Quaternion.Slerp(Quaternion.Euler(breathe, 0f, 0f), Quaternion.Euler(-55f, 0f, 0f), f);

            // The cape trails where the air pushes it, but never through his back.
            Vector3 flow = Quaternion.Inverse(Rotation) * CapeFlowWorld;
            flow.z = Mathf.Min(flow.z, -0.18f);
            _capeFlow = Vector3.Slerp(_capeFlow, flow.normalized, 1f - Mathf.Exp(-dt * 6f));
            UpdateCape(time, Speed01);
            _cape.vertices = _capeVertices;
            _cape.RecalculateNormals();
            _cape.RecalculateBounds();
        }

        void UpdateCape(float time, float speed01)
        {
            Vector3 flow = _capeFlow.normalized;
            Vector3 side = Vector3.right;
            Vector3 normal = Vector3.Cross(side, flow).normalized;
            if (Vector3.Dot(normal, Vector3.back) < 0) normal = -normal; // bows away from his back
            float flapRate = 3f + 13f * speed01, flapSize = 0.025f + 0.11f * speed01;
            for (int r = 0; r < CapeRows; r++)
            {
                float t = r / (CapeRows - 1f);
                float width = Mathf.Lerp(CapeTopWidth, CapeBottomWidth, t);
                for (int c = 0; c < CapeColumns; c++)
                {
                    float u = c / (CapeColumns - 1f) - 0.5f;
                    float wave = Mathf.Sin(time * flapRate - t * 7f + u * 2.2f) + 0.4f * Mathf.Sin(time * flapRate * 1.7f - t * 11f - u * 3f);
                    _capeVertices[r * CapeColumns + c] = CapeTop + side * (u * width) + flow * (t * CapeLength)
                                                         + normal * (wave * flapSize * t + 0.05f * t * (1f - t) * 4f);
                }
            }
        }
    }
}
