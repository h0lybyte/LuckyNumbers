// PSX station/starship kit pieces (Art/Models/*Kit): the kit's point-filtered atlas times its vertex
// colours, which carry the baked shading. The kits' lights are painted into the atlas, so there are no
// real lights: a soft overhead term keeps floors brighter than ceilings, and distance fades to _FogColor
// (PS1-style depth cue, also hides the far end of long corridors). One material per kit, every chunk.
//
// Light modes: the atlas alpha masks the painted lights (tools/kitmask.py). Surfaces are multiplied by
// _Ambient; light texels are desaturated by _LightDesaturate, then tinted by _LightColor (rgb x a), unlit
// by the baked shading. _Pulse (x = Hz, y = depth) pulses lights fully and surfaces at half depth.
// StationView sets these from content/station.txtpb (on / off / emergency).
Shader "LuckyNumbers/StationKit"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Brightness ("Brightness", Float) = 1.15
        _FogColor ("Fog Color", Color) = (0.02, 0.025, 0.035, 1)
        _FogStart ("Fog Start", Float) = 16
        _FogEnd ("Fog End", Float) = 70
        _Ambient ("Ambient (surfaces)", Color) = (1, 1, 1, 1)
        _LightColor ("Light Color (rgb x a)", Color) = (1, 1, 1, 1.15)
        _LightDesaturate ("Light Desaturate", Range(0, 1)) = 0
        _Pulse ("Pulse (Hz, depth)", Vector) = (0, 0, 0, 0)
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
                float4 _MainTex_ST, _Tint, _FogColor, _Ambient, _LightColor, _Pulse;
                float _Brightness, _FogStart, _FogEnd, _LightDesaturate;
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
                o.color = float4(v.color.rgb * overhead, v.color.a);   // a: 0 = this piece's lights stay dark
                float dist = distance(GetCameraPositionWS(), positionWS);
                o.fog = saturate((dist - _FogStart) / max(_FogEnd - _FogStart, 0.01));
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                float3 albedo = tex.rgb;
                float pulse = _Pulse.x > 0 ? 1.0 - _Pulse.y * (0.5 + 0.5 * sin(_Time.y * _Pulse.x * 6.2831853)) : 1.0;

                float3 surface = albedo * i.color.rgb * _Tint.rgb * _Brightness * _Ambient.rgb * lerp(1.0, pulse, 0.5);
                float grey = dot(albedo, float3(0.3, 0.59, 0.11));
                float3 lamp = lerp(albedo, grey.xxx, _LightDesaturate) * _LightColor.rgb * _LightColor.a * pulse;
                // Unlit pieces (vertex alpha 0): their lamps are glass with the power off, whatever the mode.
                float3 darkLamp = lerp(albedo, grey.xxx, 0.7) * 0.3 * _Ambient.rgb * lerp(1.0, pulse, 0.5);   // dims with the room
                lamp = lerp(darkLamp, lamp, i.color.a);
                float3 color = lerp(surface, lamp, tex.a);
                // Lit lamps cut through the fog a little (they're emitters); dark ones fog like walls.
                color = lerp(color, _FogColor.rgb, i.fog * i.fog * (1.0 - 0.5 * tex.a * saturate(_LightColor.a - 1.0)));
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
