#nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Movement;

public enum AudioCategory { Sfx, Ui, Ambience, Music }

/// <summary>
/// Single runtime entry point for authored SFX/music: per-category volume, positional/2D one-shot
/// playback, a looping-source helper, and a footstep clip-variant picker. Authored clips are NOT in
/// the repo yet — Resources.Load returning null on a missing name IS the load-if-present mechanism,
/// so every Play call here is a safe, silent no-op until real clips land under these exact names.
/// </summary>
public static class GameAudio
{
    public const string Footstep = "footstep";
    public const string FootstepSprint = "footstep_sprint";
    public const string SlideScrape = "slide_scrape";
    public const string JumpGrunt = "jump_grunt";
    public const string WhooshLunge = "whoosh_lunge";
    public const string WhooshSwing = "whoosh_swing";
    public const string ScuffMantle = "scuff_mantle";
    public const string CanEaten = "can_eaten";
    public const string RummageLoop = "rummage_loop";
    public const string Fizzle = "fizzle";
    public const string UiClick = "ui_click";
    public const string MusicMenu = "music_menu";
    public const string MusicRound = "music_round";

    // Same lazy-cache idiom as ChainSwingInteractable.Headless: SystemInfo.graphicsDeviceType throws
    // if touched from a field initializer / constructor context, so it's read lazily on first use.
    private static bool? _headless;
    private static bool Headless => _headless ??= SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

    // Caches the NULL result too — a missing clip must not re-hit Resources.Load every call; footsteps
    // alone fire several times a second per agent, times a dozen agents in the tag arena.
    private static readonly Dictionary<string, AudioClip?> _clipCache = new();
    // Number of contiguous _01.._04 variants found for a given base name — probed once, then cached.
    private static readonly Dictionary<string, int> _variantCounts = new();

    private static readonly string[] _volumeKeys = { "Vol_Sfx", "Vol_Ui", "Vol_Ambience", "Vol_Music" };
    private static readonly float[] _volumeCache = { -1f, -1f, -1f, -1f };

    public static float CategoryVolume(AudioCategory category)
    {
        int i = (int)category;
        if (_volumeCache[i] < 0f) _volumeCache[i] = PlayerPrefs.GetFloat(_volumeKeys[i], 1f);
        return _volumeCache[i];
    }

    public static void SetCategoryVolume(AudioCategory category, float volume)
    {
        volume = Mathf.Clamp01(volume);
        int i = (int)category;
        _volumeCache[i] = volume;
        PlayerPrefs.SetFloat(_volumeKeys[i], volume);
        PlayerPrefs.Save();
    }

    private static AudioClip? LoadClip(string name)
    {
        if (_clipCache.TryGetValue(name, out AudioClip? cached)) return cached;
        AudioClip? clip = Resources.Load<AudioClip>(name);
        _clipCache[name] = clip;
        return clip;
    }

    /// <summary>Positional 3D one-shot. No-op when headless or when the named clip isn't in a
    /// Resources folder.</summary>
    public static void Play(string clip, Vector3 position, AudioCategory category = AudioCategory.Sfx, float volume = 1f, float pitch = 1f)
    {
        if (Headless) return;
        AudioClip? resolved = LoadClip(clip);
        if (resolved == null) return;
        PlayInternal(resolved, position, spatialBlend: 1f, category, volume, pitch);
    }

    /// <summary>Non-positional one-shot (UI clicks, stingers). No-op when headless or when the named
    /// clip isn't in a Resources folder.</summary>
    public static void Play2D(string clip, AudioCategory category = AudioCategory.Ui, float volume = 1f)
    {
        if (Headless) return;
        AudioClip? resolved = LoadClip(clip);
        if (resolved == null) return;
        PlayInternal(resolved, Vector3.zero, spatialBlend: 0f, category, volume, pitch: 1f);
    }

    /// <summary>Non-positional one-shot for a clip the caller already holds. The synthesized SFX
    /// (RoundController's countdown blips) are generated at runtime, so they have no Resources entry
    /// to look up by name — but they still want the 2D placement, category volume and headless guard
    /// the name-based <see cref="Play2D(string, AudioCategory, float)"/> provides.</summary>
    public static void Play2D(AudioClip clip, AudioCategory category = AudioCategory.Ui, float volume = 1f)
    {
        if (Headless) return;
        PlayInternal(clip, Vector3.zero, spatialBlend: 0f, category, volume, pitch: 1f);
    }

    /// <summary>Footstep-only clip picker: tries baseName_01.._04 and plays a random one among
    /// whichever exist, falling back to the bare baseName, and then to fallbackBaseName if given (the
    /// sprint→walk footstep fallback). spatial=false plays 2D at full volume regardless of position —
    /// ponytail: the local player wants the same pitch/variant treatment as bots but Play2D's fixed
    /// signature has no pitch param, so this one path covers both instead of a second local-only copy
    /// of the variant/fallback logic.</summary>
    public static void PlayVariant(string baseName, Vector3 position, AudioCategory category = AudioCategory.Sfx,
        float volume = 1f, float pitch = 1f, bool spatial = true, string? fallbackBaseName = null)
    {
        if (Headless) return;
        AudioClip? resolved = ResolveVariant(baseName);
        if (resolved == null && fallbackBaseName != null) resolved = ResolveVariant(fallbackBaseName);
        if (resolved == null) return;
        PlayInternal(resolved, position, spatialBlend: spatial ? 1f : 0f, category, volume, pitch);
    }

    private static AudioClip? ResolveVariant(string baseName)
    {
        if (!_variantCounts.TryGetValue(baseName, out int count))
        {
            count = 0;
            for (int i = 1; i <= 4; i++)
            {
                if (LoadClip($"{baseName}_{i:00}") == null) break; // contiguous only — first gap stops the probe
                count++;
            }
            _variantCounts[baseName] = count;
        }

        return count == 0 ? LoadClip(baseName) : LoadClip($"{baseName}_{Random.Range(1, count + 1):00}");
    }

    // AudioSource.PlayClipAtPoint (the idiom TagAgent's synth SFX already use) gives no handle to set
    // pitch — it spawns and destroys its own hidden source. Footsteps need ±10% pitch randomization,
    // so this builds the temporary GameObject + AudioSource by hand instead.
    private static void PlayInternal(AudioClip clip, Vector3 position, float spatialBlend, AudioCategory category, float volume, float pitch)
    {
        var go = new GameObject($"OneShot_{clip.name}");
        go.transform.position = position;
        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.volume = volume * CategoryVolume(category);
        source.pitch = pitch;
        source.spatialBlend = spatialBlend;
        source.Play();
        // Divide by pitch: a pitched-DOWN clip (pitch < 1) plays LONGER than clip.length and would
        // otherwise get cut off mid-tail by a destroy timed off the unpitched length.
        Object.Destroy(go, clip.length / Mathf.Max(pitch, 0.01f));
    }

    /// <summary>Looping source parented to <paramref name="parent"/> (the slide scrape) — caller owns
    /// the lifetime (stop/Destroy it themselves). Returns null when headless or the clip is missing.</summary>
    public static AudioSource? Loop(string clip, Transform parent, AudioCategory category = AudioCategory.Sfx, float volume = 1f)
    {
        if (Headless) return null;
        AudioClip? resolved = LoadClip(clip);
        if (resolved == null) return null;

        var go = new GameObject($"Loop_{clip}");
        go.transform.SetParent(parent, false);
        var source = go.AddComponent<AudioSource>();
        source.clip = resolved;
        source.loop = true;
        source.volume = volume * CategoryVolume(category);
        source.spatialBlend = 1f;
        source.Play();
        return source;
    }
}
