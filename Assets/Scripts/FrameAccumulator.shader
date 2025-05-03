Shader "Unlit/FrameAccumulator"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex;
            sampler2D PrevTex;
            int FrameCount;

            fixed4 frag (v2f i) : SV_Target
            {
                float4 newCol = tex2D(_MainTex, i.uv);
                float4 prevCol = tex2D(PrevTex, i.uv);

                float weight = 1.0 / (FrameCount + 1);
                float4 col = lerp(prevCol, newCol, weight);

                return saturate(col);
            }
            ENDCG
        }
    }
}
