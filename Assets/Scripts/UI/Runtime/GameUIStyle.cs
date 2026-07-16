#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.UI;

/// <summary>
/// Shared style kit for the project's IMGUI screens (main menu, pause/settings, HUD, end screen).
/// Everything is generated in code — no authored assets, no UI Toolkit: RoundController's HUD is
/// "100% OnGUI" by convention (see its remarks near the round-clock update) and this kit is a
/// restyle of that, not a replacement for it.
///
/// Colours are LITERALS here rather than reads off TagRulesConfig/VisualThemeConfig on purpose:
/// Game.Rules already references nothing in this assembly, and pointing this one back at it for
/// two Colors would make the pair circular. The literals below mirror those configs' values —
/// <see cref="Tagger"/>/<see cref="Runner"/> are TagRulesConfig.taggerColor/runnerColor and
/// <see cref="Accent"/> is VisualThemeConfig.fogColor — and this file is the only place in UI code
/// a colour literal may live. If the theme moves, it moves here too.
///
/// Screens author their layout in a fixed 1080p design space (<see cref="DesignHeight"/>,
/// <see cref="DesignWidth"/>); the helpers scale to real pixels on the way out, so the same numbers
/// hold at 1440p/4K. Raw GUI calls should wrap their rect in <see cref="Scaled(Rect)"/>.
/// </summary>
public static class GameUIStyle
{
    // ---------------------------------------------------------------- Palette

    /// <summary>Charcoal panel backdrop. Deliberately not opaque — the golden-hour scene reads through it.</summary>
    public static readonly Color PanelBg = new(0x1A / 255f, 0x1A / 255f, 0x20 / 255f, 0.92f);
    /// <summary>VisualThemeConfig.fogColor exactly — the golden-hour haze is the UI's accent family.</summary>
    public static readonly Color Accent = new Color32(0xD9, 0x90, 0x6A, 0xFF);
    /// <summary>Accent lifted toward the sun/horizon end of the same family, for hover + gradient ends.</summary>
    public static readonly Color AccentBright = new Color32(0xF0, 0xA8, 0x7C, 0xFF);
    /// <summary>Body text. Same cream as <see cref="Runner"/> — the HUD already reads runners as "you".</summary>
    public static readonly Color Text = new Color32(0xFF, 0xE9, 0xC4, 0xFF);
    public static readonly Color TextDim = new Color32(0x9A, 0x92, 0x88, 0xFF);
    /// <summary>TagRulesConfig.taggerColor.</summary>
    public static readonly Color Tagger = new Color32(0xFF, 0x3D, 0x2E, 0xFF);
    /// <summary>TagRulesConfig.runnerColor.</summary>
    public static readonly Color Runner = new Color32(0xFF, 0xE9, 0xC4, 0xFF);
    /// <summary>1px separator / panel rim. Cream at low alpha, so it lifts off charcoal without glowing.</summary>
    public static readonly Color Hairline = new(1f, 0.92f, 0.77f, 0.14f);
    public static readonly Color Shadow = new(0f, 0f, 0f, 0.45f);

    // ---------------------------------------------------------------- Type scale (design-space px @1080p)

    public const int Display = 72;
    public const int Title = 34;
    public const int Body = 18;
    public const int Caption = 13;

    // ---------------------------------------------------------------- Layout / scale

    public const float DesignHeight = 1080f;

    /// <summary>Real pixels per design-space unit. Every screen in here was pixel-hardcoded against
    /// 1080p; this is what keeps those numbers honest at other heights.</summary>
    public static float Scale => Screen.height / DesignHeight;

    /// <summary>Design-space width of the current screen. Varies with aspect (1920 at 16:9, wider at
    /// 21:9) — centre on this, never on Screen.width, which is real pixels.</summary>
    public static float DesignWidth => Screen.width / Mathf.Max(Scale, 0.0001f);

    public static float Scaled(float designUnits) => designUnits * Scale;

