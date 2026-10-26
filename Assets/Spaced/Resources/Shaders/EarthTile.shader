// One streamed Sentinel-2 terrain tile on the globe (see GlobeTiles). Lit like Spaced/Earth: sun with terrain
// hillshading, ocean glint, cloud shadows and the fleet coverage overlay; at night, moonlight and the city lights
// of NASA's Black Marble (streamed tiles where loaded, the global map otherwise), broken into street-scale
// points of light on built-up ground when you are close.
// The base globe sits slightly lower (see EarthRenderer) so the two never z-fight; the tiles fade in over it
// as the camera descends (_SpacedTileFade).
Shader "Spaced/EarthTile"
{
    Properties
    {
        _MainTex ("Imagery", 2D) = "white" {}
        _NightTex ("Night lights tile", 2D) = "black" {}
        _NightRect ("Night uv' = uv * xy + zw (x = 0: use the global map)", Vector) = (0, 0, 0, 0)
        _TileZoom ("Tile zoom level", Float) = 5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "EarthTile"
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_NightTex);
            SAMPLER(sampler_NightTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _NightTex_ST;
                float4 _NightRect;
                float _TileZoom;
            CBUFFER_END

            // Shared with the base globe (set by EarthRenderer / FleetManager).
            TEXTURE2D(_SpacedNightTex);
            SAMPLER(sampler_SpacedNightTex);
            TEXTURE2D(_SpacedCloudTex);
            SAMPLER(sampler_SpacedCloudTex);
            TEXTURE2D(_SpacedCoverageTex);
            SAMPLER(sampler_SpacedCoverageTex);
            float4x4 _SpacedCloudRotation;
            float _SpacedCoverageStrength;
            float _SpacedDayBrightness;
            float _SpacedTileFade;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 globalUv : TEXCOORD1;   // equirectangular lon/lat
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 globalUv : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.globalUv = input.globalUv;
                return output;
            }

            // Close up, Black Marble's ~500 m pixels would be smooth blobs. Follow the ground instead: built-up
            // land (grey and mid-bright: not water, vegetation or snow) glows where its imagery is brighter than
            // the surroundings (roads, roofs), with a scatter of individual lights; the gaps stay dark.
            // x: brightness factor, y: how white the light is (LEDs) rather than sodium orange.
            float2 StreetLights(float2 uv, float3 albedo)
            {
                float lum = dot(albedo, float3(0.30, 0.59, 0.11));
                float saturation = max(albedo.r, max(albedo.g, albedo.b)) - min(albedo.r, min(albedo.g, albedo.b));
                float builtUp = saturate((0.12 - saturation) * 10.0) * saturate(lum * 8.0 - 0.4) * saturate((0.42 - lum) * 6.0);
                float3 around = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 4.0).rgb;
                float brighter = saturate((lum - dot(around, float3(0.30, 0.59, 0.11))) * 10.0 + 0.25);

                // Points of light on a jittered grid, at least a pixel wide so they don't shimmer.
                float2 cell = uv * 56.0;
                float2 id = floor(cell);
                float2 offset = frac(cell) - 0.5 - (SpacedHash22(id) - 0.5) * 0.7;
                float pixel = max(fwidth(cell.x), fwidth(cell.y));
                float size = max(0.07, pixel * 0.6);
                float on = step(0.4, SpacedHash12(id + 7.3));
                float points = exp(-dot(offset, offset) / (2.0 * size * size)) * (0.0049 / (size * size)) * on;
                // Seen from a few km the 10 m imagery is too soft to outline streets: let the points take over.
                float closeUp = saturate((_TileZoom - 13.5) / 2.0);
                float white = step(0.62, SpacedHash12(id + 3.1)) * saturate(points * 3.0) + brighter * builtUp * 0.3 * (1.0 - closeUp);
                float glow = lerp(0.1 + 1.4 * brighter * brighter, 0.03 + 0.12 * brighter, closeUp);
                return float2(builtUp * (glow + points * lerp(9.0, 24.0, closeUp)) + 0.02, saturate(white));
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 terrainNormal = normalize(input.normalWS);
                float3 sphereNormal = normalize(input.positionWS - _SpacedEarthCenterFar);
                float3 l = _SpacedSunDir;
                float3 v = normalize(GetCameraPositionWS() - input.positionWS);

                float brightness = _SpacedDayBrightness > 0 ? _SpacedDayBrightness : 1.25;
                float3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;
                float3 day = albedo * brightness * 1.1;

                // Clouds drift relative to the ground: rotate the Earth-fixed direction into the cloud layer.
                float lon = (input.globalUv.x - 0.5) * 2.0 * PI, lat = (input.globalUv.y - 0.5) * PI;
                float3 earthDir = float3(cos(lat) * cos(lon), sin(lat), cos(lat) * sin(lon));
                float3 cloudDir = mul((float3x3)_SpacedCloudRotation, earthDir);
                float cloud = SAMPLE_TEXTURE2D_LOD(_SpacedCloudTex, sampler_SpacedCloudTex, SpacedDirectionToUv(normalize(cloudDir)), 2).r * _SpacedCloudFade;

                // The terminator follows the planet's curvature; slopes facing the Sun get the hillshading.
                float sphereNdl = dot(sphereNormal, l);
                // Sun on the slope (hillshading) plus light from the blue sky, so slopes facing away from the Sun
                // are shaded, not black. Flat ground gets the same as the base globe (Spaced/Earth).
                float sunUp = saturate(sphereNdl * 6.0 + 0.6);
                float skyLight = SPACED_SKY_LIGHT * saturate(sphereNdl * 3.0 + 0.15) * (0.6 + 0.4 * dot(terrainNormal, sphereNormal));
                float lit = saturate(dot(terrainNormal, l)) * sunUp * SPACED_SUN_LIGHT + skyLight;
                float shade = 1.0 - cloud * 0.45;
                float3 color = day * lit * shade * _SpacedSunIntensity * 0.75;

                float ocean = saturate((day.b - day.r) * 4.0) * saturate(1.0 - dot(day, 1.0 / 3.0) * 3.0);
                float3 h = normalize(l + v);
                color += ocean * 1.6 * pow(saturate(dot(sphereNormal, h)), 120.0) * lit * shade * float3(1.0, 0.95, 0.85);

                // Night: moonlight, twilight, and the city lights.
                color += SpacedNightGround(day, terrainNormal, sphereNormal, v, ocean, sphereNdl) * shade;
                float night = SpacedNightAmount(sphereNdl);
                if (night > 0.001)
                {
                    float3 texel = _NightRect.x > 0
                        ? SAMPLE_TEXTURE2D(_NightTex, sampler_NightTex, input.uv * _NightRect.xy + _NightRect.zw).rgb
                        : SAMPLE_TEXTURE2D(_SpacedNightTex, sampler_SpacedNightTex, input.globalUv).rgb;
                    float3 lights = SpacedCityLights(texel);
                    float detail = saturate((_TileZoom - 10.0) / 3.0);
                    if (detail > 0)
                    {
                        float2 street = StreetLights(input.uv, albedo);
                        float level = dot(lights, float3(0.3, 0.55, 0.15));
                        float3 colored = lerp(float3(1.0, 0.5, 0.15), float3(0.95, 0.92, 0.85), street.y) * level * 1.45;
                        lights = lerp(lights, colored * street.x, detail);
                    }
                    color += lights * night * (1.0 - cloud * 0.75);
                }

                if (_SpacedCoverageStrength > 0)
                {
                    float2 coverage = SAMPLE_TEXTURE2D(_SpacedCoverageTex, sampler_SpacedCoverageTex, input.globalUv).rg;
                    color += (float3(0.10, 0.45, 1.0) * coverage.r * 0.10 + float3(0.25, 1.0, 0.45) * coverage.g * 0.08) * _SpacedCoverageStrength;
                }
                return half4(color, _SpacedTileFade);
            }
            ENDHLSL
        }
    }
}
