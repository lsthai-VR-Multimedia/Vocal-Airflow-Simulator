// Shader phụ trợ cho ParticleVisibilityLogger.cs — copy _CameraDepthTexture (độ sâu GPU của
// camera vừa render) ra 1 RenderTexture đọc được từ CPU (ReadPixels). Chỉ dùng cho mục đích đo
// đạc/thí nghiệm, không phải shader hiển thị.
Shader "Hidden/CopyCameraDepth"
{
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _CameraDepthTexture;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Ghi ra khoảng cách thật (eye depth, đơn vị world unit) từ camera tới vật gần nhất
            // tại mỗi pixel — để so sánh trực tiếp với khoảng cách từ camera tới từng hạt.
            float4 frag(v2f i) : SV_Target
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                float eyeDepth = LinearEyeDepth(rawDepth);
                return float4(eyeDepth, eyeDepth, eyeDepth, 1);
            }
            ENDCG
        }
    }
}
