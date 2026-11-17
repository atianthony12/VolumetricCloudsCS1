// THE FLARE round a lamp, shared by LightHalo.shader and LightHaloDynamic.shader (their headers say
// what it is and how it is set). Not from the game's shader: thin rays out from the lamp in screen
// pixels, the same maths as another mod's warning-light flare.
//
// Each shader includes this after declaring _CameraDepthTexture (sampler2D_float) and includes
// UnityCG.cginc first.
#ifndef VC_HALO_FLARE_INCLUDED
#define VC_HALO_FLARE_INCLUDED

// The player's shape controls (HaloOverride.SetFlare; 2026-11-17, "more control over flares,
// especially size"), each 1 at the designed look: the rays' length near and far (blended by the
// caller as the strengths are), their thickness, the long spikes against the fine rays (0 only
// the fine rays, 1 both, 2 only the spikes) and how many spikes. Unset (0) the strengths are 0 too
// and nothing is drawn; the guards below only keep the maths finite.
float _HaloFlareSizeNear;
float _HaloFlareSizeFar;
float _HaloFlareThickness;
float _HaloFlareSpikes;
float _HaloFlareSpikeCount;

// Vehicles' lights (2026-11-17; LightHaloDynamic only, the lights tagged while a vehicle renders):
// their strength and size in place of the near / far blend. 0 (unset): none.
float _HaloFlareVehicles;
float _HaloFlareSizeVehicles;

// The brightest a ray is, as a share of the halo's full colour; the fine rays round the lamp (the
// eye's), the long spikes (a camera's) and their first's angle (radians from the screen's right,
// anticlockwise).
#define FLARE_PEAK 0.3
#define FLARE_RAYS 36.0
#define FLARE_TILT 0.26

// A ray's length (where it has faded to 1/e) and its half width (its Gaussian's sigma), in pixels
// of a 1080-line view.
#define FLARE_LENGTH 4.0
#define FLARE_WIDTH 0.5

// Metres: the peak is held to here and falls as 1/d past it, so a far lamp's rays shrink with
// distance (held whole, a far district would be one smudge) yet a distant lamp keeps a sparkle the
// distant row can turn up (1/d^2 left ~4% at 1 km: nothing for that row to show).
#define FLARE_HOLD 200.0

// Below this (HDR, the lamp's colour in it) a ray is not drawn: the quad ends where the rays fade
// under it.
#define FLARE_FLOOR 0.0015

// One ray from the lamp out along this angle (radians): a line of Gaussian section w, fading as
// e^(-x / len) past the lamp's disc; nothing on the far side of the centre.
float FlareRay(float2 q, float angle, float lamp, float len, float w)
{
    float2 dir = float2(cos(angle), sin(angle));
    float along = dot(q, dir);
    float across = dir.x * q.y - dir.y * q.x;
    float x = max(along - lamp, 0.0);
    return along > 0.0 ? exp(-0.5 * across * across / (w * w) - x / len) : 0.0;
}

// The pattern at q (pixels from the lamp), 1 at a spike's root: the nearest of the long spikes
// (_HaloFlareSpikeCount of them, 6 as designed), and the three nearest of the fine rays -- each
// fine ray in its own slice of the circle, its angle in it, brightness and length from
// golden-ratio (Weyl) sequences on its number, so the pattern is fixed and the same round every
// lamp. A pixel sees only the rays near it. _HaloFlareSpikes weighs the two: each at full up to 1,
// then the other fades out (0: fine rays only, 1: both as designed, 2: spikes only).
float FlarePattern(float2 q, float rho, float lamp, float len, float w)
{
    float2 p = rho > 1e-3 ? q : float2(1e-3, 0.0);
    float theta = atan2(p.y, p.x);

    float spikes = max(_HaloFlareSpikeCount, 1.0);
    float spikeWeight = saturate(_HaloFlareSpikes);
    float fineWeight = saturate(2.0 - _HaloFlareSpikes);

    float spikeStep = 6.2831853 / spikes;
    float k = floor((theta - FLARE_TILT) / spikeStep + 0.5);
    float sum = spikeWeight * FlareRay(q, FLARE_TILT + k * spikeStep, lamp, len, w);

    float bin = 6.2831853 / FLARE_RAYS;
    float i0 = floor(theta / bin);
    [unroll]
    for (int j = -1; j <= 1; j++)
    {
        float i = i0 + j;
        // The ray's number, 0..rays-1, the same on both sides of the angle's wrap.
        float n = i - FLARE_RAYS * floor(i / FLARE_RAYS);
        float angle = (i + 0.5 + 0.7 * (frac(n * 0.7548777 + 0.31) - 0.5)) * bin;
        float bright = 0.15 + 0.35 * frac(n * 0.5698403 + 0.67);
        float reach = 0.3 + 0.5 * frac(n * 0.6180340 + 0.13);
        sum += fineWeight * bright * FlareRay(q, angle, lamp, len * reach, w);
    }

    return sum;
}

