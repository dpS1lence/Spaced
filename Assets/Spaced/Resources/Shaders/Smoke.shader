// Particle smoke and steam: lit by the Sun (dark at night, warm near sunset), soft, alpha-blended.
// Vertex colour comes from the particle system; a little self-emission keeps the base glowing near the flame.
Shader "Spaced/Smoke"
{
    Properties
    {
        _MainTex ("Puff", 2D) = "white" {}
        _Glow ("Flame Glow", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Smoke"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Glow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                // Particle systems in world/custom space already give world positions.
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                if (SpacedHiddenByEarth(input.positionWS)) discard;
                float4 puff = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                float3 fromCenter = input.positionWS - _SpacedEarthCenterLocal;
                float3 up = normalize(fromCenter);
                float shadow = SpacedEarthShadow(fromCenter, _SpacedEarthRadiusLocal);
                float sunElevation = dot(up, _SpacedSunDir);
                float3 sunColor = lerp(float3(1.0, 0.5, 0.25), float3(1, 0.98, 0.94), saturate(sunElevation * 4.0 + 0.1));
                float3 v = normalize(GetCameraPositionWS() - input.positionWS);
                // Forward scattering: smoke glows when the Sun is behind it.
                float scatter = 0.65 + 0.6 * pow(saturate(dot(-v, _SpacedSunDir)), 4.0);
                float3 light = sunColor * _SpacedSunIntensity * shadow * scatter + float3(0.28, 0.36, 0.5) * 0.25 * saturate(sunElevation * 3.0 + 0.3) + 0.01;
                // Smoke right behind the engines is lit by the flame; a billboard faces the camera, so use -v as normal.
                light += SpacedCinematicLight(v, input.positionWS) * 1.5;
                float3 color = input.color.rgb * puff.rgb * light + float3(1.0, 0.55, 0.2) * _Glow * input.color.a;
                return half4(color, input.color.a * puff.a);
            }
            ENDHLSL
        }
    }
}
