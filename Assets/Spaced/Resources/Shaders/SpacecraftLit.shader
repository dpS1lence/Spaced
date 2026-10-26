// PBR for hardware in the local (metre-scale) scene: satellites, rockets, pads, droneships.
// Lit by the real Sun with Earth's shadow, earthshine and sky light (SpacedCommon.hlsl). GPU-instanced.
Shader "Spaced/SpacecraftLit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Float) = 1
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        _NightEmission ("Emission only at night", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "SpacecraftLit"
            Cull Off // thin panels and fins are single-sided geometry

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _BaseMap_ST;
                float4 _NormalMap_ST;
                float _NormalStrength;
                float _Metallic;
                float _Smoothness;
                float4 _EmissionColor;
                float _NightEmission;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = normals.normalWS;
                output.tangentWS = float4(normals.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                if (SpacedHiddenByEarth(input.positionWS)) discard;

                float4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                float3 tangentNormal = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv), _NormalStrength);
                float3 n = normalize(input.normalWS);
                float3 t = normalize(input.tangentWS.xyz);
                float3 b = cross(n, t) * input.tangentWS.w;
                n = normalize(mul(tangentNormal, float3x3(t, b, n)));
                // Normals are authored outward; light the side facing the camera when we see a panel from behind.
                n = dot(n, normalize(GetCameraPositionWS() - input.positionWS)) < 0 && IS_FRONT_VFACE(face, false, true) ? -n : n;

                float3 v = normalize(GetCameraPositionWS() - input.positionWS);
                // Smoothness comes from the map's alpha so one texture can carry wear and soot.
                float3 color = SpacedLighting(albedo.rgb, _Metallic, _Smoothness * albedo.a, n, v, input.positionWS);

                float night = 1.0;
                if (_NightEmission > 0.5)
                {
                    float3 up = normalize(input.positionWS - _SpacedEarthCenterLocal);
                    night = saturate(-dot(up, _SpacedSunDir) * 8.0 + 0.3);
                }
                color += _EmissionColor.rgb * night;
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
}
