// Earth surface: monthly Blue Marble lit by the real Sun, ocean sun glint, and shadows from the cloud layer.
// At night: moonlight with its glint on the sea, twilight, and NASA Black Marble 2016 city lights.
// The haze, limb glow, airglow and aurora come from Spaced/Atmosphere.
Shader "Spaced/Earth"
{
    Properties
    {
        _DayTex ("Day", 2D) = "white" {}
        _NightTex ("Night Lights", 2D) = "black" {}
        _CloudTex ("Clouds", 2D) = "black" {}
        _NightIntensity ("Night Intensity", Float) = 1
        _DayBrightness ("Day Brightness", Float) = 1.25
        _Glint ("Ocean Glint", Range(0, 4)) = 1.6
        _CloudShadow ("Cloud Shadow", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Earth"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_DayTex);
            SAMPLER(sampler_DayTex);
            TEXTURE2D(_NightTex);
            SAMPLER(sampler_NightTex);
            TEXTURE2D(_CloudTex);
            SAMPLER(sampler_CloudTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _DayTex_ST;
                float4 _NightTex_ST;
                float4 _CloudTex_ST;
                float _NightIntensity;
                float _DayBrightness;
                float _Glint;
                float _CloudShadow;
            CBUFFER_END

            float4x4 _CloudRotation; // Earth-fixed → cloud-layer frame (the clouds drift)

            // Coverage of your communications (R) and navigation (G) fleets, Earth-fixed equirectangular.
            TEXTURE2D(_SpacedCoverageTex);
            SAMPLER(sampler_SpacedCoverageTex);
            float _SpacedCoverageStrength;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float3 cloudDir : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.cloudDir = mul((float3x3)_CloudRotation, input.normalOS);
                return output;
            }

            float2 DirectionToUv(float3 d)
            {
                return float2(atan2(d.z, d.x) / (2.0 * PI) + 0.5, asin(clamp(d.y, -1.0, 1.0)) / PI + 0.5);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 n = normalize(input.normalWS);
                float3 l = _SpacedSunDir;
                float3 v = normalize(GetCameraPositionWS() - input.positionWS);
                float ndl = dot(n, l);

                float3 day = SAMPLE_TEXTURE2D(_DayTex, sampler_DayTex, input.uv).rgb * _DayBrightness;
                float3 night = SAMPLE_TEXTURE2D(_NightTex, sampler_NightTex, input.uv).rgb;
                float cloud = SAMPLE_TEXTURE2D_LOD(_CloudTex, sampler_CloudTex, DirectionToUv(normalize(input.cloudDir)), 2).r * _SpacedCloudFade;

                // Sun plus light from the blue sky (matches the streamed terrain tiles, Spaced/EarthTile).
                float lit = saturate(ndl) * SPACED_SUN_LIGHT + SPACED_SKY_LIGHT * saturate(ndl * 3.0 + 0.15);
                float shade = 1.0 - cloud * _CloudShadow;
                float3 color = day * lit * shade * _SpacedSunIntensity * 0.75;

                // Bluer-than-red pixels are mostly ocean; give them a specular sun glint.
                float ocean = saturate((day.b - day.r) * 4.0);
                float3 h = normalize(l + v);
                color += ocean * _Glint * pow(saturate(dot(n, h)), 120.0) * lit * shade * float3(1.0, 0.95, 0.85);

                // Night: moonlight and twilight on the ground, and the city lights (dimmed under thick cloud).
                color += SpacedNightGround(day, n, n, v, ocean, ndl) * shade;
                color += SpacedCityLights(night) * SpacedNightAmount(ndl) * (1.0 - cloud * 0.75) * _NightIntensity;

                if (_SpacedCoverageStrength > 0)
                {
                    float2 coverage = SAMPLE_TEXTURE2D(_SpacedCoverageTex, sampler_SpacedCoverageTex, input.uv).rg;
                    // Soft tint inside, brighter rim where coverage ends (the derivative picks up the edge).
                    float edge = saturate(fwidth(coverage.r) * 6.0) + saturate(fwidth(coverage.g) * 6.0);
                    color += (float3(0.10, 0.45, 1.0) * coverage.r * 0.10 + float3(0.25, 1.0, 0.45) * coverage.g * 0.08
                              + float3(0.5, 0.8, 1.0) * edge * 0.35) * _SpacedCoverageStrength;
                }

                return half4(color, 1);
            }
            ENDHLSL
        }
    }
}
