Shader "Airway/VertexColorHeatmap"
{
    Properties
    {
        _Ambient ("Ambient", Range(0,1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Ambient;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 color : COLOR;
                float3 wnrm  : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.wnrm  = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // shading nhẹ để thấy hình khối, vẫn giữ đúng màu heatmap
                float3 L = normalize(float3(0.4, 1.0, 0.3));
                float ndl = saturate(dot(normalize(i.wnrm), L));
                float lit = _Ambient + (1.0 - _Ambient) * ndl;
                return fixed4(i.color.rgb * lit, 1.0);
            }
            ENDCG
        }
    }

    FallBack "Diffuse"
}
