// Relics lying at the crash site: a two-tone body (_ColorA low, _ColorB high, with a soft band) lit by the
// sun (globals _LnSunPosition / _LnSunColor), a rim glow in _ColorB and a slow pulse so they catch the eye on
// the ground. Colours come per relic (MaterialPropertyBlock: its culture's, or a named artifact's own).
Shader "LuckyNumbers/Relic"
{
    Properties
    {
        _ColorA ("Color A", Color) = (0.18, 0.62, 0.52, 1)
        _ColorB ("Color B", Color) = (0.35, 0.75, 0.95, 1)
        _Glow ("Glow", Float) = 0.9
        _Pulse ("Pulse (Hz)", Float) = 0.7
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _LnSunPosition, _LnSunColor;   // globals

            CBUFFER_START(UnityPerMaterial)
                float4 _ColorA, _ColorB;
                float _Glow, _Pulse;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float height : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.height = v.positionOS.y;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 l = _LnSunPosition.w > 0.5 ? normalize(_LnSunPosition.xyz - i.positionWS) : normalize(float3(0.4, 0.8, 0.3));
                float3 sun = _LnSunPosition.w > 0.5 ? _LnSunColor.rgb : float3(1, 1, 1);
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);

                float t = saturate(i.height + 0.5);
                float band = smoothstep(0.46, 0.5, t) - smoothstep(0.5, 0.54, t);
                float3 albedo = lerp(_ColorA.rgb, _ColorB.rgb, smoothstep(0.2, 0.8, t)) + band * 0.35;
                float diffuse = saturate(dot(n, l)) * 0.8 + 0.25;
                float rim = pow(1.0 - saturate(dot(n, v)), 2.5);
                float pulse = 0.75 + 0.25 * sin(_Time.y * _Pulse * 6.2831853);
                float3 color = albedo * sun * diffuse + _ColorB.rgb * rim * _Glow * pulse;
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
