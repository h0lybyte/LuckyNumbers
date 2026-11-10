// PSX station/starship kit pieces (Art/Models/*Kit): the kit's point-filtered atlas times its vertex
// colours, which carry the baked shading. The kits' lights are painted into the atlas, so there are no
// real lights: a soft overhead term keeps floors brighter than ceilings, and distance fades to _FogColor
// (PS1-style depth cue, also hides the far end of long corridors). One material per kit, every chunk.
//
// Light modes: the atlas alpha masks the painted lights (tools/kitmask.py). Surfaces are multiplied by
// _Ambient; light texels are desaturated by _LightDesaturate, then tinted by _LightColor (rgb x a), unlit
// by the baked shading. _Pulse (x = Hz, y = depth) pulses lights fully and surfaces at half depth.
// StationView sets these from content/station.txtpb (on / off / emergency).
//
// Light pools (_Pools): surfaces a touch brighter under the room ceilings' lit rows (every _PoolPeriod m along Z),
// only while the lamps are lit. PS1 look (global _LnPsx, toggled in game): vertices snap to a coarse screen
// grid, textures warp affinely (screen-linear UVs: (uv*w, w) interpolated, divided per pixel; WebGL 2 has no
// noperspective), colour drops to 15 bits with an ordered dither.
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
        _MaxLod ("Max mip (atlas tiles stay apart)", Range(0, 6)) = 3
        _Pools ("Light pools", Range(0, 1)) = 0
        _PoolPeriod ("Light pool period (m)", Float) = 4
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
                float4 _MainTex_ST, _MainTex_TexelSize, _Tint, _FogColor, _Ambient, _LightColor, _Pulse;
                float _Brightness, _FogStart, _FogEnd, _LightDesaturate, _MaxLod, _Pools, _PoolPeriod;
            CBUFFER_END
            float _LnPsx;            // global: 1 = PS1 look
            float _LnDoorOpen[64];   // global: how open each door is (0..1, eased), indexed by uv2.x - 1

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 door : TEXCOORD1;    // door leaves: (door index + 1, slide side +-1); 0 elsewhere
                float2 slide : TEXCOORD2;   // door leaves: slide direction (world x, z)
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fog : TEXCOORD1;
                float3 affine : TEXCOORD2;     // (uv * w, w): screen-linear UVs for the PS1 look
                float3 positionWS : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                // Doors are merged with the walls: their leaves slide 0.6 m apart into the wall here, not in C#.
                if (v.door.x > 0.5)
                {
                    float open = _LnDoorOpen[min((int)(v.door.x - 0.5), 63)];
                    positionWS += float3(v.slide.x, 0, v.slide.y) * (v.door.y * 0.6 * open);
                }
                o.positionCS = TransformWorldToHClip(positionWS);
                if (_LnPsx > 0.5)
                {
                    // Wobble: snap to a 320-wide grid (scaled to the screen's aspect), like the PS1's integer vertices.
                    float2 grid = float2(320.0, 320.0 * _ScreenParams.y / _ScreenParams.x) * 0.5;
                    o.positionCS.xy = floor(o.positionCS.xy / o.positionCS.w * grid + 0.5) / grid * o.positionCS.w;
                }
                o.positionWS = positionWS;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.affine = float3(o.uv * o.positionCS.w, o.positionCS.w);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                // Up-facing a touch brighter, down-facing a touch darker: reads as overhead light.
                float overhead = 0.9 + 0.12 * n.y;
                o.color = float4(v.color.rgb * overhead, v.color.a);   // a: 0 = this piece's lights stay dark
                float dist = distance(GetCameraPositionWS(), positionWS);
                o.fog = saturate((dist - _FogStart) / max(_FogEnd - _FogStart, 0.01));
                return o;
            }

            // Sharp bilinear: point-sampled texels (the PS1 look) whose edges blend over about one screen pixel, so
            // texel boundaries don't stair-step up close; when a pixel covers more than a texel (far off, grazing),
            // the mip chain takes over instead of skipping texels (the shimmer and broken lines). The mip level is
            // capped (_MaxLod) so neighbouring tiles of the atlas don't bleed into each other.
            float4 SampleAtlas(float2 uv)
            {
                float2 size = _MainTex_TexelSize.zw;
                float2 t = uv * size;
                float2 texelsPerPixel = max(fwidth(t), 1e-5);
                float2 edge = clamp((frac(t) - 0.5) / texelsPerPixel, -0.5, 0.5);
                float2 sharp = (floor(t) + 0.5 + edge) / size;
                float lod = clamp(log2(max(texelsPerPixel.x, texelsPerPixel.y)), 0.0, _MaxLod);
                return SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, sharp, lod);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 uv = _LnPsx > 0.5 ? i.affine.xy / i.affine.z : i.uv;
                float4 tex = SampleAtlas(uv);
                float3 albedo = tex.rgb;
                float pulse = _Pulse.x > 0 ? 1.0 - _Pulse.y * (0.5 + 0.5 * sin(_Time.y * _Pulse.x * 6.2831853)) : 1.0;

                float3 surface = albedo * i.color.rgb * _Tint.rgb * _Brightness * _Ambient.rgb * lerp(1.0, pulse, 0.5);
                // Pools of light under the lit ceiling rows (rows at z = 0..2 mod period are lit: brightest at z = 1).
                float pool = cos(6.2831853 * (i.positionWS.z - 1.0) / max(_PoolPeriod, 0.5));
                surface *= 1.0 + _Pools * 0.22 * pool * saturate(_LightColor.a - 0.4);
                float grey = dot(albedo, float3(0.3, 0.59, 0.11));
                float3 lamp = lerp(albedo, grey.xxx, _LightDesaturate) * _LightColor.rgb * _LightColor.a * pulse;
                // Unlit pieces (vertex alpha 0): their lamps are glass with the power off, whatever the mode.
                float3 darkLamp = lerp(albedo, grey.xxx, 0.7) * 0.3 * _Ambient.rgb * lerp(1.0, pulse, 0.5);   // dims with the room
                lamp = lerp(darkLamp, lamp, i.color.a);
                float3 color = lerp(surface, lamp, tex.a);
                // Lit lamps cut through the fog a little (they're emitters); dark ones fog like walls.
                color = lerp(color, _FogColor.rgb, i.fog * i.fog * (1.0 - 0.5 * tex.a * saturate(_LightColor.a - 1.0)));
                if (_LnPsx > 0.5)
                {
                    // 15-bit colour (5 bits a channel) with a 4x4 ordered dither, as the PS1 output it.
                    uint2 p = uint2(i.positionCS.xy) & 3;
                    const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
                    float dither = (bayer[p.y * 4 + p.x] + 0.5) / 16.0 - 0.5;
                    color = floor(saturate(color) * 31.0 + 0.5 + dither) / 31.0;
                }
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
