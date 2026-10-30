// THE TREE BENCH ONLY (tools/tree-bench.ps1; not shipped): does a shader's own MotionVectors pass reach the
// camera's motion-vector texture for a mesh drawn the way the game draws its near trees (Graphics.DrawMesh,
// no renderer), under legacy deferred? Its motion pass writes a marker (0.25, 0.125) the probe looks for.
Shader "Hidden/VW/TreeBenchMotionProbe"
{
	SubShader
	{
		Tags { "RenderType" = "Opaque" }

		Pass
		{
			Tags { "LightMode" = "PrePassBase" }
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v.xyz); }
			float4 frag() : SV_Target { return float4(0.5, 0.5, 1.0, 0.0); }
			ENDCG
		}

		Pass
		{
			Tags { "LightMode" = "PrePassFinal" }
			ZWrite Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v.xyz); }
			float4 frag() : SV_Target { return float4(0.5, 0.5, 0.5, 1.0); }
			ENDCG
		}

		Pass
		{
			Tags { "LightMode" = "MotionVectors" }
			ZTest LEqual ZWrite Off Cull Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			float4 vert(float4 v : POSITION) : SV_POSITION { return UnityObjectToClipPos(v.xyz); }
			float4 frag() : SV_Target { return float4(0.25, 0.125, 0.0, 1.0); }
			ENDCG
		}
	}
}
