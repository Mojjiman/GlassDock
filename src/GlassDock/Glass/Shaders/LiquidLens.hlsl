// Liquid glass lens (ps_3_0), compiled at runtime by ShaderCompiler.
// Input and Blurred are the wallpaper exactly under the pill. All lengths are physical pixels.
// Tunables come in through constant registers (LiquidLensEffect); none are literals here.

sampler2D Input   : register(s0);   // sharp wallpaper under the pill
sampler2D Blurred : register(s1);   // the same, pre-blurred once

float2 PillSize   : register(c0);
float  Radius     : register(c1);   // corner radius
float  Bezel      : register(c2);   // width of the refracting rim
float  Refraction : register(c3);   // largest displacement, reached at the outline
float  Frost      : register(c4);   // 0 sharp .. 1 blurred, whole pill
float  Chroma     : register(c5);   // fraction by which red and blue displacement differ (dispersion)
float  RimGlow    : register(c6);   // light gathered in the rim
float  Vibrancy   : register(c7);   // saturation multiplier
float  RimFrost   : register(c8);   // extra blur towards the outline

float sdRoundRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - r;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 h = PillSize * 0.5;
    float2 p = (uv - 0.5) * PillSize;
    float  d = sdRoundRect(p, h, Radius);                 // < 0 inside

    // Outward normal of the outline from the distance field's gradient (one-pixel central difference).
    float2 n = float2(
        sdRoundRect(p + float2(1, 0), h, Radius) - sdRoundRect(p - float2(1, 0), h, Radius),
        sdRoundRect(p + float2(0, 1), h, Radius) - sdRoundRect(p - float2(0, 1), h, Radius));
    n = n / max(length(n), 1e-4);

    // Convex bevel: flat in the middle, bending steeply towards the outline.
    float t = saturate(1 + d / Bezel);
    float k = 1 - sqrt(saturate(1 - t * t));

    // Samples are pulled inwards, so lookups stay under the pill and the rim magnifies.
    float2 off = -n * Refraction * k / PillSize;
    float3 sharp = float3(
        tex2D(Input, uv + off * (1 + Chroma)).r,
        tex2D(Input, uv + off).g,
        tex2D(Input, uv + off * (1 - Chroma)).b);
    float3 soft = tex2D(Blurred, uv + off).rgb;
    float3 col = lerp(sharp, soft, saturate(Frost + RimFrost * k));

    float luma = dot(col, float3(0.299, 0.587, 0.114));
    col = lerp(luma.xxx, col, Vibrancy) + RimGlow * k * k;

    float a = saturate(0.5 - d);                            // one-pixel antialiased outline
    return float4(saturate(col) * a, a);                    // WPF expects premultiplied alpha
}
