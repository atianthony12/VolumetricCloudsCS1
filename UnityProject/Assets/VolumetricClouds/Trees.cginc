// The trees' motion, shared by VolumetricClouds/TreesDefault (near trees) and
// VolumetricClouds/TreesGroup (billboards: the mid trees and the render groups' far trees), every
// pass of each, so the shadows move with the trees.
//
// THE GAME'S SWAY (Custom/Trees/Default and Custom/Trees/Group, decoded from their compiled code,
// 2026-10-30): one rigid shear per tree by its height,
//   offset = sin(angle) x _WindDirection.xz x saturate(speed x _WindDirection.w)^2 x weight x 0.1,
//   angle  = _SimulationTime.w x round(10 + 3 b) + 1.772454 a,  (a, b) = VWTreePhase(the tree's x, z),
// speed = the tree's own wind (WeatherManager.GetWindSpeed: 0..2 by its height over the wind grid;
// Tree Control multiplies it by its sway factors), weight = the vertex colour's alpha (near trees) or
// the billboard's top half. Kept as it is while VW's wind is off (_VWTreeWind.x 0, also unset).
//
// VW'S WIND (Sky/TreeWind.cs sets the globals; its C# twin TreeWind.PointAt must stay the same maths):
// - the lean: the tree bent about its base along the wind, the displacement theta h^2 / H (h the
//   point's height over the base, H the tree's), then put back at its old distance from the base so
//   the tree leans rather than shears (Sousa, "Vegetation Procedural Animation and Shading in Crysis",
//   GPU Gems 3 ch. 16, 2007: the main bending);
// - the gusts: a value-noise field (two octaves) drifting downwind across the map, the lean x
//   1 +- the gust strength where it passes;
// - each tree's own sway round its lean: three rates round its natural one (from its size: smaller
//   trees faster) that never line up, so it is not a metronome, and some across the wind; stronger
//   where a gust passes, quieter in a lull;
// - the branches: each part of the crown swinging on its own (a phase that changes every couple of
//   metres) along the wind, up and down and across, more the higher and farther out it is (Sousa's
//   detail bending);
// - the leaves: a quicker, finer motion every way (their normal, across, up), growing with the
//   square of the wind, so a storm throws the foliage about;
// - branches and leaves weighted by the game's own sway weight (the vertex colour's alpha); near
//   trees only. Every rate is a whole number of cycles in the clock's period, so its wrap never jumps.
// Every per-tree number comes from the tree's x, z and size only, so a near tree and its billboard
// move alike at the handover. All times and drifts are wrapped in C# (no shader sees a drift).
#ifndef VW_TREES_INCLUDED
#define VW_TREES_INCLUDED

float4 _SimulationTime;
float4 _WindDirection;

float4 _VWTreeWind;    // x 1 = VW's wind, y the lean (radians, before the tree's own wind and the gusts), z the gust strength, w the flutter (m)
float4 _VWTreeWindDir; // xyz the way the wind blows (world, level, unit), w the clock: 0..1 over _VWTreeClock.x seconds
float4 _VWTreeGust;    // xy the gust field's drift (m, wrapped to its tile), z 1 / its cell (m), w the sway's share of the lean
float4 _VWTreeClock;   // x the clock's period (s), y the share across the wind, z the branches' swing (share of the tree's size), w unused

// The gust field's tile, in cells of its first octave (the second is 2.5 x finer, 320 cells): the
// drift is wrapped to it in C# (TreeWind.GustTileCells).
#define VW_GUST_TILE 128.0

// The game's per-tree phase from the tree's place: x the phase, y the seed of its speed.
float2 VWTreePhase(float2 xz)
{
	float4 s = sin(xz.xyxy * float4(0.1, 0.1, 0.37, 0.37));
	return s.yw * s.xz;
}

// The game's sway of a near tree's vertex v (mesh space; weight its colour's alpha, speed _Color.w).
float3 VWGameSway(float3 v, float weight, float speed, float2 treeXZ)
{
	float2 p = VWTreePhase(treeXZ);
	float angle = _SimulationTime.w * round(p.y * 3.0 + 10.0) + p.x * 1.772454;
	float amp = saturate(speed * _WindDirection.w);
	float4 w = sin(angle) * _WindDirection.xzxz * (amp * amp) * weight * 0.1;
	float3 q;
	q.y = v.y - dot(w.xzyw, v.xxzz);
	q.xz = w.zw * v.y + v.xz;
	return q;
}

