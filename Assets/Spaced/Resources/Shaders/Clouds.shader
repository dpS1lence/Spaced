// Cloud layer ~10 km above the surface. White where sunlit; on the night side lit by the Moon, glowing orange
// above cities (their light from below), and flickering with lightning in thick storm cloud.
Shader "Spaced/Clouds"
{
    Properties
    {
        _CloudTex ("Clouds", 2D) = "black" {}
        _Opacity ("Opacity", Range(0, 1)) = 0.92
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-200" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Clouds"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_CloudTex);
            SAMPLER(sampler_CloudTex);
            TEXTURE2D(_SpacedNightTex);
            SAMPLER(sampler_SpacedNightTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _CloudTex_ST;
                float _Opacity;
            CBUFFER_END

            float4x4 _SpacedCloudRotation; // Earth-fixed → cloud-layer frame

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
                float3 earthDir : TEXCOORD2;   // Earth-fixed direction (the layer itself drifts)
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.earthDir = mul(transpose((float3x3)_SpacedCloudRotation), input.normalOS);
                return output;
            }

            // Thunderstorms: thick cloud in the tropics and mid-latitudes flashes now and then, one cell at a time.
            float Lightning(float3 earthDir, float cloud)
            {
                float2 lonLat = float2(atan2(earthDir.z, earthDir.x), asin(clamp(earthDir.y, -1.0, 1.0))) * (180.0 / PI);
                float storm = saturate((cloud - 0.55) * 4.0) * saturate(1.3 - abs(lonLat.y) / 40.0);
                if (storm <= 0) return 0;
                float2 cell = floor(lonLat / 1.2);
                float clock = _Time.y * 1.7 + SpacedHash12(cell) * 40.0;
                float slot = floor(clock);
                if (SpacedHash12(cell + slot * 13.7) < 1.0 - 0.05 * storm) return 0;
                float age = frac(clock);
                float flicker = exp(-age * 10.0) * (0.55 + 0.45 * sin(age * 90.0));
                float2 local = frac(lonLat / 1.2) - 0.5 - (SpacedHash22(cell + slot) - 0.5) * 0.6;
                return flicker * exp(-dot(local, local) * 26.0) * storm;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float cloud = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, input.uv).r;
                float3 n = normalize(input.normalWS);
                float ndl = dot(n, _SpacedSunDir);
                // Wrap lighting: clouds stay faintly lit a little past the terminator, with a warm edge.
                float lit = saturate(ndl * 0.85 + 0.15);
                float3 tint = lerp(float3(1.0, 0.55, 0.3), float3(1, 1, 1), saturate(ndl * 5.0 + 0.2));
                float3 color = tint * lit * _SpacedSunIntensity * 0.85 + 0.004;

                float night = SpacedNightAmount(ndl);
                if (night > 0.001)
                {
                    float3 earthDir = normalize(input.earthDir);
                    float moonUp = saturate(dot(n, _SpacedMoonDir) * 4.0 + 0.1);
                    float3 nightColor = float3(0.62, 0.72, 1.0) * saturate(dot(n, _SpacedMoonDir) * 0.85 + 0.15) * moonUp * _SpacedMoonLight * 3.5;
                    // City light from below, softened by the cloud.
                    float3 below = SAMPLE_TEXTURE2D_LOD(_SpacedNightTex, sampler_SpacedNightTex, SpacedDirectionToUv(earthDir), 3.5).rgb;
                    nightColor += SpacedCityLights(below) * 0.45;
                    nightColor += float3(0.75, 0.82, 1.0) * Lightning(earthDir, cloud) * 6.0;
                    color += nightColor * night;
                }
                return half4(color, saturate(cloud * 1.15) * _Opacity * _SpacedCloudFade);
            }
            ENDHLSL
        }
    }
}
