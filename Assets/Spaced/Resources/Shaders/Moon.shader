// The Moon: NASA LROC colour map lit by the Sun (so the phase is right), with a little earthshine on the
// dark part of a crescent.
Shader "Spaced/Moon"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "gray" {}
        _Brightness ("Brightness", Float) = 0.7
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Moon"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Brightness;
            CBUFFER_END

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
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 n = normalize(input.normalWS);
                float3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;
                // Lunar regolith scatters back towards the Sun: soften the terminator less than Lambert would.
                float ndl = dot(n, _SpacedSunDir);
                float lit = saturate(ndl) * (0.75 + 0.25 * saturate(ndl));
                float3 color = albedo * lit * _SpacedSunIntensity * _Brightness;
                color += albedo * float3(0.30, 0.38, 0.55) * 0.012;   // earthshine
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
}
