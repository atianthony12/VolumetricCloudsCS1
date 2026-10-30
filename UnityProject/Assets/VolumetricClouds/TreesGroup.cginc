// The billboard trees (Custom/Trees/Group rebuilt, Sky/TreeShaders.cs). Two ways in:
// - MULTI_INSTANCE: the mid trees, TreeInfo.m_lodMaterial, up to 16 a draw (TreeManager.BeginRenderingImpl:
//   _TreeLocation[i] = (x, y + the centre's height, z, scale), _TreeColor[i] (a the tree's own wind),
//   _TreeDisableRuined[i]; the corner's index in the vertex's z, its offset in uv1);
// - SINGLE_INSTANCE: the far trees, baked into the render groups' tree layer
//   (RenderManager.m_groupLayerMaterials[TreeManager.m_treeLayer], TreeInstance.PopulateGroupData: the
//   corner at the tree + its offset, the offset again in uv1, the colour in the vertex colour, its alpha
//   the tree's own wind / 2).
// The game's surface shader decoded from its compiled code (2026-10-30, the INFOMODE_OFF programs;
// PrePassBase, PrePassFinal with its own lighting, a hand-written ShadowCaster from the sun's frame;
// Cull Off, queue AlphaTest) and kept as it is but for the vertex's motion (Trees.cginc).
// - The vertex: a square billboard (half-size the larger half-extent) facing the camera, turned toward
//   the camera's own frame as the view steepens, 1 to 1.5 times as large from 500 to 3000 m; its top
//   half swayed by the game (or VW's wind: the same lean and gusts as the near tree, at the crown).
// - The surface: as the near tree's, cut out at XYCA alpha 0.55; the light pulled toward _AmbientColor
//   where the leaf texture's alpha is low (TreeLod below).
#ifndef VW_TREES_GROUP_INCLUDED
#define VW_TREES_GROUP_INCLUDED

#include "Trees.cginc"

sampler2D _MainTex;
sampler2D _XYCAMap;
sampler2D _ShadowAMap;
sampler2D _TerrainRuinedDiffuse;
float4 _TerrainTextureTiling1;
float4 _AmbientColor;
float3 _CameraRight;
float3 _CameraUp;
float3 _CameraForward;
float3 _ShadowRight;
float3 _ShadowUp;
float3 _ShadowForward;

#ifdef MULTI_INSTANCE
float4 _TreeLocation[16];
float4 _TreeColor[16];
float _TreeDisableRuined[16];
#endif

struct Input
{
	float2 uv_MainTex;
	float4 treeColour;
	float4 treeBase;
};

// One corner of a billboard: c its centre (mesh space), s its offset from it (m), colour, speed the
// tree's own wind (0..2), amp the game's sway's saturate(_WindDirection.w x speed) as each of its
// programs writes it, ruined (1 = disasters leave it).
struct VWCorner
{
	float3 c;
	float2 s;
	float4 colour;
	float speed;
	float amp;
	float ruined;
};

VWCorner VWCornerOf(appdata_full v, bool caster)
{
	VWCorner k;
#ifdef MULTI_INSTANCE
	int i = (int)v.vertex.z;
	float4 location = _TreeLocation[i];
	k.c = float3(v.vertex.xy - v.texcoord1.xy + location.xy, location.z);
	k.s = v.texcoord1.xy * location.w;
	k.colour = _TreeColor[i];
	k.speed = k.colour.w;
	k.amp = saturate(_WindDirection.w * k.speed);
	k.ruined = _TreeDisableRuined[i];
#else
	k.c = float3(v.vertex.xy - v.texcoord1.xy, v.vertex.z);
	k.s = v.texcoord1.xy;
	// The far mesh carries the tree's own wind / 2 in its colour's alpha (TreeInstance.PopulateGroupData).
	k.colour = caster ? v.color * float4(1.0, 1.0, 1.0, 2.0) : v.color;
	k.speed = v.color.a + v.color.a;
	k.amp = caster ? saturate(k.speed * _WindDirection.w) : saturate(dot(v.color.aa, _WindDirection.ww));
	k.ruined = 0.0;
#endif
	return k;
}

