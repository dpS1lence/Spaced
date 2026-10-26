// Rocket exhaust plume (additive, HDR). The unit lathe mesh is shaped here: at sea level the plume is a
// tight, bright jet with Mach diamonds; as ambient pressure falls it balloons into the huge, faint plume
// seen at high altitude. _Expansion: 0 = sea level, 1 = vacuum.
Shader "Spaced/Plume"
{
    Properties
    {
        [HDR] _CoreColor ("Core", Color) = (6, 4.5, 2.6, 1)
        [HDR] _EdgeColor ("Edge", Color) = (2.2, 0.9, 0.35, 1)
        _Length ("Length (m)", Float) = 30
        _Radius ("Nozzle radius (m)", Float) = 1.8
        _Expansion ("Expansion", Range(0, 1)) = 0
        _Intensity ("Intensity", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Plume"
            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CoreColor;
                float4 _EdgeColor;
                float _Length;
                float _Radius;
                float _Expansion;
                float _Intensity;
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
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float t : TEXCOORD2;
                float angle : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float t = input.uv.y;
                // Sea level: nearly straight jet. Vacuum: wide parabolic bloom.
                float widen = lerp(1.0 + 0.6 * t, 1.0 + 18.0 * sqrt(t), _Expansion);
                float3 p = float3(input.positionOS.x * _Radius * widen, -t * _Length, input.positionOS.z * _Radius * widen);
                float3 positionWS = TransformObjectToWorld(p);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = TransformObjectToWorldNormal(normalize(float3(input.positionOS.x, 0, input.positionOS.z)));
                output.t = t;
                output.angle = input.uv.x;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                if (SpacedHiddenByEarth(input.positionWS)) discard;
                float3 v = normalize(GetCameraPositionWS() - input.positionWS);
                float facing = abs(dot(normalize(input.normalWS), v));
                float t = input.t;

                // Soft edges, bright core, fade along the length.
                float body = pow(facing, lerp(1.6, 3.0, _Expansion)) * pow(saturate(1.0 - t), lerp(1.4, 2.5, _Expansion));
                float diamonds = lerp(0.55 + 0.45 * cos(t * 48.0), 1.0, saturate(_Expansion * 3.0));
                float flicker = 0.9 + 0.1 * sin(_Time.y * 63.0 + input.angle * 40.0 + t * 20.0);
                float3 color = lerp(_CoreColor.rgb, _EdgeColor.rgb, saturate(t * 2.5 + _Expansion * 0.5));
                float strength = body * diamonds * flicker * _Intensity * lerp(1.0, 0.18, _Expansion);
                return half4(color * strength, 1);
            }
            ENDHLSL
        }
    }
}
