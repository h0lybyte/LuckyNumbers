// Cold-gas puff (Core/ShipView, content/engines.txtpb gas jets): compressed air, not a flame. Same mesh as the
// exhaust (a unit tube along +Z plus a quad flagged uv2.x = 1), drawn alpha-blended so it shows on a bright sky:
//   cone   the jet leaving the nozzle, widening to _Expand x the nozzle radius as the air spreads;
//   cloud  the quad, facing the camera, rolls out along the cone and swells as the puff ages.
// Per renderer: _Puff (age 0..1 through one puff) and _Thrust (its strength 0..1). The puff shoots out fast and
// slows, then thins away; wisps (simplex noise) churn through it. Lit by the scene's sun (_LnSunColor) and sky
// (_LnSkyColor), so it's warm on a sunny planet and pale grey in space.
Shader "LuckyNumbers/ColdGas"
{
    Properties
    {
        _Color ("Colour", Color) = (0.92, 0.94, 0.97, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.65
        _Softness ("Edge Softness", Float) = 1.6
        _Expand ("Cone Width At The End (x nozzle)", Float) = 5
        _CloudSize ("Cloud Size (x nozzle)", Float) = 5
        _WispScale ("Wisp Scale", Float) = 3
        _WispSpeed ("Wisp Speed", Float) = 2
        _Puff ("Puff Age (set at runtime)", Range(0, 1)) = 0.3
        _Thrust ("Strength (set at runtime)", Float) = 1
        _Seed ("Seed (set at runtime)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Blend One OneMinusSrcAlpha   // premultiplied
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Noise.hlsl"

            float4 _LnSunPosition, _LnSunColor, _LnSkyColor;   // globals (Ship.shader's), unset: neutral

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Opacity, _Softness, _Expand, _CloudSize, _WispScale, _WispSpeed, _Puff, _Thrust, _Seed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float2 kind : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float kind : TEXCOORD3;
            };

            // How far out the puff has got (share of the jet's length): fast off the nozzle, slowing as it spreads.
            float Reach() { float a = saturate(_Puff); return 0.15 + 0.85 * (1.0 - pow(1.0 - a, 3.0)); }

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.uv = v.uv;
                o.kind = v.kind.x;
                float reach = Reach();
                float radius = length(float3(UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20));
                if (v.kind.x > 0.5)
                {
                    // Cloud: rides the front of the puff, swelling as it goes.
                    float3 centre = TransformObjectToWorld(float3(0, 0, reach * 0.8));
                    float size = radius * _CloudSize * (0.35 + 0.65 * reach) * (0.6 + 0.4 * saturate(_Thrust));
                    float3 right = UNITY_MATRIX_V._m00_m01_m02, up = UNITY_MATRIX_V._m10_m11_m12;
                    o.positionWS = centre + (right * v.uv.x + up * v.uv.y) * size;
                    o.normalWS = -UNITY_MATRIX_V._m20_m21_m22;
                }
                else
                {
                    float t = v.positionOS.z;
                    float widen = lerp(1.0, _Expand, t) * (0.7 + 0.3 * reach);
                    o.positionWS = TransformObjectToWorld(float3(v.positionOS.xy * widen, t * reach));
                    float slope = (_Expand - 1.0) / max(reach, 1e-3);
                    o.normalWS = TransformObjectToWorldNormal(normalize(float3(v.positionOS.xy, -slope)));
                }
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float age = saturate(_Puff);
                // In fast, then thinning away.
                float life = smoothstep(0.0, 0.08, age) * pow(1.0 - age, 1.6);
                float time = _Time.y * _WispSpeed + _Seed * 13.0;
                float a;
                if (i.kind > 0.5)
                {
                    float r2 = dot(i.uv, i.uv);
                    float2 st = i.uv * _WispScale + float2(_Seed * 5.0, -time);
                    float wisps = 0.65 + 0.35 * (ln_simplex(st) * 0.6 + ln_simplex(st * 2.1 + 3.3) * 0.4);
                    a = saturate(exp(-r2 * 3.0) * saturate(1.0 - r2) * wisps * 1.3);
                }
                else
                {
                    float along = i.uv.y;
                    float3 n = normalize(i.normalWS);
                    float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                    float body = pow(saturate(abs(dot(n, view))), _Softness);
                    float fade = pow(saturate(1.0 - along), 1.3);
                    float ang = i.uv.x * 6.28318;
                    float2 st = float2(cos(ang) * 1.3 + _Seed * 2.0, along * _WispScale * 2.0 - time * 2.0);
                    float wisps = 0.6 + 0.4 * (ln_simplex(st) * 0.6 + ln_simplex(st * 2.3 + 7.1) * 0.4);
                    a = body * fade * wisps;
                }
                a *= _Opacity * life * saturate(_Thrust);
                // Lit by the scene: sun plus sky (both neutral when the scene sets neither).
                float3 sun = _LnSunPosition.w > 0.5 ? _LnSunColor.rgb : float3(1, 1, 1);
                float3 sky = _LnSkyColor.w > 0.5 ? _LnSkyColor.rgb : float3(0.55, 0.58, 0.65);
                float3 light = sun * 0.55 + sky * 0.45;
                return float4(_Color.rgb * light * a, a);
            }
            ENDHLSL
        }
    }
}