    /// <summary>Design-space rect -> real pixels. The helpers below apply this themselves; this is for
    /// the raw GUI.Label/GUI.DrawTexture calls screens still make around them.</summary>
    public static Rect Scaled(Rect designRect)
    {
        float s = Scale;
        return new Rect(designRect.x * s, designRect.y * s, designRect.width * s, designRect.height * s);
    }

    // ---------------------------------------------------------------- Headless guard

    // Same lazy-cache idiom as ChainSwingInteractable.Headless/GameAudio.Headless: graphicsDeviceType
    // throws if touched from a static field initializer, so it's read on first use. Under the headless
    // self-play harness (SelfPlayTests) there is no device to build a Texture2D or an OS font against —
    // every generator below returns null and every draw helper no-ops rather than allocating or throwing.
    private static bool? _headless;
    private static bool Headless => _headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    // ---------------------------------------------------------------- Generated textures

    private const int PanelTexSize = 32;   // 9-slice source: corners + a stretched 8px middle
    private const int PanelRadius = 10;
    private const int PanelBorder = 12;    // > radius so the slice never cuts into the curve; < size/2
    private const int ShadowTexHeight = 24;
    private const int GradientTexWidth = 64;
    private const int VignetteTexSize = 128;

    private static Texture2D? _panelTex;
    private static Texture2D? _buttonNormalTex;
    private static Texture2D? _buttonHoverTex;
    private static Texture2D? _buttonPressedTex;
    private static Texture2D? _shadowTex;
    private static Texture2D? _gradientTex;
    private static Texture2D? _vignetteTex;
    private static Texture2D? _thumbTex;

    /// <summary>Rounded-rect charcoal panel with a hairline rim. 9-slice — set <c>border</c> to
    /// <see cref="PanelBorder"/> scaled if you hand this to a GUIStyle yourself.</summary>
    public static Texture2D? PanelTex
    {
        get
        {
            if (_panelTex != null) return _panelTex;
            if (Headless) return null;
            return _panelTex = RoundedRect(PanelBg, Hairline);
        }
    }

    public static Texture2D? ButtonNormalTex
    {
        get
        {
            if (_buttonNormalTex != null) return _buttonNormalTex;
            if (Headless) return null;
            // A touch lighter than the panel it sits on, or buttons vanish into their own backdrop.
            return _buttonNormalTex = RoundedRect(new Color(0.16f, 0.16f, 0.19f, 0.95f), Hairline);
        }
    }

    public static Texture2D? ButtonHoverTex
    {
        get
        {
            if (_buttonHoverTex != null) return _buttonHoverTex;
            if (Headless) return null;
            // Hover reads as the accent arriving on the rim, not as a fill swap — keeps text legible.
            return _buttonHoverTex = RoundedRect(new Color(0.24f, 0.21f, 0.20f, 0.97f), AccentBright);
        }
    }

    public static Texture2D? ButtonPressedTex
    {
        get
        {
            if (_buttonPressedTex != null) return _buttonPressedTex;
            if (Headless) return null;
            return _buttonPressedTex = RoundedRect(Accent, AccentBright);
        }
    }

    /// <summary>1px hairline. ponytail: Unity's built-in white texture already IS a 1x1 — tint it with
    /// GUI.color, same as the existing HUD/kill-cam letterbox draws do. Nothing to generate.</summary>
    public static Texture2D HairlineTex => Texture2D.whiteTexture;

