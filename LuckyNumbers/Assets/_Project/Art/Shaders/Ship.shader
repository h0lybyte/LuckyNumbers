// Textured ship lit by the universe's star (globals _LnSunPosition / _LnSunColor, like Planet3D):
// wrapped diffuse + ambient floor + a soft fill from the camera side (dark skins and the night side stay
// readable), bright saturated texels (lights, engines) glow regardless of light, thin rim against space.
// On a planet, the globals _LnSkyColor / _LnGroundColor (w = 1 when set) tint the ambient: sky light from above,
// ground bounce from below, so the shadow side picks up the world it's in. Metal gets a soft sun highlight (_Spec).
// Ships set _Lift 0 (true albedo, no lifted floor) and a high _GlowMin (painted gold/orange isn't a lamp).
// Optional: double-sided (_Cull Off; back faces lit from their side) and alpha cutout (_Cutoff > 0) for decals.
Shader "LuckyNumbers/Ship"
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
        _Heat ("Entry Heat (0..1, set at runtime)", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _Cutoff ("Alpha Cutoff (0 = opaque)", Range(0, 1)) = 0
        _Spec ("Sun Highlight", Range(0, 2)) = 0
        _SpecPower ("Highlight Tightness", Range(2, 128)) = 24
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Ambient, _Fill, _Glow, _Rim, _Heat, _Cutoff, _Lift, _GlowMin, _Spec, _SpecPower;
            CBUFFER_END

            #include "Include/ShipLit.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 positionWS : TEXCOORD2; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            float4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float4 texel = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                if (_Cutoff > 0.0) clip(texel.a - _Cutoff);
                float3 n = normalize(i.normalWS) * (front ? 1.0 : -1.0);
                return float4(ShipShade(texel.rgb, n, i.positionWS, _Heat), 1);
            }
            ENDHLSL
        }
    }
}
