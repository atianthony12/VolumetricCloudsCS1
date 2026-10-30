// The near trees (Custom/Trees/Default rebuilt, Sky/TreeShaders.cs): one Graphics.DrawMesh per tree
// (TreeInstance.RenderInstance, TreeInfo.m_material; _Color, _ObjectIndex and _DisableRuined in the
// draw's property block, _Color.a the tree's own wind). The game's surface shader decoded from its
// compiled code (2026-10-30, the INFOMODE_OFF programs; its passes PrePassBase, PrePassFinal and a
// hand-written ShadowCaster, Cull Off, queue AlphaTest): kept as it is but for the vertex's motion
// (Trees.cginc). Defining VW_TREES_GAME_ONLY leaves out VW's wind (the bench's twin of the game).
//
// - The vertex: moved (the game's sway or VW's wind); the surface keeps the mesh's own point, normal
//   and tangent (the normal and the snow are worked out from the UNMOVED point, as the game does), and
//   the surface shader's frame is the mesh's own (normal z, tangent x): its normal is in mesh space.
// - The surface: the leaf texture tinted by _Color through the XYCA map's blue, burned and ruined from
//   the natural-resource maps, snow (SNOW_ON) from the terrain's grass texture by the XYCA's blue; cut
//   out at XYCA alpha 0.5 (0.6 in the caster); the normal: the mesh normal bent outward from the trunk
//   by the XYCA's blue, then the XYCA's normal map on it. Lambert: the normals buffer's alpha is 0
//   (Real Light reads that as "thin").
#ifndef VW_TREES_DEFAULT_INCLUDED
#define VW_TREES_DEFAULT_INCLUDED

#include "Trees.cginc"

sampler2D _MainTex;
sampler2D _XYCAMap;
sampler2D _TerrainRuinedDiffuse;
sampler2D _TerrainGrassDiffuse;
float4 _Color;
float4 _TerrainTextureTiling1;
float4 _TerrainTextureTiling2;
float _DisableRuined;
float _DisableSnow;

// The tree's size (m in its mesh) for VW's wind: TreeShaders sets it on each TreeInfo's material (the
// billboard's: twice its larger half-extent, so a near tree and its billboard sway alike).
float _VWTreeSize;

struct Input
{
	float2 uv_MainTex;
	float3 treeVertex;
	float3 treeNormal;
	float4 treeTangent;
};

// The point v (mesh space) of this tree, moved: the game's sway, or VW's wind.
float3 VWTreeVertex(float3 v, float3 normal, float weight)
{
	float3 treePos = unity_ObjectToWorld._m03_m13_m23;
	float3 game = VWGameSway(v, weight, _Color.w, treePos.xz);
#ifdef VW_TREES_GAME_ONLY
	return game;
#else
	float3 world = mul(unity_ObjectToWorld, float4(v, 1.0)).xyz;
	float scale = length(unity_ObjectToWorld._m01_m11_m21);
	float3 n = normalize(mul((float3x3)unity_ObjectToWorld, normal));
	float3 moved = VWWindPoint(world, treePos, treePos.xz, _VWTreeSize * scale, _Color.w, weight, n);
	float3 ours = mul(unity_WorldToObject, float4(moved, 1.0)).xyz;
	return _VWTreeWind.x > 0.5 ? ours : game;
#endif
}

void vert(inout appdata_full v, out Input o)
{
	UNITY_INITIALIZE_OUTPUT(Input, o);
	o.treeVertex = v.vertex.xyz;
	o.treeNormal = v.normal;
	o.treeTangent = v.tangent;
	v.vertex.xyz = VWTreeVertex(v.vertex.xyz, v.normal, v.color.a);
	v.normal = float3(0.0, 0.0, 1.0);
	v.tangent = float4(1.0, 0.0, 0.0, 1.0);
}

void surf(Input IN, inout SurfaceOutput o)
{
	float3 treePos = unity_ObjectToWorld._m03_m13_m23;
	float burned, ruined, wilt;
	VWTreeDamage(treePos.xz, burned, ruined, wilt);
	float4 burn = burned * float4(0.25, 0.0, -0.25, 0.4);
	float keep = 1.0 - _DisableRuined;
	float4 xyca = tex2D(_XYCAMap, IN.uv_MainTex);
	clip((0.5 - xyca.a) - max(sqrt(ruined) * 0.49, burn.w) * keep);

	float3 tint = burn.xyz * keep + _Color.rgb;
	float3 leaf = tex2D(_MainTex, IN.uv_MainTex).rgb;
#ifdef SNOW_ON
	float4 snow = tex2D(_TerrainGrassDiffuse, IN.treeVertex.xz * _TerrainTextureTiling2.x);
	float bare = saturate(-(snow.a * (1.0 - _DisableSnow) - (2.0 - xyca.z)));
	bare = bare * bare * (bare * -2.0 + 3.0);
	float3 base = lerp(snow.rgb, leaf, bare);
	float tinted = bare * xyca.z;
#else
	float3 base = leaf;
	float tinted = xyca.z;
#endif
	float3 colour = lerp(base, base * tint, tinted);
	float3 rubble = tex2D(_TerrainRuinedDiffuse, (IN.treeVertex.xz + treePos.xz) * _TerrainTextureTiling1.y).rgb;
	o.Albedo = lerp(colour, rubble * wilt, ruined * keep);

	// The normal, in the mesh's frame: bent outward from the trunk, then the normal map on it.
	float2 out2 = IN.treeVertex.xz * rsqrt(dot(IN.treeVertex.xz, IN.treeVertex.xz));
	float3 bend = float3(tinted * out2.x, IN.treeVertex.y * 0.2 + 4.0, tinted * out2.y);
	float3 n = normalize(bend * float3(3.0, tinted, 3.0) + IN.treeNormal);
	float3 b = normalize(cross(n, IN.treeTangent.xyz) * IN.treeTangent.w);
	float3 t = cross(b, n);
	float2 nm = xyca.xy * 2.0 - 1.0;
	o.Normal = b * nm.y + t * nm.x + n * sqrt(1.0 - min(dot(nm, nm), 1.0));
}

// The shadow caster (hand-written in the game's: no keywords, cut out at 0.6, the bias doubled).
struct VWCasterV2F
{
	float4 pos : SV_POSITION;
	float4 uv : TEXCOORD0;
	float3 treePos : TEXCOORD1;
};

VWCasterV2F vertCaster(appdata_full v)
{
	VWCasterV2F o;
	float3 p = VWTreeVertex(v.vertex.xyz, v.normal, v.color.a);
	o.pos = VWGameShadowBias(UnityObjectToClipPos(p));
	o.uv = float4(v.texcoord.xy, 0.0, 0.0);
	o.treePos = unity_ObjectToWorld._m03_m13_m23;
	return o;
}

float4 fragCaster(VWCasterV2F i) : SV_Target
{
	float burned, ruined, wilt;
	VWTreeDamage(i.treePos.xz, burned, ruined, wilt);
	float4 xyca = tex2D(_XYCAMap, i.uv.xy);
	clip((0.6 - xyca.a) - max(sqrt(ruined) * 0.49, burned * 0.4) * (1.0 - _DisableRuined));
	return float4(1.0, 1.0, 1.0, 1.0);
}

#endif
