// Physically based single scattering (Rayleigh + Mie) through a 100 km atmosphere, ray-marched per pixel.
// Gives the blue limb from orbit, blue sky and red sunsets from the launch pad, and haze over the planet.
// Drawn on the back faces of a shell so it works from inside and outside; ZTest Always with the ray
// clipped analytically at the ground, so the haze also lies over the surface, and at the scene depth, so it
// stops at mountains (and anything else) in front of sea level.
// The night side adds light of its own: city glow scattered in the haze, the thin green airglow layer that
// outlines the night limb, and the aurora ovals around the geomagnetic poles (the shell reaches 320 km).
Shader "Spaced/Atmosphere"
{
    Properties
    {
        _SunIntensity ("Sun Intensity", Float) = 22
        _MieG ("Mie Anisotropy", Range(0, 0.99)) = 0.76
        _Airglow ("Airglow", Float) = 0.3
        _Aurora ("Aurora", Float) = 2.2
        _CityGlow ("City glow in haze", Float) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-100" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Atmosphere"
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "SpacedCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SunIntensity;
                float _MieG;
                float _Airglow;
                float _Aurora;
                float _CityGlow;
            CBUFFER_END

            TEXTURE2D(_SpacedNightTex);
            SAMPLER(sampler_SpacedNightTex);

            // |camera - centre|² - R² for the planet and the atmosphere top, from the CPU in double precision.
            float _SpacedCamPlanetCFar;
            float _SpacedCamAtmosphereCFar;

            // Units: 1 = 1000 km.
            static const float AtmosphereHeight = 0.1;
            static const float RayleighHeight = 0.008;
            static const float MieHeight = 0.0012;
            static const float3 RayleighBeta = float3(5.8, 13.5, 33.1);
            static const float MieBeta = 21.0;
            static const int ViewSamples = 16;
            static const int LightSamples = 6;
            static const int AuroraSamples = 48;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                return output;
            }

            // Optical depth (Rayleigh, Mie) from p towards the Sun; returns false if the planet blocks it.
            bool LightDepth(float3 p, float3 l, float top, out float2 depth)
            {
                depth = 0;
                float b = dot(p, l);
                float c = dot(p, p) - top * top;
                float pathLength = -b + sqrt(max(b * b - c, 0.0));
                float ds = pathLength / LightSamples;
                for (int i = 0; i < LightSamples; i++)
                {
                    float3 q = p + l * (ds * (i + 0.5));
                    float h = sqrt(dot(q, q)) - _SpacedEarthRadiusFar;
                    if (h < 0) return false;
                    depth += float2(exp(-h / RayleighHeight), exp(-h / MieHeight)) * ds;
                }
                return true;
            }

            // City light at the ground below a point (blurred: it is what the haze scatters).
            float3 CityGlowBelow(float3 pointFromCenter)
            {
                float3 earthDir = normalize(mul((float3x3)_SpacedWorldToEarth, pointFromCenter));
                float3 texel = SAMPLE_TEXTURE2D_LOD(_SpacedNightTex, sampler_SpacedNightTex, SpacedDirectionToUv(earthDir), 5.0).rgb;
                return SpacedCityLights(texel) * SpacedNightAmount(dot(normalize(pointFromCenter), _SpacedSunDir));
            }

            // Airglow: a faint layer of green light (atomic oxygen, 557.7 nm) ~95 km up, ±4 km. Seen edge-on at the
            // night limb the path through it is ~30× longer than looking straight down, so it becomes the thin
            // glowing line astronauts photograph. Integrated analytically at the ray's crossings of the layer.
            float3 Airglow(float3 origin, float3 direction, float tMax)
            {
                float r0 = _SpacedEarthRadiusFar + 0.095, sigma = 0.004;
                float b = dot(direction, origin);
                float d2 = max(dot(origin, origin) - b * b, 0.0);   // squared distance of the ray's line from the centre
                float d = sqrt(d2);
                if (d > r0 + 4.0 * sigma) return 0;
                float disc = max(r0 * r0 - d2, 0.0);
                float path = sigma * 2.5066 * r0 / sqrt(disc + 2.0 * r0 * sigma);
                float grazing = exp(-0.5 * pow(max(d - r0, 0.0) / sigma, 2.0));
                float root = sqrt(disc);
                float glow = 0;
                for (int i = 0; i < 2; i++)
                {
                    float t = -b + (i == 0 ? -root : root);
                    if (t < 0 || t > tMax) continue;
                    float3 p = origin + direction * t;
                    // Beyond the tangent point the light crosses the dense low air: rays grazing low are dimmed.
                    float extinction = i == 0 ? 1.0 : 1.0 - exp(-max(d - _SpacedEarthRadiusFar, 0.0) / 0.012);
                    glow += path * grazing * extinction * SpacedNightAmount(dot(normalize(p), _SpacedSunDir) + 0.1);
                    if (root < 1e-5) break;
                }
                return float3(0.35, 1.0, 0.48) * glow * _Airglow;
            }

            // Aurora: curtains of green (oxygen, 557.7 nm, ~100–150 km) topped with red (630 nm, 200–300 km) along an
            // oval ~67° geomagnetic latitude on the night side and ~76° towards noon, in both hemispheres.
            // Brightness varies along the oval in rays and folds that drift slowly.
            float3 Aurora(float3 origin, float3 direction, float tMax, float jitter)
            {
                float activity = _SpacedAuroraActivity;
                if (activity <= 0) return 0;
                float R = _SpacedEarthRadiusFar;
                float outer = R + 0.32;
                float b = dot(direction, origin);
                float disc = b * b - (dot(origin, origin) - outer * outer);
                if (disc <= 0) return 0;
                float s = sqrt(disc);
                float t0 = max(-b - s, 0.0), t1 = min(-b + s, tMax);
                if (t1 <= t0) return 0;

                float3 axis = _SpacedMagneticAxis;
                // Skip rays that stay well clear of auroral latitudes (checked at the ends, middle and closest approach).
                float tc = clamp(-b, t0, t1);
                float reach = max(max(abs(dot(normalize(origin + direction * t0), axis)), abs(dot(normalize(origin + direction * t1), axis))),
                                  max(abs(dot(normalize(origin + direction * (0.5 * (t0 + t1))), axis)), abs(dot(normalize(origin + direction * tc), axis))));
                if (reach < 0.74) return 0;   // sin 48°

                float3 midnight = -_SpacedSunDir - axis * dot(-_SpacedSunDir, axis);
                midnight = normalize(midnight + 1e-5);
                float3 dawn = cross(axis, midnight);
                float time = _Time.y;

                float ds = (t1 - t0) / AuroraSamples;
                float3 sum = 0;
                for (int i = 0; i < AuroraSamples; i++)
                {
                    float3 p = origin + direction * (t0 + ds * (i + jitter));
                    float r = length(p);
                    float h = (r - R) * 1000.0; // km
                    if (h < 85.0) continue;
                    float3 u = p / r;
                    float sinLat = dot(u, axis);
                    float magLat = abs(asin(sinLat)) * (180.0 / PI);
                    if (magLat < 52.0) continue;
                    float3 equator = u - axis * sinLat;
                    float mlt = atan2(dot(equator, dawn), dot(equator, midnight));   // 0 at magnetic midnight
                    float hemisphere = sinLat > 0 ? 0.0 : 31.7;

                    // The oval: further from the pole at midnight and when active; wavy, slowly drifting edges.
                    float center = 71.5 - 4.5 * cos(mlt) - (activity - 1.0) * 3.5
                                   + 1.3 * sin(mlt * 4.0 + time * 0.03 + hemisphere)
                                   + (SpacedNoise1(mlt * 9.0 + time * 0.05 + hemisphere) - 0.5) * 2.2;
                    float width = (1.0 + 0.7 * activity) * (0.6 + 0.4 * cos(mlt * 0.5));
                    float offset = (magLat - center) / width;
                    float band = exp(-offset * offset);
                    if (band < 0.01) continue;

                    // Inside the oval the light comes in thin sheets (curtains) a few tens of km apart, folded
                    // along the oval; along each sheet, rays flicker slowly.
                    float fold = (magLat - center) / 0.45 + 1.6 * SpacedNoise1(mlt * 7.0 + time * 0.02 + hemisphere);
                    float sheets = pow(0.5 + 0.5 * cos(fold * 6.2832), 6.0);
                    band *= 0.25 + 0.75 * sheets;
                    float rays = SpacedNoise1(mlt * 160.0 + time * 0.25 + hemisphere);
                    rays = 0.15 + 0.85 * rays * rays * rays * (0.5 + SpacedNoise1(mlt * 23.0 - time * 0.12));

                    float green = smoothstep(92.0, 108.0, h) * (h < 125.0 ? 1.0 : exp(-(h - 125.0) / 38.0));
                    float red = exp(-pow((h - 230.0) / 55.0, 2.0)) * 0.28;
                    float violet = exp(-pow((h - 98.0) / 5.0, 2.0)) * 0.25;
                    float dark = SpacedNightAmount(dot(u, _SpacedSunDir) + 0.15);
                    float3 emission = float3(0.12, 1.0, 0.42) * green + float3(1.0, 0.12, 0.28) * red + float3(0.55, 0.25, 1.0) * violet;
                    sum += emission * band * rays * dark * ds;
                }
                return sum * _Aurora * activity;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 origin = _WorldSpaceCameraPos - _SpacedEarthCenterFar;
                float3 direction = normalize(input.positionWS - _WorldSpaceCameraPos);
                float3 l = _SpacedSunDir;
                float planet = _SpacedEarthRadiusFar;
                float top = planet + AtmosphereHeight;
                float b = dot(direction, origin);

                // Distance to the first solid thing along the ray: the ground, or geometry in the depth buffer
                // (terrain above sea level, satellites).
                float tSolid = 1e9;
                bool hitsGround = false;
                float cPlanet = _SpacedCamPlanetCFar;
                float discPlanet = b * b - cPlanet;
                if (cPlanet > 0 && b < 0 && discPlanet > 0)
                {
                    tSolid = cPlanet / (-b + sqrt(discPlanet));
                    hitsGround = true;
                }
                float rawDepth = SampleSceneDepth(input.positionCS.xy / _ScaledScreenParams.xy);
                #if UNITY_REVERSED_Z
                bool hasGeometry = rawDepth > 0.0;
                #else
                bool hasGeometry = rawDepth < 1.0;
                #endif
                if (hasGeometry)
                {
                    float3 forward = -UNITY_MATRIX_V[2].xyz;
                    float tScene = LinearEyeDepth(rawDepth, _ZBufferParams) / max(dot(direction, forward), 1e-4);
                    if (tScene < tSolid)
                    {
                        tSolid = tScene;
                        hitsGround = length(origin + direction * tScene) < planet + 0.012;
                    }
                }

                // Single scattering of sunlight.
                float3 color = 0;
                float alpha = 0;
                float cTop = _SpacedCamAtmosphereCFar;
                float discTop = b * b - cTop;
                if (discTop > 0 && !(cTop > 0 && b >= 0))
                {
                    float root = sqrt(discTop);
                    float tNear = cTop > 0 ? cTop / (-b + root) : 0.0;   // stable near root; 0 inside the atmosphere
                    float tFar = min(-b + root, tSolid);
                    if (tFar > tNear)
                    {
                        float ds = (tFar - tNear) / ViewSamples;
                        float2 viewDepth = 0;
                        float3 rayleigh = 0, mie = 0;
                        for (int i = 0; i < ViewSamples; i++)
                        {
                            float3 p = origin + direction * (tNear + ds * (i + 0.5));
                            float h = max(sqrt(dot(p, p)) - planet, 0.0);
                            float2 density = float2(exp(-h / RayleighHeight), exp(-h / MieHeight)) * ds;
                            viewDepth += density;

                            float2 lightDepth;
                            if (!LightDepth(p, l, top, lightDepth)) continue;
                            float3 tau = RayleighBeta * (viewDepth.x + lightDepth.x) + MieBeta * 1.1 * (viewDepth.y + lightDepth.y);
                            float3 attenuation = exp(-tau);
                            rayleigh += density.x * attenuation;
                            mie += density.y * attenuation;
                        }

                        float mu = dot(direction, l);
                        float g = _MieG;
                        float phaseR = 3.0 / (16.0 * PI) * (1.0 + mu * mu);
                        float phaseM = 3.0 / (8.0 * PI) * ((1.0 - g * g) * (1.0 + mu * mu)) / ((2.0 + g * g) * pow(max(1.0 + g * g - 2.0 * g * mu, 1e-4), 1.5));
                        color = _SunIntensity * (rayleigh * RayleighBeta * phaseR + mie * MieBeta * phaseM);

                        // Extinction along the view ray hides what lies behind thick air (stars by day, the far limb).
                        float3 transmittance = exp(-(RayleighBeta * viewDepth.x + MieBeta * 1.1 * viewDepth.y));
                        float haze = 1.0 - dot(transmittance, float3(1, 1, 1) / 3.0);
                        float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
                        alpha = saturate(max(haze, luminance * 2.5));

                        // Light pollution: city light scattered by the haze over the ground it comes from and, from
                        // inside the atmosphere, as a glow dome around you.
                        float3 glow = 0;
                        if (hitsGround) glow += CityGlowBelow(origin + direction * tFar);
                        if (cTop <= 0) glow += CityGlowBelow(origin) * 0.35 * saturate(1.0 - dot(direction, normalize(origin)) * 0.7);
                        color += glow * haze * _CityGlow;
                    }
                }

                // Light the night sky makes itself (added on top; it doesn't hide anything).
                float jitter = frac(52.9829189 * frac(dot(input.positionCS.xy, float2(0.06711056, 0.00583715)))); // interleaved gradient noise
                float3 emission = Airglow(origin, direction, tSolid) + Aurora(origin, direction, tSolid, jitter);
                return half4(color + emission, alpha);
            }
            ENDHLSL
        }
    }
}
