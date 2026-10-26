// Local ground and sea around a launch pad or droneship (metres, pad frame: +X east, +Z north).
// Procedural scrubland, beach and roads on land; animated waves with sky reflection and sun glint at sea.
// Fades out towards the edge so the planet texture takes over.
Shader "Spaced/Ground"
{
    Properties
    {
        _Shoreline ("Shoreline x (m)", Float) = 900
        _Radius ("Fade Radius (m)", Float) = 6000
        _AllSea ("All Sea", Float) = 0
        _ImageryTex ("Imagery", 2D) = "black" {}
        _ImageryMapping ("Imagery uv = xy + (east, north) * zw", Vector) = (0, 0, 0, 0)
        _HasImagery ("Has Imagery", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry-10" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Ground"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SpacedCommon.hlsl"

            TEXTURE2D(_ImageryTex);
            SAMPLER(sampler_ImageryTex);

            CBUFFER_START(UnityPerMaterial)
                float _Shoreline;
                float _Radius;
                float _AllSea;
                float4 _ImageryTex_ST;
                float4 _ImageryMapping;
                float _HasImagery;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 positionOS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash(i), hash(i + float2(1, 0)), f.x), lerp(hash(i + float2(0, 1)), hash(i + float2(1, 1)), f.x), f.y);
            }
            float fbm(float2 p)
            {
                float s = 0, a = 0.5;
                for (int i = 0; i < 5; i++) { s += noise(p) * a; p *= 2.07; a *= 0.5; }
                return s;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.positionOS = input.positionOS.xyz;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 xz = input.positionOS.xz;
                float3 n = normalize(input.normalWS);
                float3 v = normalize(GetCameraPositionWS() - input.positionWS);
                float distanceFromCenter = length(xz);
                float shore = _Shoreline + (fbm(float2(0, xz.y * 0.0015)) - 0.5) * 500.0;
                float sea = _AllSea > 0.5 ? 1.0 : (_Shoreline >= 0 ? smoothstep(shore - 15.0, shore + 15.0, xz.x) : 1.0 - smoothstep(shore - 15.0, shore + 15.0, xz.x));
                float beach = _AllSea > 0.5 ? 0.0 : (1.0 - smoothstep(0.0, 120.0, abs(xz.x - shore))) * (1.0 - sea);

                // Real Sentinel-2 imagery: decides land and water by itself where it covers the ground.
                float2 imageryUv = _ImageryMapping.xy + xz * _ImageryMapping.zw;
                bool hasImagery = _HasImagery > 0.5 && all(imageryUv > 0.002) && all(imageryUv < 0.998);
                float3 imagery = 0;
                if (hasImagery)
                {
                    imagery = SAMPLE_TEXTURE2D(_ImageryTex, sampler_ImageryTex, imageryUv).rgb;
                    float luminance = dot(imagery, float3(0.299, 0.587, 0.114));
                    float watery = saturate((imagery.b - imagery.r) * 12.0 + 0.3) * saturate((0.09 - luminance) * 25.0);
                    sea = watery > 0.5 ? 1.0 : 0.0;
                    beach = 0;
                }

                float3 albedo;
                float smoothness;
                if (sea > 0.5)
                {
                    // Sum of travelling waves perturbs the normal.
                    float t = _Time.y;
                    float2 g = float2(0, 0);
                    g += float2(0.8, 0.6) * cos(dot(xz, float2(0.8, 0.6)) * 0.09 + t * 1.1) * 0.35;
                    g += float2(-0.3, 0.95) * cos(dot(xz, float2(-0.3, 0.95)) * 0.21 + t * 1.7) * 0.2;
                    g += float2(0.95, -0.2) * cos(dot(xz, float2(0.95, -0.2)) * 0.57 + t * 2.9) * 0.1;
                    g += (float2(noise(xz * 0.05 + t * 0.2), noise(xz * 0.05 - t * 0.17)) - 0.5) * 0.3;
                    float3 east = normalize(cross(n, float3(0, 0, 1)));
                    float3 north = cross(east, n);
                    n = normalize(n - east * g.x * 0.3 - north * g.y * 0.3);
                    albedo = hasImagery ? imagery * 0.8 : float3(0.02, 0.06, 0.09);
                    smoothness = 0.92;
                }
                else
                {
                    float scrub = fbm(xz * 0.01);
                    float detail = fbm(xz * 0.12);
                    albedo = lerp(float3(0.16, 0.19, 0.08), float3(0.30, 0.28, 0.17), scrub) * (0.8 + detail * 0.4);
                    albedo = lerp(albedo, float3(0.78, 0.72, 0.58), beach);
                    // Service roads.
                    float road = max(1.0 - smoothstep(4.0, 6.0, abs(xz.y + 260.0)), 1.0 - smoothstep(4.0, 6.0, abs(xz.x + 320.0)));
                    road = max(road, (1.0 - smoothstep(3.0, 5.0, abs(xz.x - xz.y * 0.4 + 40.0))) * step(xz.y, -70.0) * step(-700.0, xz.y));
                    albedo = lerp(albedo, float3(0.10, 0.10, 0.10), road * (1.0 - sea));
                    smoothness = 0.08 + road * 0.2;
                    if (hasImagery)
                    {
                        // Keep a little procedural grain so 10 m pixels don't look smeared up close.
                        albedo = imagery * (0.85 + detail * 0.3);
                        smoothness = 0.1;
                    }
                }

                float3 color = SpacedLighting(albedo, 0.0, smoothness, n, v, input.positionWS);
                if (sea > 0.5)
                {
                    // Sky reflection, stronger at grazing angles.
                    float3 up = normalize(input.positionWS - _SpacedEarthCenterLocal);
                    float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(n, v)), 5.0);
                    float day = saturate(dot(up, _SpacedSunDir) * 3.0 + 0.2);
                    color += fresnel * float3(0.35, 0.5, 0.75) * day * _SpacedSunIntensity * 0.6;
                }

                float alpha = 1.0 - smoothstep(_Radius * 0.55, _Radius, distanceFromCenter);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
