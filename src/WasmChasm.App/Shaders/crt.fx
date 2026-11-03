// WASM Chasm CRT effect: subtle barrel curvature, scanlines and vignette.
// Compile: fxc /nologo /T ps_2_0 /E main /Fo crt.ps crt.fx
sampler2D input : register(s0);
float curvature : register(c0);   // 0 = flat, ~0.06 subtle
float scanlines : register(c1);   // 0..1 scanline darkness
float height    : register(c2);   // render height in pixels

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 cc = uv - 0.5;
    float d = dot(cc, cc) * curvature;
    float2 p = uv + cc * d;

    if (p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1)
        return float4(0, 0, 0, 1);

    float4 c = tex2D(input, p);
    float s = 1 - scanlines * (0.5 + 0.5 * sin(p.y * height * 3.14159));
    float v = 1 - dot(cc, cc) * 0.7;
    c.rgb *= s * v;
    return c;
}
