// Distant galaxy on a quad, always facing the camera: log-spiral arms around a bright core, fBm dust,
// squash + roll to fake the viewing angle. Additive, so it glows over the skybox.
Shader "LuckyNumbers/Galaxy"
{
    Properties
    {
        _CoreColor ("Core Color", Color) = (1, 0.9, 0.7, 1)
        _ArmColor ("Arm Color", Color) = (0.5, 0.6, 1, 1)
        _ArmCount ("Arm Count (0 = elliptical)", Float) = 2
        _Twist ("Twist", Float) = 3
        _Brightness ("Brightness", Float) = 1
        _CoreSize ("Core Size", Float) = 0.15
        _Squash ("Squash (0 face-on .. 1 edge-on)", Range(0, 0.95)) = 0.3
        _RollDegrees ("Roll (degrees)", Float) = 0
        _Dust ("Dust", Range(0, 1)) = 0.5
        _Seed ("Seed", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/Noise.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _CoreColor, _ArmColor;
                float _ArmCount, _Twist, _Brightness, _CoreSize, _Squash, _RollDegrees, _Dust, _Seed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 p : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 center = TransformObjectToWorld(float3(0, 0, 0));
                float scale = length(float3(UNITY_MATRIX_M._m00, UNITY_MATRIX_M._m10, UNITY_MATRIX_M._m20));
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;   // camera axes in world space
                float3 ws = center + (right * v.positionOS.x + up * v.positionOS.y) * scale;
                o.positionCS = TransformWorldToHClip(ws);
                o.p = (v.uv - 0.5) * 2.0;                                       // -1..1
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float a0 = radians(_RollDegrees), s = sin(a0), c = cos(a0);
                float2 p = float2(i.p.x * c - i.p.y * s, i.p.x * s + i.p.y * c);
                p.y /= max(1.0 - _Squash, 0.05);                                // viewing angle
                float r = length(p);
                if (r > 1.0) return 0;

                float angle = atan2(p.y, p.x);
                float arms = _ArmCount > 0.5
                    ? pow(saturate(0.5 + 0.5 * cos(_ArmCount * (angle - _Twist * log(r + 0.02)))), 3.0)
                    : 1.0;
                float disk = exp(-r * 4.0);
                float core = exp(-(r * r) / max(_CoreSize * _CoreSize, 1e-4));
                float dust = lerp(1.0, saturate(ln_fbm(p * 5.0 + _Seed, 4, 0.5, 0.5, 2.0) * 0.7 + 0.6), _Dust);
                float edge = ln_smoothstep_down(0.75, 1.0, r);

                float3 color = (_CoreColor.rgb * core + _ArmColor.rgb * arms * disk * dust) * _Brightness * edge;
                return float4(color, 0);
            }
            ENDHLSL
        }
    }
}
