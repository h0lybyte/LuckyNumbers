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
