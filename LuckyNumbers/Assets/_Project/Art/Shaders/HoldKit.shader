// Dwarven holds (Scripts/Hold): one material for every chunk. Each face samples the Texture2DArray _Layers
// (Art/Textures/HoldLayers.asset, built by Editor/HoldTextureBuilder.cs) at its slice, uv0 = (u, v, slice), with the
// StationKit "sharp bilinear" (crisp texels up close, one anti-aliased pixel at texel edges, mips far off), times the
// vertex colour (baked lantern light + ambient). Up to 4 real URP additional lights add on top (cheap Lambert, no
// shadows; Forward and Forward+). Distance fades to _HazeColor (dark underground air): fully hazed at _HazeDistance.
// Cutouts: _ALPHATEST_ON clips below _Cutoff (layer alpha; the array is DXT5 only if a layer has alpha).
// PS1 look (global _LnPsx): vertex snap, affine UVs, 15-bit dithered colour, as StationKit.
// Lantern flicker (global _LnLanternFlicker = amount, speed, ambient; HoldScene/HoldAmbiencePresenter): the baked
// light above the ambient level breathes with a slow 3D noise (neighbouring walls together, each lantern's pool out of
// step with the next) plus a quick shiver. Per vertex; 0 amount = steady (the planet's gate).
Shader "LuckyNumbers/HoldKit"
{
    Properties
    {
        _Layers ("Layers (Texture2DArray)", 2DArray) = "" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Brightness ("Brightness", Float) = 1
        _HazeColor ("Haze Color", Color) = (0.035, 0.05, 0.055, 1)
        _HazeDistance ("Haze Distance (fully hazed, m)", Float) = 40
        _LightGain ("Real Lights Gain", Range(0, 2)) = 1
        _MaxLod ("Max mip", Range(0, 7)) = 7
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma require 2darray
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Include/Noise.hlsl"

            TEXTURE2D_ARRAY(_Layers);
            SAMPLER(sampler_Layers);

            CBUFFER_START(UnityPerMaterial)
                float4 _Layers_TexelSize, _Tint, _HazeColor;
                float _Brightness, _HazeDistance, _LightGain, _MaxLod, _Cutoff;
            CBUFFER_END
            float _LnPsx;   // global: 1 = PS1 look
            float4 _LnLanternFlicker;   // global: x amount, y speed, z the ambient level (unflickered)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;        // baked light (rgb)
                float3 uv : TEXCOORD0;       // (u, v, slice)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
                float4 color : COLOR;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 affine : TEXCOORD3;   // (uv * w, w): screen-linear UVs for the PS1 look
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                if (_LnPsx > 0.5)
                {
                    float2 grid = float2(320.0, 320.0 * _ScreenParams.y / _ScreenParams.x) * 0.5;
                    o.positionCS.xy = floor(o.positionCS.xy / o.positionCS.w * grid + 0.5) / grid * o.positionCS.w;
                }
                o.uv = v.uv;
                o.affine = float3(v.uv.xy * o.positionCS.w, o.positionCS.w);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.color = v.color;
                if (_LnLanternFlicker.x > 0.0)
                {
                    float t = _Time.y * _LnLanternFlicker.y;
                    float slow = ln_simplex(float3(o.positionWS.xz * 0.11, t * 0.35) + o.positionWS.y * 0.05);
                    float quick = sin(t * 4.1 + slow * 9.0) * sin(t * 2.3 + o.positionWS.x * 0.3);
                    float flicker = 1.0 + _LnLanternFlicker.x * (slow * 0.75 + quick * 0.35);
                    float3 ambient = _LnLanternFlicker.zzz;
                    o.color.rgb = min(o.color.rgb, ambient) + max(o.color.rgb - ambient, 0.0) * flicker;
                }
                return o;
            }

            // Sharp bilinear (see StationKit): texel centres snapped, edges blended over ~1 screen pixel, mip by footprint.
            float4 SampleLayer(float2 uv, float slice)
            {
                float2 size = _Layers_TexelSize.zw;
                float2 t = uv * size;
                float2 texelsPerPixel = max(fwidth(t), 1e-5);
                float2 edge = clamp((frac(t) - 0.5) / texelsPerPixel, -0.5, 0.5);
                float2 sharp = (floor(t) + 0.5 + edge) / size;
                float lod = clamp(log2(max(texelsPerPixel.x, texelsPerPixel.y)), 0.0, _MaxLod);
                return SAMPLE_TEXTURE2D_ARRAY_LOD(_Layers, sampler_Layers, sharp, slice, lod);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 uv = _LnPsx > 0.5 ? i.affine.xy / i.affine.z : i.uv.xy;
                float4 tex = SampleLayer(uv, floor(i.uv.z + 0.5));
                #if defined(_ALPHATEST_ON)
                    clip(tex.a - _Cutoff);
                #endif
                float3 albedo = tex.rgb * _Tint.rgb;
                float3 light = i.color.rgb;

                #if defined(_ADDITIONAL_LIGHTS) || defined(_CLUSTER_LIGHT_LOOP)
                {
                    float3 n = normalize(i.normalWS);
                    InputData inputData = (InputData)0;
                    inputData.positionWS = i.positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                    uint pixelLightCount = (uint)min(GetAdditionalLightsCount(), 4);
                    uint used = 0;
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        if (used < 4u)
                        {
                            Light l = GetAdditionalLight(lightIndex, i.positionWS);
                            light += l.color * (l.distanceAttenuation * saturate(dot(n, l.direction)) * _LightGain);
                            used++;
                        }
                    LIGHT_LOOP_END
                }
                #endif

                float3 color = albedo * light * _Brightness;
                float haze = saturate(distance(GetCameraPositionWS(), i.positionWS) / max(_HazeDistance, 0.01));
                color = lerp(color, _HazeColor.rgb, haze * haze);   // eases in: near stays clear, far fades out
                if (_LnPsx > 0.5)
                {
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
