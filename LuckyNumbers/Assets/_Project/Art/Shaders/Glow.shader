// Glowing sprites (Planet/BoltField, BoltLook.Glow): a monster's lobbed flask (a soft core with a trail) and its splash
// (a toxic puff that swells and fades). Additive HDR (it blooms), no depth write, fogged to nothing far off.
// GPU-instanced: colour and shape per instance, so every flask, trail and puff in a field is one RenderMeshInstanced
// draw per 64. The mesh is Unity's quad (-0.5..0.5 in XY); the vertex shader turns it to face the camera from the
// instance matrix's position and scale:
//   _GlowShape.x = 0: a sprite, |column 0| across, facing the camera;
//   _GlowShape.x = 1: a trail, along column 1 (its full length; head at +0.5), |column 0| wide, turned about that axis
//                     to the camera;
//   _GlowShape.y: < 0 a steady glow; 0..1 a puff's age (it thins to a ring of wisps and fades out);
//   _GlowShape.z: a seed (each puff's wisps differ).
// Plain arithmetic only (no textures, no derivatives): WebGL 2 and WebGPU alike.
Shader "LuckyNumbers/Glow"
{
    Properties
    {
        [HDR] _BaseColor ("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options nolightprobe nolightmap
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
                UNITY_DEFINE_INSTANCED_PROP(float4, _GlowShape)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q : TEXCOORD0;      // -1..1 across the quad
                float4 shape : TEXCOORD1;
                float3 color : TEXCOORD2;
                float fog : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                float4 shape = UNITY_ACCESS_INSTANCED_PROP(Props, _GlowShape);
                float4x4 m = GetObjectToWorldMatrix();
                float3 centre = float3(m._m03, m._m13, m._m23);
                float3 across = float3(m._m00, m._m10, m._m20);
                float3 along = float3(m._m01, m._m11, m._m21);
                float width = length(across);
                float3 p;
                if (shape.x > 0.5)
                {
                    float3 toCam = GetCameraPositionWS() - centre;
                    float3 side = SafeNormalize(cross(along, toCam)) * width;
                    p = centre + along * v.positionOS.y + side * v.positionOS.x;
                }
                else
                {
                    float3 right = UNITY_MATRIX_V[0].xyz;
                    float3 up = UNITY_MATRIX_V[1].xyz;
                    p = centre + (right * v.positionOS.x + up * v.positionOS.y) * width;
                }
                o.positionCS = TransformWorldToHClip(p);
                o.q = v.uv * 2.0 - 1.0;
                o.shape = shape;
                o.color = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor).rgb;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            float Hash(float n) { return frac(sin(n) * 43758.5453); }

            // Smooth noise round the rim (angle a, 0..1 a turn), `cells` bumps a turn.
            float Rim(float a, float cells, float seed)
            {
                float x = a * cells;
                float i = floor(x);
                float f = x - i;
                float u = f * f * (3.0 - 2.0 * f);
                return lerp(Hash(fmod(i, cells) + seed), Hash(fmod(i + 1.0, cells) + seed), u);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 q = i.q;
                float glow;
                if (i.shape.x > 0.5)
                {
                    // Trail: bright down its middle, tapering and fading toward its tail (q.y -1).
                    float t = saturate(q.y * 0.5 + 0.5);
                    float across = saturate(1.0 - abs(q.x) * lerp(1.6, 1.0, t));
                    glow = across * across * t * t * 0.9;
                }
                else if (i.shape.y < 0.0)
                {
                    // Steady core: a hot centre in a soft halo.
                    float r2 = dot(q, q);
                    glow = (exp(-r2 * 14.0) * 1.6 + exp(-r2 * 4.0) * 0.45) * saturate(1.0 - r2);
                }
                else
                {
                    // Puff: a lumpy cloud that hollows into a ring of wisps as it swells, and fades out.
                    float age = saturate(i.shape.y);
                    float r = length(q);
                    float a = atan2(q.y, q.x) * (0.5 / PI) + 0.5;
                    float n = Rim(a, 7.0, i.shape.z) * 0.6 + Rim(a, 13.0, i.shape.z + 3.1) * 0.4;
                    float edge = 0.72 + 0.26 * n;
                    float body = 1.0 - smoothstep(edge - 0.25, edge, r);
                    float hollow = lerp(1.0, smoothstep(0.0, edge * 0.85, r), age);
                    float flash = exp(-r * r * 10.0) * saturate(1.0 - age * 4.0) * 2.0;   // the first instant's bright pop
                    float fade = (1.0 - age) * (1.0 - age);
                    glow = (body * hollow * (0.55 + 0.45 * n) + flash) * fade;
                }
                float3 c = i.color * glow;
                return float4(MixFogColor(c, float3(0, 0, 0), i.fog), 1);   // fog takes it to nothing (additive)
            }
            ENDHLSL
        }
    }
}