    /// <summary>Soft drop-shadow strip: opaque-ish at the TOP row, fading to clear downward. Draw it
    /// directly beneath a panel's bottom edge.</summary>
    public static Texture2D? ShadowTex
    {
        get
        {
            if (_shadowTex != null) return _shadowTex;
            if (Headless) return null;

            var pixels = new Color[ShadowTexHeight];
            for (int y = 0; y < ShadowTexHeight; y++)
            {
                // Texture y=0 is the bottom row and GUI.DrawTexture draws the texture upright, so the
                // top row (y = height-1) is the edge that meets the panel — that's where alpha peaks.
                float t = y / (float)(ShadowTexHeight - 1);
                float a = Shadow.a * t * t; // squared: falls off fast, so it reads as a soft contact shadow
                pixels[y] = new Color(Shadow.r, Shadow.g, Shadow.b, a);
            }

            _shadowTex = new Texture2D(1, ShadowTexHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _shadowTex.SetPixels(pixels);
            _shadowTex.Apply();
            return _shadowTex;
        }
    }

    /// <summary>Horizontal accent gradient (accent -> bright, left to right). Meter fills, slider fills,
    /// underlines. Tint with GUI.color for role-coloured variants.</summary>
    public static Texture2D? GradientTex
    {
        get
        {
            if (_gradientTex != null) return _gradientTex;
            if (Headless) return null;

            var pixels = new Color[GradientTexWidth];
            for (int x = 0; x < GradientTexWidth; x++)
                pixels[x] = Color.Lerp(Accent, AccentBright, x / (float)(GradientTexWidth - 1));

            _gradientTex = new Texture2D(GradientTexWidth, 1, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _gradientTex.SetPixels(pixels);
            _gradientTex.Apply();
            return _gradientTex;
        }
    }

    /// <summary>Radial vignette, clear at centre -> dark at the corners. Stretch over the whole screen
    /// behind an end screen so the panel has something to sit against. See <see cref="Vignette"/>.</summary>
    public static Texture2D? VignetteTex
    {
        get
        {
            if (_vignetteTex != null) return _vignetteTex;
            if (Headless) return null;

            var pixels = new Color[VignetteTexSize * VignetteTexSize];
            float half = (VignetteTexSize - 1) * 0.5f;
            for (int y = 0; y < VignetteTexSize; y++)
            {
                for (int x = 0; x < VignetteTexSize; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    // Normalised against the corner (length 1) so the darkening lands in the corners
                    // rather than clipping flat across the edge midpoints.
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Sqrt(2f);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 1f, r));
                    pixels[y * VignetteTexSize + x] = new Color(0f, 0f, 0f, a);
                }
            }

            _vignetteTex = new Texture2D(VignetteTexSize, VignetteTexSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _vignetteTex.SetPixels(pixels);
            _vignetteTex.Apply();
            return _vignetteTex;
        }
    }

    // Signed-distance rounded rect, antialiased over the last pixel of the edge, with a 1px inset rim.
    // One SetPixels for the whole texture (mirrors the generated-clip pattern in TagAgent).
    private static Texture2D RoundedRect(Color fill, Color rim)
    {
        var pixels = new Color[PanelTexSize * PanelTexSize];
        float half = PanelTexSize * 0.5f;
        float inner = half - PanelRadius;

        for (int y = 0; y < PanelTexSize; y++)
        {
            for (int x = 0; x < PanelTexSize; x++)
            {
                float px = x + 0.5f - half;
                float py = y + 0.5f - half;
                float qx = Mathf.Abs(px) - inner;
                float qy = Mathf.Abs(py) - inner;
                float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - PanelRadius; // <0 inside

                float coverage = Mathf.Clamp01(0.5f - d);          // 1px antialiased edge
                float rimAmount = Mathf.Clamp01(d + 1.5f) * coverage; // rim rides the inside of the edge
                Color c = Color.Lerp(fill, rim, rimAmount * rim.a);
                pixels[y * PanelTexSize + x] = new Color(c.r, c.g, c.b, c.a * coverage);
            }
        }

        var tex = new Texture2D(PanelTexSize, PanelTexSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    // ---------------------------------------------------------------- Font
    //
    // There is deliberately NO custom font here. A Font.CreateDynamicFontFromOSFont("Segoe UI"...) used
    // to hang off this kit and made every string in the Game view render as the word "Gizmos" — the
    // Game view toolbar's own button label. Glyphs resolved out of the Editor's live dynamic-font atlas
    // instead of ours; only text broke, every generated texture below drew fine.
    //
    // It was NOT an object-lifetime bug, so hideFlags is not the fix: Resources.UnloadUnusedAssets
    // "walks the whole game object hierarchy ... static variables are also examined" (Unity docs), and
    // every cache in this file is a static field, so nothing here is collectable. The panel/gradient
    // textures prove it — they are static-cached the same way and render correctly, so no sweep is
    // eating this class's statics.
    //
    // ponytail: the styles below derive from GUI.skin.label/button, which already carry Unity's built-in
    // font — deleting the override IS the fix. fontSize/fontStyle (incl. Bold) still work off it.
    // If a bespoke typeface is ever worth it, it needs an imported font asset, not an OS handle.

    // ---------------------------------------------------------------- Styles

    // GUIStyle construction touches GUI.skin, so it only happens inside OnGUI — cached here rather than
    // rebuilt every frame, same as RoundController.EnsureHudStyles. Keyed by the knobs that vary; the
    // per-draw textColor is left to the caller to assign (it varies by role/urgency at every call site).
    private static readonly Dictionary<(int, TextAnchor, FontStyle), GUIStyle> _labelStyles = new();
    private static GUIStyle? _panelStyle;
    private static GUIStyle? _buttonStyle;
    private static GUIStyle? _sliderStyle;
    private static GUIStyle? _thumbStyle;
    private static float _styleScale = -1f; // styles bake scaled px, so a resolution change must rebuild them

    private static void EnsureStyles()
    {
        float scale = Scale;
        if (!Mathf.Approximately(_styleScale, scale))
        {
            _labelStyles.Clear();
            _panelStyle = null;
            _buttonStyle = null;
            _sliderStyle = null;
            _thumbStyle = null;
            _styleScale = scale;
        }

        // The 9-slice border is what keeps the corner radius CONSTANT as a panel grows: GUI.Box slices
        // the source at these insets and stretches only the middle. GUI.DrawTexture has no such notion —
        // it would smear a 32px rounded corner into an ellipse across a full-width panel.
        int border = Mathf.RoundToInt(Scaled(PanelBorder));
        int pad = Mathf.RoundToInt(Scaled(12f));

        _panelStyle ??= new GUIStyle
        {
            border = new RectOffset(border, border, border, border),
            normal = { background = PanelTex },
        };

        _buttonStyle ??= new GUIStyle(GUI.skin.button)
        {
            fontSize = Mathf.RoundToInt(Scaled(Body)),
            alignment = TextAnchor.MiddleCenter,
            border = new RectOffset(border, border, border, border),
            padding = new RectOffset(pad, pad, pad / 2, pad / 2),
            normal = { background = ButtonNormalTex, textColor = Text },
            hover = { background = ButtonHoverTex, textColor = AccentBright },
            active = { background = ButtonPressedTex, textColor = PanelBg },
            focused = { background = ButtonHoverTex, textColor = AccentBright },
        };

        // Track is drawn by hand in Slider (so it can show a gradient fill); the style itself is bare.
        _sliderStyle ??= new GUIStyle { fixedHeight = 0f };
        // No 9-slice border on the thumb: it's ~14px wide, so a 12px-per-side slice would overrun it.
        // Stretching the whole rounded rect down to thumb size is both simpler and what it should look like.
        _thumbStyle ??= new GUIStyle
        {
            fixedWidth = Scaled(14f),
            fixedHeight = 0f,
            normal = { background = ThumbTex },
            active = { background = ButtonPressedTex },
        };
    }

    private static Texture2D? ThumbTex
    {
        get
        {
            if (_thumbTex != null) return _thumbTex;
            if (Headless) return null;
            return _thumbTex = RoundedRect(AccentBright, Text);
        }
    }

    /// <summary>Cached label style at a design-space size. Assign <c>.normal.textColor</c> before each
    /// draw — the instance is shared, exactly as the existing HUD styles are.</summary>
    public static GUIStyle Label(int size, TextAnchor align = TextAnchor.MiddleLeft, FontStyle fontStyle = FontStyle.Normal)
    {
        EnsureStyles();
        var key = (size, align, fontStyle);
        if (_labelStyles.TryGetValue(key, out GUIStyle cached)) return cached;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(Scaled(size)),
            alignment = align,
            fontStyle = fontStyle,
            wordWrap = false,
            normal = { textColor = Text },
        };
        _labelStyles[key] = style;
        return style;
    }

    // ---------------------------------------------------------------- Draw helpers (design-space rects)

    /// <summary>Rounded charcoal panel + its contact shadow. Rect is design space.</summary>
    public static void Panel(Rect designRect)
    {
        if (Headless) return;
        EnsureStyles();
        Rect rect = Scaled(designRect);

        // Shadow first, so the panel's antialiased rim draws over its top edge. It's a 1px-wide vertical
        // ramp, so stretching it across the panel width is exactly what it's for.
        float shadowHeight = Scaled(ShadowTexHeight * 0.75f);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax, rect.width, shadowHeight), ShadowTex, ScaleMode.StretchToFill, true);

        GUI.Box(rect, GUIContent.none, _panelStyle);
    }

