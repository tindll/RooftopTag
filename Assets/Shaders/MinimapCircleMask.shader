Shader "RooftopTag/MinimapCircleMask"
{
    // Composites the minimap's square top-down render with a circular inclusion mask (alpha=1
    // inside the circle, 0 outside) so the result is a genuinely transparent-cornered texture —
    // GUI.DrawTexture then alpha-blends it against the 3D scene behind the HUD instead of showing
    // an opaque square backdrop. Used via Graphics.Blit(renderTexture, compositeTexture, this),
    // which bypasses the active render pipeline (a raw blit), so plain CG is fine here too.
    Properties
    {
        _MainTex ("Minimap Render", 2D) = "white" {}
        _MaskTex ("Circle Mask (alpha)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _MaskTex;

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                col.a = tex2D(_MaskTex, i.uv).a;
                return col;
            }
            ENDCG
        }
    }
}
