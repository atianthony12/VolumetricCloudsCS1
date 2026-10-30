// THE TREE BENCH ONLY (tools/tree-bench.ps1; not in BundleBuilder's list, not shipped): VolumetricClouds/TreesDefault
// with VW's wind compiled out = the game's, for setting its compiled programs against the game's own. Here, not in a
// folder of its own: a surface shader's nested includes resolve from its own folder.
Shader "Hidden/VW/GameTreesDefault"
{
	Properties
	{
		_MainTex ("Base", 2D) = "white" {}
		_XYCAMap ("XYCA", 2D) = "grey" {}
		_Color ("Colour", Color) = (1, 1, 1, 1)
	}

	SubShader
	{
		Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
		Cull Off

		CGPROGRAM
		#pragma target 3.5
		#pragma surface surf Lambert vertex:vert exclude_path:deferred exclude_path:forward nometa nolightmap nodynlightmap nodirlightmap noforwardadd
		#pragma multi_compile SHADING SNOW_ON
		#define VW_TREES_GAME_ONLY
		#include "TreesDefault.cginc"
		ENDCG

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZTest Less
			Cull Off

			CGPROGRAM
			#pragma target 3.5
			#pragma vertex vertCaster
			#pragma fragment fragCaster
			#include "UnityCG.cginc"
			#include "Lighting.cginc"
			#define VW_TREES_GAME_ONLY
		#include "TreesDefault.cginc"
			ENDCG
		}
	}
	Fallback Off
}
