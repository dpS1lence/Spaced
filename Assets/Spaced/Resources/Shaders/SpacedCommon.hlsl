// Shared lighting for Spaced shaders. All globals are set once per frame by CameraRig.
#ifndef SPACED_COMMON_INCLUDED
#define SPACED_COMMON_INCLUDED

float3 _SpacedSunDir;            // unit vector towards the Sun (Unity world axes)
float _SpacedSunIntensity;
// 1 = clouds as normal, 0 = hidden (they fade out as you zoom in to inspect the ground).
float _SpacedCloudFade;

// Local scene (metres): Earth centre relative to the floating origin, radius, and
// |camera - centre|² - R² computed in double precision on the CPU (float would cancel catastrophically).
float3 _SpacedEarthCenterLocal;
float _SpacedEarthRadiusLocal;
float _SpacedCamEarthCLocal;

// Cinematic lights for the local scene, set while a launch is being watched:
// a key light (pad floodlights / camera fill) and two engine glows (booster and second stage).
float4 _SpacedKeyLight;      // xyz: unit direction towards the light, w: intensity
float3 _SpacedKeyLightColor;
float4 _SpacedGlow0;         // xyz: position (local metres), w: intensity
float4 _SpacedGlow1;
float3 _SpacedGlowColor;

// Ground lighting shared by the base globe and the terrain tiles: direct sunlight and blue-sky fill.
#define SPACED_SUN_LIGHT 0.8
#define SPACED_SKY_LIGHT 0.22

// Far scene (1 unit = 1000 km).
float3 _SpacedEarthCenterFar;
float _SpacedEarthRadiusFar;

// ── Night side ──
float3 _SpacedMoonDir;           // unit vector towards the Moon (world axes)
float _SpacedMoonLight;          // moonlight relative to sunlight, phase included
float4x4 _SpacedWorldToEarth;    // world → Earth-fixed axes (Y = north pole, as the base globe)
float3 _SpacedMagneticAxis;      // north geomagnetic pole (world axes)
float _SpacedAuroraActivity;     // 0 none, 1 active night, 2 storm

float2 SpacedDirectionToUv(float3 d)
{
    return float2(atan2(d.z, d.x) / (2.0 * PI) + 0.5, asin(clamp(d.y, -1.0, 1.0)) / PI + 0.5);
}

float SpacedHash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float2 SpacedHash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

/// Smooth 1-D value noise in [0, 1].
float SpacedNoise1(float x)
{
    float i = floor(x), f = frac(x);
    float a = SpacedHash12(float2(i, 17.0)), b = SpacedHash12(float2(i + 1.0, 17.0));
    return lerp(a, b, f * f * (3.0 - 2.0 * f));
}

// How dark it is where the Sun stands at this cosine above the horizon: 0 in daylight, 1 once it has set.
float SpacedNightAmount(float sunNdl) { return 1.0 - smoothstep(-0.08, 0.06, sunNdl); }

// Emitted city light (HDR) from a NASA Black Marble texel (sampled as sRGB, so already linear), which has a
// faint blue ground baked in. Sparse and dim lights are sodium orange; bright city cores are whiter (LEDs,
// mixed sources) and bloom.
float3 SpacedCityLights(float3 texel)
{
    float3 light = max(texel - float3(0.004, 0.006, 0.016), 0.0);
    float l = dot(light, float3(0.30, 0.55, 0.15));
    float3 tint = lerp(float3(1.0, 0.45, 0.12), float3(1.0, 0.80, 0.58), saturate(l * 2.2));
    return tint * l * 1.5;
}

// Light on the ground where the Sun is down: moonlight (blue-shifted, as eyes see it at night) with its glint
// on water, the afterglow of twilight from the sky, and a trace of starlight and airglow.
float3 SpacedNightGround(float3 albedo, float3 n, float3 sphereN, float3 v, float ocean, float sunNdl)
{
    float night = SpacedNightAmount(sunNdl);
    float moonUp = saturate(dot(sphereN, _SpacedMoonDir) * 6.0 + 0.15);
    float moon = saturate(dot(n, _SpacedMoonDir)) * moonUp * _SpacedMoonLight;
    float3 color = albedo * float3(0.60, 0.70, 1.0) * moon * 2.6;
    float3 h = normalize(_SpacedMoonDir + v);
    color += ocean * pow(saturate(dot(sphereN, h)), 900.0) * moonUp * _SpacedMoonLight * float3(0.75, 0.82, 1.0) * 6.0;
    color += albedo * float3(0.50, 0.58, 0.85) * 0.006;

    // Civil to nautical twilight: the sky keeps lighting the ground for a while after sunset.
    float twilight = saturate(1.0 + sunNdl / 0.22);
    color += albedo * float3(0.42, 0.48, 0.85) * 0.06 * twilight * twilight * twilight * smoothstep(0.04, -0.02, sunNdl);
    return color * night;
}

