// PSX station/starship kit pieces (Art/Models/*Kit): the kit's point-filtered atlas times its vertex
// colours, which carry the baked shading. The kits' lights are painted into the atlas, so there are no
// real lights: a soft overhead term keeps floors brighter than ceilings, and distance fades to _FogColor
// (PS1-style depth cue, also hides the far end of long corridors). One material per kit, every chunk.
Shader "LuckyNumbers/StationKit"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Brightness ("Brightness", Float) = 1.15
        _FogColor ("Fog Color", Color) = (0.02, 0.025, 0.035, 1)
        _FogStart ("Fog Start", Float) = 10
        _FogEnd ("Fog End", Float) = 38
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

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST, _Tint, _FogColor;
                float _Brightness, _FogStart, _FogEnd;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fog : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                // Up-facing a touch brighter, down-facing a touch darker: reads as overhead light.
                float overhead = 0.9 + 0.12 * n.y;
                o.color = float4(v.color.rgb * overhead, 1);
                float dist = distance(GetCameraPositionWS(), positionWS);
                o.fog = saturate((dist - _FogStart) / max(_FogEnd - _FogStart, 0.01));
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb;
                float3 color = albedo * i.color.rgb * _Tint.rgb * _Brightness;
                color = lerp(color, _FogColor.rgb, i.fog * i.fog);
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
