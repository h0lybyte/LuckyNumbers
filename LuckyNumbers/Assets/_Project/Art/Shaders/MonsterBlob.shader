// Monsters' blob shadows (Planet/MonsterBlobs): a soft dark patch on the ground under each one, a fake shadow (the game
// has no shadow maps). Unlit, multiplied over what's already drawn (Blend DstColor Zero), so overlapping blobs just
// darken more and draw order doesn't matter. No depth write; depth test on, so a monster's feet cover their own blob.
// GPU-instanced (MonsterBlobs draws them all with RenderMeshInstanced), WebGL 2 and WebGPU.
// The mesh is a flat grid in the XZ plane, -0.5..0.5 (U = 2x, V = 2z across it). The instance's matrix lays it on the
// ground's slope; _BlobBend bends it to the terrain's curvature (metres up: x U^2 + y V^2 + z U V) and w is its
// strength (distance and height fade). Pulled toward the camera along its view ray, so it stays where it is on screen,
// by a few centimetres plus a little per metre away, and depth-offset: the ground's mesh is straight between vertices
// 1.5 m apart where the bent blob follows the smooth terrain, and depth precision drops with distance.
Shader "LuckyNumbers/MonsterBlob"
{
    Properties
    {
        _ShadowColor ("Shadow Color (full strength)", Color) = (0.22, 0.21, 0.25, 1)
        _Softness ("Edge Softness", Range(0.05, 1)) = 0.6
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
                half _Softness;
                float _Pull, _PullPerMeter;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Blob)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BlobBend)   // curvature x U^2, y V^2, z U V (m); w: strength
            UNITY_INSTANCING_BUFFER_END(Blob)

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 disc : TEXCOORD0;   // xy: U, V (-1..1 across the blob); z: this instance's strength
                float fog : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float4 bend = UNITY_ACCESS_INSTANCED_PROP(Blob, _BlobBend);
                float2 uv = v.positionOS.xz * 2.0;
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                p.y += bend.x * uv.x * uv.x + bend.y * uv.y * uv.y + bend.z * uv.x * uv.y;   // on the terrain's curve
                float3 toCam = GetCameraPositionWS() - p;
                float d = length(toCam);
                p += toCam * (min(_Pull + _PullPerMeter * d, 0.5 * d) / max(d, 1e-4));       // in front of the ground
                o.positionCS = TransformWorldToHClip(p);
                o.disc = float3(uv, bend.w);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half r = saturate(1.0 - length(i.disc.xy));             // 1 at the centre, 0 at the rim
                half dark = smoothstep(0.0, _Softness, r) * i.disc.z;    // soft edge, scaled by the instance's fade
                half3 c = lerp(half3(1, 1, 1), _ShadowColor.rgb, dark);
                return half4(MixFogColor(c, half3(1, 1, 1), i.fog), 1);  // fog washes it out (x1: no change)
            }
            ENDHLSL
        }
    }
}
