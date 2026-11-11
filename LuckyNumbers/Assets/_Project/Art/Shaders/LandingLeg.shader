// A ship's landing leg, faked in the vertex shader: one strut mesh (LandingGear.Mesh: three tubes, uv.x = which,
// uv.y = 0..1 along it) that _Extend (0..1, per renderer) telescopes. The housing drops out of the hull first, then
// the piston slides out of it and the foot pad opens, the foot reaching object y -1 at full extension (the
// transform's y scale is the leg's full length, so the foot lands on the ground). Greyscale: lit by the
// universe's star (_LnSunPosition, like LuckyNumbers/Ship) with a hemisphere ambient, the piston polished (a sun
// highlight), the housing banded, the pad dark.
Shader "LuckyNumbers/LandingLeg"
{
    Properties
    {
        _Extend ("Extend (0 = tucked away, set at runtime)", Range(0, 1)) = 1
        _Housing ("Housing Share Of The Length", Range(0.1, 0.9)) = 0.42
        _Piston ("Piston Radius (of the housing's)", Range(0.2, 1)) = 0.55
        _Foot ("Foot Pad Radius (of the housing's)", Range(1, 4)) = 2.1
        _FootHeight ("Foot Pad Height (of the length)", Range(0.01, 0.2)) = 0.045
        _Ambient ("Ambient", Range(0, 1)) = 0.32
        _Grey ("Housing / Piston / Foot Grey", Vector) = (0.34, 0.72, 0.2, 0)
        _Spec ("Piston Highlight", Range(0, 2)) = 0.7
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Cull Off   // the mesh is cheap tubes: draw both sides, light the back from its side
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _LnSunPosition, _LnSunColor; // globals set by SpacePresenter / CrashSitePresenter
            float4 _LnSkyColor, _LnGroundColor; // globals set by CrashSitePresenter (w = 1), else neutral

            CBUFFER_START(UnityPerMaterial)
                float _Extend, _Housing, _Piston, _Foot, _FootHeight, _Ambient, _Spec;
                float4 _Grey;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 part : TEXCOORD2;   // which tube, along it
            };

            Varyings vert(Attributes v)
            {
                float which = v.uv.x, t = v.uv.y;
                float e = saturate(_Extend);
                // Housing first, then the piston out of it; the foot pad opens over the last stretch.
                float housing = _Housing * smoothstep(0.0, 0.35, e);
                float bottom = housing + (1.0 - _Housing) * smoothstep(0.3, 1.0, e);
                float pad = smoothstep(0.65, 1.0, e);
                float3 p = v.positionOS.xyz;   // the mesh: unit circles in xz (y unused), caps at radius 0
                float radius, y;
                if (which < 0.5)        { radius = 1.0;           y = -t * housing; }
                else if (which < 1.5)   { radius = _Piston;       y = -lerp(housing * 0.85, bottom - _FootHeight * pad, t); }
                else                    { radius = lerp(_Piston, _Foot, pad); y = -(bottom - _FootHeight * pad * (1.0 - t)); }
                float3 pos = float3(p.x * radius, y, p.z * radius);
                Varyings o;
                o.positionWS = TransformObjectToWorld(pos);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.part = float2(which, t);
                return o;
            }

            float4 frag(Varyings i, bool front : SV_IsFrontFace) : SV_Target
            {
                float3 n = normalize(i.normalWS) * (front ? 1.0 : -1.0);
                bool sunSet = _LnSunPosition.w > 0.5;
                float3 l = sunSet ? normalize(_LnSunPosition.xyz - i.positionWS) : normalize(float3(0.6, 0.5, -0.6));
                float sun = sunSet ? dot(_LnSunColor.rgb, float3(0.299, 0.587, 0.114)) : 1.0;   // greyscale: its brightness only
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);
                float diffuse = saturate(dot(n, l) * 0.75 + 0.25);
                // Ambient: brighter from the sky above than the ground below (their brightness, when a planet sets them).
                float sky = _LnSkyColor.w > 0.5 ? dot(_LnSkyColor.rgb, float3(0.299, 0.587, 0.114)) : 1.0;
                float ground = _LnGroundColor.w > 0.5 ? dot(_LnGroundColor.rgb, float3(0.299, 0.587, 0.114)) : 0.6;
                float ambient = _Ambient * lerp(ground, sky, n.y * 0.5 + 0.5);

                float which = i.part.x, t = i.part.y;
                float grey = which < 0.5 ? _Grey.x : which < 1.5 ? _Grey.y : _Grey.z;
                if (which < 0.5) grey *= 1.0 - 0.35 * step(0.82, frac(t * 3.0 + 0.1));   // housing: two dark bands
                if (which > 0.5 && which < 1.5) grey *= lerp(0.55, 1.0, t);              // piston: shadowed up inside
                float spec = which > 0.5 && which < 1.5 ? pow(saturate(dot(reflect(-l, n), v)), 28.0) * _Spec : 0.0;
                float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * 0.18;
                float c = grey * (ambient + diffuse * sun * 0.85) + spec * sun + rim;
                return float4(c.xxx, 1);
            }
            ENDHLSL
        }
    }
}
