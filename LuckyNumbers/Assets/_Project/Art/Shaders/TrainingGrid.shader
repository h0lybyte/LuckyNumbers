// The Flight School's deck (Planet/FlightSchoolPresenter): a dark plate with glowing grid lines in world space
// (minor every _Minor m, major every _Major m), a pad ring in the middle, and everything fading into the dark
// towards the edge, so moving over it shows your speed and height. Opaque (writes depth), so the engine flames
// and cold-gas puffs drawn after it layer over it properly. Lines are anti-aliased with screen-space derivatives.
Shader "LuckyNumbers/TrainingGrid"
{
    Properties
    {
        _Base ("Plate", Color) = (0.02, 0.03, 0.05, 1)
        _Line ("Lines", Color) = (0.1, 0.55, 0.9, 1)
        _Pad ("Pad Ring", Color) = (0.2, 0.95, 0.6, 1)
        _Minor ("Minor Spacing (m)", Float) = 10
        _Major ("Major Spacing (m)", Float) = 100
        _PadRadius ("Pad Radius (m)", Float) = 30
        _Fade ("Fade Radius (m)", Float) = 550
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

            CBUFFER_START(UnityPerMaterial)
                float4 _Base, _Line, _Pad;
                float _Minor, _Major, _PadRadius, _Fade;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            // 1 on a line every `spacing` m, `width` pixels wide, fading out where lines get denser than pixels.
            float Grid(float2 p, float spacing, float width)
            {
                float2 q = p / spacing;
                float2 d = fwidth(q);
                float2 g = abs(frac(q - 0.5) - 0.5) / max(d, 1e-5);
                float l = 1.0 - saturate(min(g.x, g.y) - width + 1.0);
                return l * saturate(1.0 - max(d.x, d.y) * 3.0);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 p = i.positionWS.xz;
                float minor = Grid(p, _Minor, 1.0) * 0.35;
                float major = Grid(p, _Major, 1.6);
                float r = length(p);
                float rd = fwidth(r);
                float ring = 1.0 - saturate(abs(r - _PadRadius) / max(rd, 1e-5) - 1.5);
                float centre = 1.0 - saturate(abs(r - _PadRadius * 0.08) / max(rd, 1e-5) - 1.0);
                float fade = 1.0 - smoothstep(_Fade * 0.6, _Fade, r);
                float3 c = _Base.rgb + _Line.rgb * max(minor, major) * fade + _Pad.rgb * max(ring, centre);
                return float4(c, 1);
            }
            ENDHLSL
        }
    }
}