// The game's sway of a billboard point p (offset its corner's offset from the centre, top its top
// half's height), or VW's wind (base the tree's foot in mesh space, place its world x, z, size the
// billboard's height as drawn).
float3 VWBillboardMotion(float3 p, float3 offset, float top, VWCorner k, float3 base, float2 place, float size)
{
	float2 ph = VWTreePhase(place);
	float angle = _SimulationTime.w * round(ph.y * 3.0 + 10.0) + ph.x * 1.772454;
	float amp = k.amp;
	float4 w = sin(angle) * _WindDirection.xzxz * (amp * amp) * sign(top) * 0.1;
	float3 game;
	game.xz = w.zw * (top + top) + p.xz;
	game.y = p.y - dot(w.xzyw, offset.xxzz);
#ifdef VW_TREES_GAME_ONLY
	return game;
#else
	float3 ours = VWWindPoint(p, base, place, size, k.speed, 0.0, float3(0.0, 1.0, 0.0));
	return _VWTreeWind.x > 0.5 ? ours : game;
#endif
}

void vert(inout appdata_full v, out Input o)
{
	UNITY_INITIALIZE_OUTPUT(Input, o);
	VWCorner k = VWCornerOf(v, false);
	float3 world = mul(unity_ObjectToWorld, float4(k.c, v.vertex.w)).xyz;
	float3 foot = float3(world.x, world.y - abs(k.s.y), world.z);
	o.treeColour = k.colour;
	o.treeBase = float4(foot, k.ruined);

	float3 toTree = foot - _WorldSpaceCameraPos;
	float far = saturate((length(toTree) - 500.0) * 0.0004);
	float grow = far * far * (far * -2.0 + 3.0) * 0.5 + 1.0;
	float3 d = normalize(toTree);
	float3 side = cross(float3(0.0, 1.0, 0.0), d);
	float3 right0 = side * rsqrt(dot(side.xz, side.xz));
	float3 up0 = cross(d, right0);
	float steep = d.y * d.y;
	float3 up = lerp(up0, _CameraUp, steep);
	float3 right = lerp(right0, _CameraRight, steep);
	float3 ahead = lerp(d, _CameraForward, steep);

	float sy = sign(k.s.y);
	float extent = max(abs(k.s.y), abs(k.s.x));
	float signedHalf = extent * sy;
	float3 offset = (right * k.s.x) * grow + (up * signedHalf) * grow;
	float top = max(signedHalf, 0.0);
	offset -= ahead * top;
	float3 p = k.c + offset;

	// Looked at from above: the billboard slid back along the view, as the game does.
	float down = min(d.y, 0.0) / min(_CameraForward.y, -0.001);
	float2 h = d.xz - _CameraForward.xz * down;
	float flip = sign(dot(h, _CameraRight.xz));
	float lean = (2.0 * k.s.x * flip - sy * extent);
	lean = min(saturate(-dot(h, _CameraUp.xz)) * lean + signedHalf, 0.0);
	p.xz -= lean * (h * min(_CameraForward.y, 0.0) * (_CameraForward.y * _CameraForward.y)) * grow * 1.5;

	float3 footMesh = float3(k.c.x, k.c.y - abs(k.s.y), k.c.z);
	v.vertex = float4(VWBillboardMotion(p, offset, top, k, footMesh, foot.xz, 2.0 * extent * grow), 1.0);
	v.normal = -_CameraForward;
	v.tangent = float4(_CameraRight, -1.0);
}

