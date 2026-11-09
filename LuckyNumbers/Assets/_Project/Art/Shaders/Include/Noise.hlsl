#ifndef LUCKYNUMBERS_NOISE_INCLUDED
#define LUCKYNUMBERS_NOISE_INCLUDED

// 2D simplex noise + fBm, after The Book of Shaders
// (https://thebookofshaders.com/edit.php#11/2d-snoise-clear.frag, https://thebookofshaders.com/13/).

float3 ln_mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float2 ln_mod289(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 ln_permute(float3 x) { return ln_mod289(((x * 34.0) + 1.0) * x); }

float ln_simplex(float2 v)
{
    const float4 C = float4(0.211324865405187,   // (3.0-sqrt(3.0))/6.0
                            0.366025403784439,   // 0.5*(sqrt(3.0)-1.0)
                            -0.577350269189626,  // -1.0 + 2.0 * C.x
                            0.024390243902439);  // 1.0 / 41.0

    float2 i  = floor(v + dot(v, C.yy));
    float2 x0 = v - i + dot(i, C.xx);
    float2 i1 = (x0.x > x0.y) ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float2 x1 = x0 + C.xx - i1;
    float2 x2 = x0 + C.zz;

    i = ln_mod289(i);
    float3 p = ln_permute(ln_permute(i.y + float3(0.0, i1.y, 1.0)) + i.x + float3(0.0, i1.x, 1.0));

    float3 m = max(0.5 - float3(dot(x0, x0), dot(x1, x1), dot(x2, x2)), 0.0);
    m = m * m;
    m = m * m;

    float3 x  = 2.0 * frac(p * C.www) - 1.0;
    float3 h  = abs(x) - 0.5;
    float3 ox = floor(x + 0.5);
    float3 a0 = x - ox;
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);

    float3 g;
    g.x  = a0.x * x0.x + h.x * x0.y;
    g.yz = a0.yz * float2(x1.x, x2.x) + h.yz * float2(x1.y, x2.y);
    return 130.0 * dot(m, g);
}

// fBm with `octaves` layers; frequency multiplies, amplitude falls off per octave.
float ln_fbm(float2 st, int octaves, float amplitude, float falloff, float frequency)
{
    float value = 0.0;
    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * ln_simplex(st);
        st *= frequency;
        amplitude *= falloff;
    }
    return value;
}

// 3D simplex noise: Ian McEwan, Stefan Gustavson, Ashima Arts (webgl-noise, MIT license), ported to HLSL.
float4 ln_mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 ln_permute(float4 x) { return ln_mod289(((x * 34.0) + 1.0) * x); }
float4 ln_taylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float ln_simplex(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    const float4 D = float4(0.0, 0.5, 1.0, 2.0);

    float3 i  = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);

    float3 g  = step(x0.yzx, x0.xyz);
    float3 l  = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);

    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - D.yyy;

    i = ln_mod289(i);
    float4 p = ln_permute(ln_permute(ln_permute(
                   i.z + float4(0.0, i1.z, i2.z, 1.0))
                 + i.y + float4(0.0, i1.y, i2.y, 1.0))
                 + i.x + float4(0.0, i1.x, i2.x, 1.0));

    const float n_ = 0.142857142857; // 1/7
    float3 ns = n_ * D.wyz - D.xzx;
    float4 j  = p - 49.0 * floor(p * ns.z * ns.z);
    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x  = x_ * ns.x + ns.yyyy;
    float4 y  = y_ * ns.x + ns.yyyy;
    float4 h  = 1.0 - abs(x) - abs(y);

    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, 0.0);
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    float3 p0 = float3(a0.xy, h.x);
    float3 p1 = float3(a0.zw, h.y);
    float3 p2 = float3(a1.xy, h.z);
    float3 p3 = float3(a1.zw, h.w);
    float4 norm = ln_taylorInvSqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
    p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;

    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
}

float ln_fbm(float3 p, int octaves, float amplitude, float falloff, float frequency)
{
    float value = 0.0;
    for (int i = 0; i < octaves; i++)
    {
        value += amplitude * ln_simplex(p);
        p *= frequency;
        amplitude *= falloff;
    }
    return value;
}

float ln_hash31(float3 p)
{
    p = frac(p * float3(123.34, 456.21, 789.53));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y * p.z);
}

float3 ln_hash33(float3 p)
{
    return float3(ln_hash31(p), ln_hash31(p + 17.13), ln_hash31(p + 31.71));
}

// GLSL code often calls smoothstep(edge0, edge1, x) with edge0 > edge1 to get a falling edge.
// That is undefined in WGSL (WebGPU), so use this explicitly: 1 at x <= lo, 0 at x >= hi.
float ln_smoothstep_down(float lo, float hi, float x)
{
    return 1.0 - smoothstep(lo, hi, x);
}

float2 ln_rotate(float2 coord, float angle)
{
    coord -= 0.5;
    float s = sin(angle), c = cos(angle);
    coord = float2(coord.x * c - coord.y * s, coord.x * s + coord.y * c);
    return coord + 0.5;
}

float ln_hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

#endif
