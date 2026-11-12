// Monsters drawn by MonsterCrowd: GPU-instanced, each one skinned in the vertex stage from its model's baked animation
// texture (MonsterVat, baked by Editor/VatBaker from the skinned rig), lit exactly as the skinned model's Ship shader
// (Include/ShipLit.hlsl). Works on WebGL 2 and WebGPU: texel loads in the vertex stage, no compute, no SV_VertexID.
// _VatBones: per frame (a row), every bone's skinning matrix (bone -> model space x bind pose) as 3 texels (its rows),
// bone b at x = 3b. The mesh is the skin's bind pose with its 4 bone indices in uv1 and their weights in uv2.
// Per instance (_VatA, _VatB): clip A's frame pair (f0, f1, blend) and its weight; clip B's frame pair (the clip it's
// fading from) and the hit flash (the Ship shader's _Heat).
Shader "LuckyNumbers/MonsterVat"
{
    Properties
    {
        _MainTex ("Skin", 2D) = "white" {}
        _Ambient ("Ambient", Range(0, 1)) = 0.22
        _Fill ("Camera Fill", Range(0, 1)) = 0.45
        _Glow ("Light Glow", Float) = 1.5
        _Rim ("Rim", Float) = 0.35
        _Lift ("Dark Lift (0 = true albedo)", Range(0, 1)) = 1
        _GlowMin ("Glow From Brightness", Range(0, 1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _Cutoff ("Alpha Cutoff (0 = opaque)", Range(0, 1)) = 0
        _Spec ("Sun Highlight", Range(0, 2)) = 0
        _SpecPower ("Highlight Tightness", Range(2, 128)) = 24
        [NoScaleOffset] _VatBones ("Bone Matrices (RGBA half)", 2D) = "black" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
    TEXTURE2D(_VatBones);

    CBUFFER_START(UnityPerMaterial)
        float4 _MainTex_ST;
        float _Ambient, _Fill, _Glow, _Rim, _Cutoff, _Lift, _GlowMin, _Spec, _SpecPower;
    CBUFFER_END

    UNITY_INSTANCING_BUFFER_START(Vat)
        UNITY_DEFINE_INSTANCED_PROP(float4, _VatA)   // clip A: frame, next frame, blend, A's weight
        UNITY_DEFINE_INSTANCED_PROP(float4, _VatB)   // clip B: frame, next frame, blend; w: hit flash (heat)
    UNITY_INSTANCING_BUFFER_END(Vat)

    struct Attributes
    {
        float4 positionOS : POSITION;   // bind pose, mesh space
        float3 normalOS : NORMAL;
        float2 uv : TEXCOORD0;
        float4 bones : TEXCOORD1;       // its 4 bones
        float4 weights : TEXCOORD2;     // and their weights
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    // Bone `bone`'s skinning matrix at `frame`, blended toward the next frame by t.
    float3x4 VatBone(float bone, float3 frames)
    {
        int x = (int)(bone + 0.5) * 3;
        int f0 = (int)(frames.x + 0.5), f1 = (int)(frames.y + 0.5);
        float4 r0 = lerp(LOAD_TEXTURE2D_LOD(_VatBones, int2(x, f0), 0), LOAD_TEXTURE2D_LOD(_VatBones, int2(x, f1), 0), frames.z);
        float4 r1 = lerp(LOAD_TEXTURE2D_LOD(_VatBones, int2(x + 1, f0), 0), LOAD_TEXTURE2D_LOD(_VatBones, int2(x + 1, f1), 0), frames.z);
        float4 r2 = lerp(LOAD_TEXTURE2D_LOD(_VatBones, int2(x + 2, f0), 0), LOAD_TEXTURE2D_LOD(_VatBones, int2(x + 2, f1), 0), frames.z);
        return float3x4(r0, r1, r2);
    }

    // One clip's skinning matrix for this vertex: its bones by weight (linear blend skinning, as Unity's).
    float3x4 VatSkin(float4 bones, float4 weights, float3 frames)
    {
        float3x4 m = VatBone(bones.x, frames) * weights.x;
        if (weights.y > 0.0) m += VatBone(bones.y, frames) * weights.y;
        if (weights.z > 0.0) m += VatBone(bones.z, frames) * weights.z;
        if (weights.w > 0.0) m += VatBone(bones.w, frames) * weights.w;
        return m;
    }

    // This instance's pose, model space: clip A, cross-faded from clip B while A's weight is under 1.
    void VatPose(Attributes v, out float3 position, out float3 normal, out float heat)
    {
        float4 a = UNITY_ACCESS_INSTANCED_PROP(Vat, _VatA);
        float4 b = UNITY_ACCESS_INSTANCED_PROP(Vat, _VatB);
        float4 w = v.weights / max(1e-4, dot(v.weights, 1.0));   // 8-bit weights: back to a sum of 1
        float3x4 m = VatSkin(v.bones, w, a.xyz);
        if (a.w < 0.999) m = lerp(VatSkin(v.bones, w, b.xyz), m, a.w);
        position = mul(m, float4(v.positionOS.xyz, 1.0));
        normal = normalize(mul((float3x3)m, v.normalOS));
        heat = b.w;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        // As the Ship shader's one pass (no LightMode: SRPDefaultUnlit), so it draws where the skinned model did.
        Pass
        {
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Include/ShipLit.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float heat : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                float3 position, normal;
                VatPose(v, position, normal, o.heat);
                o.positionWS = TransformObjectToWorld(position);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(normal);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                if (_Cutoff > 0.0) clip(texel.a - _Cutoff);
                float3 n = normalize(i.normalWS) * (front ? 1.0 : -1.0);
                return float4(ShipShade(texel.rgb, n, i.positionWS, i.heat), 1);
            }
            ENDHLSL
        }

        // URP shadows from the same pose (MonsterCrowd.CastShadows).
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float4 vert(Attributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 position, normal;
                float heat;
                VatPose(v, position, normal, heat);
                float3 positionWS = TransformObjectToWorld(position);
                float3 normalWS = TransformObjectToWorldNormal(normal);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 toLight = normalize(_LightPosition - positionWS);
            #else
                float3 toLight = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, toLight));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