// 1 in direct sunlight, 0 in Earth's shadow, soft across the terminator/penumbra.
float SpacedEarthShadow(float3 positionFromEarthCenter, float earthRadius)
{
    float along = dot(positionFromEarthCenter, _SpacedSunDir);
    if (along > 0) return 1;
    float perpendicular = length(positionFromEarthCenter - along * _SpacedSunDir);
    return smoothstep(earthRadius * 0.996, earthRadius * 1.006, perpendicular);
}

// True when the Earth sits between the camera and this local-scene fragment (e.g. a booster
// landing beyond the horizon). Uses the numerically stable root c / (-b + sqrt(b² - c)).
bool SpacedHiddenByEarth(float3 positionWS)
{
    float c = _SpacedCamEarthCLocal;
    if (c <= 0) return false;
    float3 toFragment = positionWS - _WorldSpaceCameraPos;
    float distance = length(toFragment);
    float3 direction = toFragment / max(distance, 1e-4);
    float b = dot(direction, _WorldSpaceCameraPos - _SpacedEarthCenterLocal);
    float discriminant = b * b - c;
    if (b >= 0 || discriminant <= 0) return false;
    float t = c / (-b + sqrt(discriminant));
    return t < distance - 60.0;
}

// Engine glow falls off with distance; radius ~60 m, wrapped so it reaches around the body.
float3 SpacedGlowLight(float4 glow, float3 n, float3 positionWS)
{
    if (glow.w <= 0) return 0;
    float3 toGlow = glow.xyz - positionWS;
    float d2 = dot(toGlow, toGlow);
    float wrap = saturate(dot(n, toGlow * rsqrt(max(d2, 1e-4))) * 0.6 + 0.4);
    return _SpacedGlowColor * glow.w * wrap / (1.0 + d2 / 3600.0);
}

// Key light + engine glows, for hardware and smoke alike.
float3 SpacedCinematicLight(float3 n, float3 positionWS)
{
    float3 light = SpacedGlowLight(_SpacedGlow0, n, positionWS) + SpacedGlowLight(_SpacedGlow1, n, positionWS);
    if (_SpacedKeyLight.w > 0)
        light += _SpacedKeyLightColor * _SpacedKeyLight.w * saturate(dot(n, _SpacedKeyLight.xyz) * 0.75 + 0.25);
    return light;
}

// Sun + Earth shadow + earthshine + sky light near the ground, GGX specular.
float3 SpacedLighting(float3 albedo, float metallic, float smoothness, float3 n, float3 v, float3 positionWS)
{
    float3 fromCenter = positionWS - _SpacedEarthCenterLocal;
    float radius = length(fromCenter);
    float3 up = fromCenter / radius;
    float altitude = radius - _SpacedEarthRadiusLocal;
    float3 l = _SpacedSunDir;

    float shadow = SpacedEarthShadow(fromCenter, _SpacedEarthRadiusLocal);
    float ndl = saturate(dot(n, l));

    float roughness = max(0.04, (1.0 - smoothness) * (1.0 - smoothness));
    float a2 = roughness * roughness;
    float3 h = normalize(l + v);
    float ndh = saturate(dot(n, h));
    float ndv = saturate(dot(n, v)) + 1e-4;
    float d = a2 / (PI * pow(ndh * ndh * (a2 - 1.0) + 1.0, 2.0));
    float k = roughness * 0.5;
    float g = (ndl / (ndl * (1.0 - k) + k)) * (ndv / (ndv * (1.0 - k) + k));
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);
    float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(h, v)), 5.0);
    float3 specular = d * g * fresnel / max(4.0 * ndv * ndl, 1e-4);
    float3 diffuse = albedo * (1.0 - metallic) / PI;

    float3 sunColor = float3(1.0, 0.97, 0.92) * _SpacedSunIntensity;
    // Low Sun: warmer, dimmer light through more air.
    float sunElevation = dot(up, l);
    if (altitude < 60000.0) sunColor *= lerp(float3(1.0, 0.45, 0.2), float3(1, 1, 1), saturate(sunElevation * 4.0 + 0.1));
    float3 color = (diffuse * PI + specular * PI) * sunColor * ndl * shadow;

    // Earthshine: light reflected off the sunlit planet below.
    float dayBelow = saturate(sunElevation + 0.3);
    color += albedo * float3(0.30, 0.38, 0.50) * 0.35 * saturate(-dot(n, up) * 0.5 + 0.5) * dayBelow;

    // Blue sky light inside the atmosphere.
    float atmosphere = saturate(1.0 - altitude / 40000.0);
    color += albedo * float3(0.30, 0.42, 0.62) * 0.55 * atmosphere * saturate(sunElevation * 3.0 + 0.2) * saturate(dot(n, up) * 0.5 + 0.5);

    // Cinematic key light (with a little specular so paint and metal read) and engine glow.
    color += albedo * SpacedCinematicLight(n, positionWS);
    if (_SpacedKeyLight.w > 0)
    {
        float3 hk = normalize(_SpacedKeyLight.xyz + v);
        color += fresnel * pow(saturate(dot(n, hk)), 2.0 / max(a2, 0.002)) * _SpacedKeyLightColor * _SpacedKeyLight.w * 0.25;
    }

    color += albedo * 0.015;
    return color;
}

#endif
