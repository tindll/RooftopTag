Shader "RooftopTag/GradientSkybox"
{
    Properties
    {
        _ZenithColor("Zenith", Color) = (0.23, 0.18, 0.37, 1)
        _MidColor("Mid", Color) = (0.71, 0.32, 0.32, 1)
        _HorizonColor("Horizon", Color) = (0.94, 0.56, 0.29, 1)
        _GroundColor("Ground", Color) = (1.0, 0.78, 0.45, 1)
        _SunColor("Sun", Color) = (1.0, 0.85, 0.54, 1)
        _SunDirection("Sun Direction", Vector) = (0, 0.22, 1, 0)
        _SunSize("Sun Size", Range(16, 2048)) = 384
        _MidPoint("Mid Point", Range(0.05, 0.9)) = 0.35
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            fixed4 _ZenithColor, _MidColor, _HorizonColor, _GroundColor, _SunColor;
            float4 _SunDirection;
            float _SunSize, _MidPoint;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float y = d.y;

                fixed3 sky;
                if (y < 0.0)
                {
                    // Below horizon: fade from horizon into the warm haze floor.
                    sky = lerp(_HorizonColor.rgb, _GroundColor.rgb, saturate(-y * 4.0));
                }
                else if (y < _MidPoint)
                {
                    sky = lerp(_HorizonColor.rgb, _MidColor.rgb, y / _MidPoint);
                }
                else
                {
                    sky = lerp(_MidColor.rgb, _ZenithColor.rgb, (y - _MidPoint) / (1.0 - _MidPoint));
                }

                // Sun disc + broad warm glow around it.
                float s = saturate(dot(d, normalize(_SunDirection.xyz)));
                sky += _SunColor.rgb * pow(s, _SunSize) * 1.5;
                sky += _SunColor.rgb * pow(s, 8.0) * 0.25;

                return fixed4(sky, 1);
            }
            ENDCG
        }
    }
}
