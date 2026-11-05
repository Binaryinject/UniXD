#ifndef AKYUI_XD_TRANSPARENCY_INCLUDED
#define AKYUI_XD_TRANSPARENCY_INCLUDED

float4 _XdReferenceBackground;

float3 XdSrgbToLinear(float3 value)
{
    return value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4);
}

float3 XdLinearToSrgb(float3 value)
{
    value = max(value, 0.0);
    return value <= 0.0031308 ? value * 12.92 : 1.055 * pow(value, 1.0 / 2.4) - 0.055;
}

float4 XdMatchStraight(float4 color)
{
    #ifdef UNITY_COLORSPACE_GAMMA
        return color;
    #else
        float alpha = saturate(color.a);
        if (alpha < 0.00001 || alpha > 0.99999) return color;
        float3 background = XdSrgbToLinear(_XdReferenceBackground.rgb);
        float3 target = XdSrgbToLinear(lerp(_XdReferenceBackground.rgb, XdLinearToSrgb(color.rgb), alpha));
        float3 difference = background - color.rgb;
        float3 denominator = abs(difference) > 0.00001 ? difference : float3(1, 1, 1);
        float3 coverage = (background - target) / denominator;
        coverage = abs(difference) > 0.00001 ? coverage : alpha.xxx;
        float adjusted = saturate(max(alpha, max(coverage.r, max(coverage.g, coverage.b))));
        // Match every RGB channel over the chosen reference, not only luminance.
        // Final alpha already contains texture, vertex tint, CanvasGroup and clipping.
        color.rgb = max(0.0, (target - background * (1.0 - adjusted)) / max(adjusted, 0.00001));
        color.a = adjusted;
        return color;
    #endif
}

float4 XdMatchPremultiplied(float4 color)
{
    #ifdef UNITY_COLORSPACE_GAMMA
        return color;
    #else
        if (color.a < 0.00001 || color.a > 0.99999) return color;
        float4 straight = XdMatchStraight(float4(color.rgb / color.a, color.a));
        return float4(straight.rgb * straight.a, straight.a);
    #endif
}
#endif