// One depth sample: 1 if nothing nearer than the lamp (less the bias) is there, or the point is
// off the screen (nothing there that is known to be in the way).
float FlareSeenAt(float2 uv, float eye, float bias)
{
    float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
    float raw = tex2Dlod(_CameraDepthTexture, float4(saturate(uv), 0.0, 0.0)).x;
    float scene = LinearEyeDepth(raw);
    return lerp(1.0, step(eye - bias, scene), inside);
}

// How much of the lamp is seen (0..1), for the vertex program: the depth at its centre and at four
// points a little round it, against its own depth less its housing (a lamp's light point sits in
// or under its head) and the depth buffer's precision far off. uv: the lamp's centre on the
// screen (0..1); px: a pixel in uv.
float FlareSeen(float2 uv, float eye, float2 px)
{
    float bias = 1.5 + 0.002 * eye;
    float2 r = 2.0 * px;
    float seen = FlareSeenAt(uv, eye, bias);
    seen += FlareSeenAt(uv + float2(r.x, 0.0), eye, bias);
    seen += FlareSeenAt(uv - float2(r.x, 0.0), eye, bias);
    seen += FlareSeenAt(uv + float2(0.0, r.y), eye, bias);
    seen += FlareSeenAt(uv - float2(0.0, r.y), eye, bias);
    return 0.2 * seen;
}

// The flare of one lamp, for the vertex program. strength, size: the player's (near / distant
// blended), facing: the glare's facing term, lum: the lamp's brightest channel as drawn (colour x
// intensity), lightPos: world. Returns (peak -- a share of the lamp's colour, 0 = none --, length px,
// width px, how far out it is worth drawing, METRES at the lamp's distance); centreUV: its centre
// on the screen.
float4 FlareSetup(float3 lightPos, float dist, float strength, float size, float facing, float lum, out float2 centreUV)
{
    float lines = max(_ScreenParams.y, 1.0) / 1080.0;
    float len = FLARE_LENGTH * max(size, 0.05) * lines;
    float w = FLARE_WIDTH * max(_HaloFlareThickness, 0.1) * lines;

    float4 clip = mul(UNITY_MATRIX_VP, float4(lightPos, 1.0));
    float4 screen = ComputeScreenPos(clip);
    bool ahead = clip.w > 1e-4;
    centreUV = ahead ? screen.xy / screen.w : float2(-1.0, -1.0);

    float fall = min(1.0, FLARE_HOLD / max(dist, 1e-3));
    float peak = FLARE_PEAK * max(strength, 0.0) * fall * facing;

    // Seen: only worth the five depth reads for a lamp with a flare at all.
    float eye = -mul(UNITY_MATRIX_V, float4(lightPos, 1.0)).z;
    // (An if, not ?:, which would read them for every lamp.)
    float seen = 0.0;
    [branch] if (ahead && peak * lum > FLARE_FLOOR)
        seen = FlareSeen(centreUV, eye, 1.0 / max(_ScreenParams.xy, 1.0));
    peak *= seen;

    float top = peak * lum;
    bool on = ahead && top > FLARE_FLOOR;
    float reachPx = on ? len * log(top / FLARE_FLOOR) + 2.0 : 0.0;

    // Pixels per metre at the lamp's distance (|P11|: the projection may be flipped). The distance,
    // not the depth along the view: never under the depth, so the quad is never too small.
    float pxPerMetre = 0.5 * max(_ScreenParams.y, 1.0) * abs(UNITY_MATRIX_P[1][1]) / max(dist, 1e-3);
    return float4(on ? peak : 0.0, len, w, reachPx / max(pxPerMetre, 1e-6));
}

#endif
