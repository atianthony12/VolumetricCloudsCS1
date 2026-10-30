// THE TREE BENCH ONLY (tools/tree-bench.ps1; not shipped): Trees.cginc's VWWindPoint on a list of points,
// one a pixel of a float target (point i at pixel i of a row t3.y wide), for the C# twin
// (Sky/TreeWind.PointAt). Inputs: the point in POSITION, t0 (base xyz, height), t1 (place xz, speed,
// flutter), t2 (normal), t3 (index, count).
Shader "Hidden/VW/TreeBenchWindProbe"
{
	SubShader
	{
		Pass
		{
			ZTest Always ZWrite Off Cull Off

			CGPROGRAM
			#pragma target 3.5
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "Trees.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float4 t0 : TEXCOORD0;
				float4 t1 : TEXCOORD1;
				float4 t2 : TEXCOORD2;
				float4 t3 : TEXCOORD3;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float4 moved : TEXCOORD0;
			};

			v2f vert(appdata v)
			{
				v2f o;
				o.moved = float4(VWWindPoint(v.vertex.xyz, v.t0.xyz, v.t1.xy, v.t0.w, v.t1.z, v.t1.w, v.t2.xyz), 1.0);
				o.pos = float4((v.t3.x + 0.5) / v.t3.y * 2.0 - 1.0, 0.0, 0.5, 1.0);
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				return i.moved;
			}
			ENDCG
		}

		// Pass 1: the gust at each point's place, and its two octaves of noise.
		Pass
		{
			ZTest Always ZWrite Off Cull Off

			CGPROGRAM
			#pragma target 3.5
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "Trees.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float4 t1 : TEXCOORD1;
				float4 t3 : TEXCOORD3;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float4 gust : TEXCOORD0;
			};

			v2f vert(appdata v)
			{
				v2f o;
				float2 p = (v.t1.xy - _VWTreeGust.xy) * _VWTreeGust.z;
				o.gust = float4(VWGust(v.t1.xy), VWValueNoise(p, VW_GUST_TILE), VWValueNoise(p * 2.5, VW_GUST_TILE * 2.5), 1.0);
				o.pos = float4((v.t3.x + 0.5) / v.t3.y * 2.0 - 1.0, 0.0, 0.5, 1.0);
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				return i.gust;
			}
			ENDCG
		}
	}
}