    /// <summary>Button with hover/pressed states. Rect is design space. Returns true on click.</summary>
    public static bool Button(Rect designRect, string label)
    {
        if (Headless) return false;
        EnsureStyles();

        // GUI.Button already resolves normal/hover/active off the style itself — no manual mouse
        // hit-testing needed, and it gets keyboard/controller focus right for free.
        bool clicked = GUI.Button(Scaled(designRect), label, _buttonStyle);
        if (clicked) PlayClick();
        return clicked;
    }

    // TODO(audio): UI click SFX — the audio plan lands later. Deliberately a no-op seam so every button
    // already routes through one place; wiring it up is a one-liner here and nowhere else.
    private static void PlayClick()
    {
    }

    /// <summary>Horizontal slider with a gradient fill. Rect is design space.</summary>
    public static float Slider(Rect designRect, float value, float min, float max)
    {
        if (Headless) return value;
        EnsureStyles();

        Rect rect = Scaled(designRect);
        GUI.Box(rect, GUIContent.none, _panelStyle); // 9-sliced track, same rounded rim as a panel

        // Gradient is 64x1 — stretching it over the filled span is the intended use, and it has no
        // corners to distort, so a plain DrawTexture is correct here (unlike the track above).
        float t = Mathf.InverseLerp(min, max, value);
        if (t > 0f)
        {
            float inset = Scaled(2f);
            GUI.DrawTexture(new Rect(rect.x + inset, rect.y + inset, (rect.width - inset * 2f) * t, rect.height - inset * 2f),
                GradientTex, ScaleMode.StretchToFill, true);
        }

        return GUI.HorizontalSlider(rect, value, min, max, _sliderStyle, _thumbStyle);
    }