// 0..1 from a cell (whole, not negative): pcg2d (Jarzynski & Olano, "Hash Functions for GPU Rendering",
// JCGT 2020), in integers so every program and the C# twin agree to the bit. A float hash ("hash without
// sine") was tried first: where its last product lands within an ulp of a whole number its frac flips
// 0 <-> 1 by rounding alone (the bench found cells where the GPU and C# differed by 0.99), which could set
// a tree's shadow pass against its own drawing.
uint2 VWPcg2d(uint2 v)
{
	v = v * 1664525u + 1013904223u;
	v.x += v.y * 1664525u;
	v.y += v.x * 1664525u;
	v = v ^ (v >> 16u);
	v.x += v.y * 1664525u;
	v.y += v.x * 1664525u;
	v = v ^ (v >> 16u);
	return v;
}

float VWHash(float2 cell)
{
	return (float)(VWPcg2d((uint2)cell).x >> 8u) * (1.0 / 16777216.0);
}

// Value noise, smoothstepped, tiling every `tile` cells.
float VWValueNoise(float2 p, float tile)
{
	float2 i = floor(p);
	float2 f = p - i;
	float2 u = f * f * (3.0 - 2.0 * f);
	float2 i0 = i - tile * floor(i / tile);
	float2 i1 = i0 + 1.0;
	i1 = i1 >= tile ? i1 - tile : i1;
	float a = VWHash(i0);
	float b = VWHash(float2(i1.x, i0.y));
	float c = VWHash(float2(i0.x, i1.y));
	float d = VWHash(i1);
	return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// The gusts at a place: 0 a lull .. 1 a gust.
float VWGust(float2 xz)
{
	float2 p = (xz - _VWTreeGust.xy) * _VWTreeGust.z;
	float g = 0.65 * VWValueNoise(p, VW_GUST_TILE) + 0.35 * VWValueNoise(p * 2.5, VW_GUST_TILE * 2.5);
	return smoothstep(0.3, 0.75, g);
}

// Two phases (0..1, used as cycles) from a tree's place: continuous, different a few metres apart. The
// sines' arguments in turns, reduced before the 2 pi (map coordinates would give them hundreds of radians).
float2 VWPlacePhase(float2 xz)
{
	float2 turns = frac(float2(0.0113000 * xz.x - 0.0148014 * xz.y, 0.0084352 * xz.y + 0.0138465 * xz.x));
	float2 s = sin(6.2831853 * turns);
	return float2(
		frac(0.137 * xz.x + 0.211 * xz.y + 1.7 * s.x),
		frac(0.173 * xz.x - 0.119 * xz.y + 1.3 * s.y));
}

// The sine of an angle in turns, reduced first: a GPU's sin of hundreds of radians is off by a tenth
// of a radian (the bench's C# twin found it).
float VWSinTurns(float turns)
{
	return sin(6.2831853 * frac(turns));
}

// VW's wind on point p of a tree whose base is b (p, b, n in a frame whose y is up and level as the
// world's; place = the tree's world x, z). height: the tree's size (m); speed: its own wind (the
// game's, 1 on open ground; Tree Control's factor in it); flutter: the point's sway weight (0 on
// billboards); n: its normal.
float3 VWWindPoint(float3 p, float3 b, float2 place, float height, float speed, float flutter, float3 n)
{
	float H = max(height, 1.0);
	// Small trees and bushes are stiffer and sheltered: everything x (H / 12 m)^0.75, 0.3 to 1
	// (a 3 m bush 35%, a 6 m tree 59%, 12 m and up 100%).
	float k = clamp(speed, 0.0, 2.0) * pow(clamp(H * (1.0 / 12.0), 0.3, 1.0), 0.75);
	float gust = VWGust(place);
	float lean = _VWTreeWind.y * k;
	float mean = lean * max(0.0, 1.0 + _VWTreeWind.z * (2.0 * gust - 1.0));
	// How hard the air churns here: half in a lull, one and a half in a gust.
	float stir = 0.5 + gust;

	// Every rate a whole number of cycles in the clock's period, all from the trunk's own (c, from the
	// tree's size: about 1.2 / sqrt(H) Hz, 0.38 Hz for a 10 m tree); each sine's argument kept in turns
	// (VWSinTurns).
	float2 phase = VWPlacePhase(place);
	float clock = _VWTreeWindDir.w;
	float f0 = clamp(1.2 * rsqrt(H), 0.2, 0.9);
	float c = max(1.0, round(f0 * _VWTreeClock.x));
	float sway = 0.6 * VWSinTurns(c * clock + phase.x)
	           + 0.3 * VWSinTurns(round(c * 1.618) * clock + phase.y)
	           + 0.35 * VWSinTurns(max(1.0, round(c * 0.41)) * clock + phase.x + phase.y);
	float across = 0.7 * VWSinTurns(max(1.0, round(c * 0.87)) * clock + phase.y)
	             + 0.3 * VWSinTurns(round(c * 2.13) * clock + phase.x);

	// The trunk: the lean and its sway, bent about the base and kept at its length.
	float3 dir = _VWTreeWindDir.xyz;
	float3 side = float3(-dir.z, 0.0, dir.x);
	float3 r = p - b;
	float h = max(r.y, 0.0);
	float along = mean + lean * _VWTreeGust.w * stir * sway;
	float aside = lean * _VWTreeClock.y * stir * across;
	float3 bent = r + (dir * along + side * aside) * (h * h / H);
	bent *= sqrt(dot(r, r) / max(dot(bent, bent), 1e-8));

	// The branches: each part of the crown on its own phase (about every 2 m), the outer and upper
	// parts most.
	float limb = flutter * saturate(h / (0.3 * H)) * (0.4 + 0.6 * saturate(length(r.xz) / (0.3 * H)));
	float spot = dot(r, float3(0.23, 0.17, 0.29));
	float b1 = VWSinTurns(round(c * 2.6) * clock + spot + phase.x);
	float b2 = VWSinTurns(round(c * 3.7) * clock + spot * 1.3 + phase.y);
	float b3 = VWSinTurns(round(c * 3.1) * clock - spot * 0.8 + phase.x + phase.y);
	bent += (dir * (0.3 + 0.5 * b1) + float3(0.0, 0.45 * b2, 0.0) + side * (0.35 * b3)) * (_VWTreeClock.z * H * k * stir * limb);

	// The leaves: quicker (1.8 to 3.6 Hz), finer (a phase about every 0.9 m), every way.
	float cl = max(1.0, round(clamp(f0 * 6.0, 1.8, 3.6) * _VWTreeClock.x));
	float leaf = dot(r, float3(0.61, 0.83, 0.47));
	float l1 = VWSinTurns(cl * clock + leaf + phase.x);
	float l2 = VWSinTurns(round(cl * 1.43) * clock + leaf * 0.7 + phase.y);
	float l3 = VWSinTurns(round(cl * 1.19) * clock - leaf * 1.3 + phase.x);
	bent += (n * l1 + side * (0.7 * l2) + float3(0.0, 0.5 * l3, 0.0) + dir * (0.25 + 0.35 * l2)) * (_VWTreeWind.w * flutter * k * (0.4 + 0.8 * gust));
	return b + bent;
}

// The game's shadow-caster bias, as its casters do it: Unity's UnityApplyLinearShadowBias with twice
// unity_LightShadowBias.x (read from the compiled code).
float4 VWGameShadowBias(float4 clipPos)
{
#if defined(UNITY_REVERSED_Z)
	clipPos.z += max(-1.0, min((unity_LightShadowBias.x + unity_LightShadowBias.x) / clipPos.w, 0.0));
	float clamped = min(clipPos.z, clipPos.w * UNITY_NEAR_CLIP_VALUE);
#else
	clipPos.z += saturate((unity_LightShadowBias.x + unity_LightShadowBias.x) / clipPos.w);
	float clamped = max(clipPos.z, clipPos.w * UNITY_NEAR_CLIP_VALUE);
#endif
	clipPos.z = lerp(clipPos.z, clamped, unity_LightShadowBias.y);
	return clipPos;
}

// The game's damage to a tree from its place: burned (fire), ruined (disasters), from its maps.
float4 _NaturalResourceMapping;
sampler2D _NaturalResources;
sampler2D _NaturalDestruction;

void VWTreeDamage(float2 xz, out float burned, out float ruined, out float wilt)
{
	float2 uv = xz * _NaturalResourceMapping.z + _NaturalResourceMapping.xy;
	float4 res = tex2D(_NaturalResources, uv);
	float4 dest = tex2D(_NaturalDestruction, uv);
	burned = saturate(max(res.z * 16.0 - 12.0, dest.x));
	ruined = saturate(dest.y * 1.5);
	wilt = saturate(-(1.0 - (2.0 - dest.y)));
	wilt = wilt * wilt * (wilt * -2.0 + 3.0);
}

#endif
