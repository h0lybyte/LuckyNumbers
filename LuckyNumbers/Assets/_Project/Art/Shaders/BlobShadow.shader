// Blob shadows (Planet/BlobShadows): a soft dark patch on the ground under each caster, a fake shadow (the game has no
// shadow maps). Unlit, multiplied over what's already drawn (Blend DstColor Zero), so overlapping blobs just darken
// more and draw order doesn't matter. No depth write; depth test on, so a caster's feet cover their own blob.
// GPU-instanced (BlobShadows draws them all with RenderMeshInstanced), WebGL 2 and WebGPU. Flasks' stains use it too
// (no _BlobShape: a plain round blob).
//
// The mesh is a flat grid in the XZ plane, -0.5..0.5 (U = 2x, V = 2z across it). The instance's matrix lays it on the
// ground's slope, along the caster's facing; _BlobBend bends it to the terrain's curvature (metres up:
// x U^2 + y V^2 + z U V) and w is the body's strength (distance, height and sun fades).
// _BlobShape: xy is the sun's sweep in footprint units (the footprint's ellipse has radius 1; the quad is centred
// half-way along the sweep and big enough to hold it), z softens the rim (a lifted caster), w is the contact core's
// strength. The blob is the footprint's ellipse dragged along the sweep: dark at the caster, fading to _TipFade at the
// far end, softer as it goes; plus a darker core under the feet (the unswept ellipse's middle).
// Pulled toward the camera along its view ray, so it stays where it is on screen, by a few centimetres plus a little
// per metre away, and depth-offset: the ground's mesh is straight between vertices 1.5 m apart where the bent blob
// follows the smooth terrain, and depth precision drops with distance.
Shader "LuckyNumbers/BlobShadow"
{
    Properties
    {
        _ShadowColor ("Shadow Color (full strength)", Color) = (0.22, 0.21, 0.25, 1)
        _Softness ("Edge Softness", Range(0.05, 1)) = 0.55
        _CoreSize ("Contact Core Size (of the radius)", Range(0.05, 1)) = 0.5
        _TipFade ("Sweep's Far End Strength", Range(0, 1)) = 0.35
        _Pull ("Pull Toward Camera (m)", Float) = 0.05
        _PullPerMeter ("Pull Toward Camera per Metre Away", Float) = 0.01
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Blend DstColor Zero
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options nolightprobe nolightmap
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShadowColor;
                half _Softness, _CoreSize, _TipFade;
                float _Pull, _PullPerMeter;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Blob)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BlobBend)    // curvature x U^2, y V^2, z U V (m); w: body strength
                UNITY_DEFINE_INSTANCED_PROP(float4, _BlobShape)   // xy: sun sweep (footprint units); z: extra softness; w: core
            UNITY_INSTANCING_BUFFER_END(Blob)

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 q : TEXCOORD0;       // xy: footprint units from the caster's middle; zw: the sweep
                float3 look : TEXCOORD1;    // x: body strength, y: softness, z: core strength
                float fog : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float4 bend = UNITY_ACCESS_INSTANCED_PROP(Blob, _BlobBend);
                float4 shape = UNITY_ACCESS_INSTANCED_PROP(Blob, _BlobShape);
                float2 uv = v.positionOS.xz * 2.0;
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                p.y += bend.x * uv.x * uv.x + bend.y * uv.y * uv.y + bend.z * uv.x * uv.y;   // on the terrain's curve
                float3 toCam = GetCameraPositionWS() - p;
                float d = length(toCam);
                p += toCam * (min(_Pull + _PullPerMeter * d, 0.5 * d) / max(d, 1e-4));       // in front of the ground
                o.positionCS = TransformWorldToHClip(p);
                // The quad holds the swept ellipse: half-size 1 + |sweep| / 2 per axis, centred half-way along it.
                float2 sweep = shape.xy;
                o.q = float4(uv * (1.0 + abs(sweep) * 0.5) - sweep * 0.5, sweep);
                o.look = float3(bend.w, saturate(_Softness + shape.z), shape.w);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 q = i.q.xy;
                float2 s = i.q.zw;
                float ss = dot(s, s);
                float t = ss > 1e-4 ? saturate(dot(q, s) / ss) : 0.0;     // how far along the sweep (0: at the caster)
                float d = length(q - s * t);                                 // 0 on the sweep's spine, 1 at the rim
                half soft = saturate(i.look.y + 0.35 * t);                   // the penumbra widens away from the caster
                half body = (1.0 - smoothstep(1.0 - soft, 1.0, d)) * lerp(1.0, _TipFade, t);
                half core = 1.0 - smoothstep(_CoreSize * 0.25, _CoreSize, length(q));
                half dark = saturate(body * i.look.x + core * i.look.z);
                half3 c = lerp(half3(1, 1, 1), _ShadowColor.rgb, dark);
                return half4(MixFogColor(c, half3(1, 1, 1), i.fog), 1);      // fog washes it out (x1: no change)
            }
            ENDHLSL
        }
    }
}
