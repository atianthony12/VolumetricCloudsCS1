// THE CLOUD COLUMN MAP'S PASS (1.6.1): one column of sky a texel, marched straight UP through the slab with the very
// same SampleDensityLod as the visible clouds and their shadows (invariant 1), fed the same way (CloudShaderParams.Apply).
// Lightning reads it back about once a second (Sky/CloudColumnMap.cs, Sky/CloudColumns.cs) to put its bolts in the drawn
// clouds. Included by CloudColumnMap.shader and, with VC_LITE defined, by CloudColumnMapLite.shader -- the same choice
// as the shadow map's (CloudShadowMap / CloudShadowMapLite), so it always marches the density the clouds are drawn with.
//
// Out (ARGB32, linear; CloudColumns.Decode reads it):
//   r  the optical depth straight up, t / (t + 4)
//   g  where the optical depth from below first reaches 0.1 (the drawn underside), a share of the slab; 1 = never
//   b  the highest step with any cloud in it (its top), a share of the slab
//   a  where it first reaches 1 (the dense part), a share of the slab; 1 = never
// No texel is ever all zero (no cloud: g = 1): an all-zero readback = the program did not draw.

#include "UnityCG.cginc"
#include "CloudCommon.cginc"

float4 _ColumnRect;     // xy = the map's centre (world xz), z = its side (m), w = the noise's mip level for a texel
float _ColumnSteps;     // steps through the slab (CloudColumnMap: about 30 m each)
float _VCProbe;         // 1: write the check's colour and nothing else (as the other cloud passes)

#ifndef COLUMN_MOST_STEPS
#define COLUMN_MOST_STEPS 128
#endif

float4 frag(v2f_img i) : SV_Target
{
    if (_VCProbe > 0.5)
        return float4(0.25, 0.5, 0.75, 1.0);

    float2 xz = _ColumnRect.xy + (i.uv - 0.5) * _ColumnRect.z;
    float slab = _CloudTop - _CloudBottom;
    float steps = clamp(_ColumnSteps, 8.0, COLUMN_MOST_STEPS);
    float stepLen = slab / steps;

    float tau = 0.0;
    float under = 1.0;
    float dense = 1.0;
    float top = 0.0;

    [loop]
    for (int k = 0; k < COLUMN_MOST_STEPS; k++)
    {
        if ((float)k >= steps)
            break;

        float y = _CloudBottom + stepLen * ((float)k + 0.5);
        float d = SampleDensityLod(float3(xz.x, y, xz.y), true, _ColumnRect.w);
        float dt = d * stepLen * _Absorption;
        // Where in this step the depth passes 0.1 / 1, as if it grew evenly over the step.
        float into = (float)k / steps;
        if (under > 0.999 && tau + dt >= 0.1)
            under = into + saturate((0.1 - tau) / max(dt, 1e-6)) / steps;
        if (dense > 0.999 && tau + dt >= 1.0)
            dense = into + saturate((1.0 - tau) / max(dt, 1e-6)) / steps;
        // Any cloud at all in this step (2.5e-4 per metre: 0.01 of depth over 40 m).
        if (d * _Absorption > 2.5e-4)
            top = ((float)k + 1.0) / steps;
        tau += dt;
    }

    // Shares of the slab of 1 are written as 254/255 at most where reached, so "never" (exactly 1) stays apart.
    float g = under > 0.999 ? 1.0 : min(under, 254.0 / 255.0);
    float a = dense > 0.999 ? 1.0 : min(dense, 254.0 / 255.0);
    return float4(tau / (tau + 4.0), g, top, a);
}