void surf(Input IN, inout SurfaceOutput o)
{
	float burned, ruined, wilt;
	VWTreeDamage(IN.treeBase.xz, burned, ruined, wilt);
	float4 burn = burned * float4(0.25, 0.0, -0.25, 0.4);
	float keep = 1.0 - IN.treeBase.w;
	float4 xyca = tex2D(_XYCAMap, IN.uv_MainTex);
	clip((xyca.a - 0.55) - max(sqrt(ruined) * 0.43, burn.w) * keep);

	float3 tint = burn.xyz * keep + IN.treeColour.rgb;
	float4 leaf = tex2D(_MainTex, IN.uv_MainTex);
	float4 colour = float4(lerp(leaf.rgb, leaf.rgb * tint, xyca.z), leaf.a);
	float4 rubble = tex2D(_TerrainRuinedDiffuse, IN.treeBase.xz * _TerrainTextureTiling1.y + IN.uv_MainTex);
	rubble.rgb *= wilt;
	colour = lerp(colour, rubble, ruined * keep);
	o.Albedo = colour.rgb;
	o.Alpha = colour.a;

	float2 nm = xyca.xy * 2.0 - 1.0;
	o.Normal = float3(nm, sqrt(1.0 - min(dot(nm, nm), 1.0)));
}

// The game's billboard light: the light buffer's pulled toward _AmbientColor (by its w) where the
// leaf texture's alpha is low.
half4 LightingTreeLod_PrePass(SurfaceOutput s, half4 light)
{
	light.rgb = lerp(light.rgb, _AmbientColor.rgb, (1.0 - s.Alpha) * _AmbientColor.w);
	half4 c;
	c.rgb = s.Albedo * light.rgb;
	c.a = s.Alpha;
	return c;
}

half4 LightingTreeLod(SurfaceOutput s, half3 lightDir, half atten)
{
	return half4(s.Albedo, s.Alpha);
}

// The shadow caster: the billboard faces the sun (the game's _ShadowRight/Up/Forward), cut out by
// _ShadowAMap's alpha at 0.55.
struct VWCasterV2F
{
	float4 pos : SV_POSITION;
	float4 colour : COLOR;
	float4 uv : TEXCOORD0;
	float4 treeBase : TEXCOORD1;
};

VWCasterV2F vertCaster(appdata_full v)
{
	VWCasterV2F o;
	VWCorner k = VWCornerOf(v, true);
	float3 world = mul(unity_ObjectToWorld, float4(k.c, v.vertex.w)).xyz;
	float3 foot = float3(world.x, world.y - abs(k.s.y), world.z);
	float far = saturate((length(foot - _WorldSpaceCameraPos) - 500.0) * 0.0004);
	float grow = far * far * (far * -2.0 + 3.0) * 0.5 + 1.0;

	float sy = sign(k.s.y);
	float extent = max(abs(k.s.y), abs(k.s.x));
	float signedHalf = extent * sy;
	float3 offset = (k.s.x * _ShadowRight) * grow + (signedHalf * _ShadowUp) * grow;
	float top = max(signedHalf, 0.0);
	offset -= (min(signedHalf, 0.0) * _ShadowForward * min(_ShadowForward.y, 0.0)) * _ShadowForward.y;
	float3 p = k.c + offset;

	float3 footMesh = float3(k.c.x, k.c.y - abs(k.s.y), k.c.z);
	p = VWBillboardMotion(p, offset, top, k, footMesh, foot.xz, 2.0 * extent * grow);
	o.pos = VWGameShadowBias(UnityObjectToClipPos(p));
	o.colour = k.colour;
	o.uv = float4(v.texcoord.xy, 0.0, 0.0);
	o.treeBase = float4(foot, k.ruined);
	return o;
}

float4 fragCaster(VWCasterV2F i) : SV_Target
{
	float burned, ruined, wilt;
	VWTreeDamage(i.treeBase.xz, burned, ruined, wilt);
	float a = tex2D(_ShadowAMap, i.uv.xy).a;
	clip((a - 0.55) - max(sqrt(ruined) * 0.43, burned * 0.4) * (1.0 - i.treeBase.w));
	return float4(1.0, 1.0, 1.0, 1.0);
}

#endif