    /// <summary>Full-screen radial vignette, for end screens. <paramref name="alpha"/> 0..1.</summary>
    public static void Vignette(float alpha)
    {
        if (Headless) return;
        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), VignetteTex, ScaleMode.StretchToFill, true);
        GUI.color = prev;
    }
}

/// <summary>
/// Cubic easing on UNSCALED time. Every menu in this project freezes the game with
/// <c>Time.timeScale = 0</c> (main menu, pause, kill cam), so anything easing off Time.time would sit
/// dead still exactly when it's on screen — these read Time.unscaledTime instead.
/// </summary>
public static class UIEase
{
    public static float In(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t;
    }

    public static float Out(float t)
    {
        t = Mathf.Clamp01(t) - 1f;
        return t * t * t + 1f;
    }

    public static float InOut(float t)
    {
        t = Mathf.Clamp01(t);
        return t < 0.5f
            ? 4f * t * t * t
            : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
    }

    /// <summary>Eased 0..1 progress of a <paramref name="duration"/>-long animation that began at
    /// <paramref name="unscaledStartTime"/> (a captured <see cref="Time.unscaledTime"/>). The usual
    /// call: <c>UIEase.Since(_openedAt, 0.25f)</c> for a panel's slide/fade-in.</summary>
    public static float Since(float unscaledStartTime, float duration) =>
        Out(duration <= 0f ? 1f : (Time.unscaledTime - unscaledStartTime) / duration);
}
