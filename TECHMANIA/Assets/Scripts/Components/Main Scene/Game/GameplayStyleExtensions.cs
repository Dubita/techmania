using UnityEngine;
using UnityEngine.UIElements;

// Performance helpers for the gameplay loop.
//
// Writing to a VisualElement's inline style (e.g. style.backgroundImage)
// marks the element dirty, forcing UI Toolkit to re-resolve styles and
// repaint it -- even when the assigned value is identical to the current
// one. The gameplay loop re-assigns note/trail/scanline/VFX sprites every
// frame, so for non-animated skins (and on frames where an animation has
// not advanced) these are pure waste.
//
// These helpers compare against the value already set and only write when
// it actually changes. Behaviour is identical; only redundant writes are
// removed. See PERFORMANCE-AND-FEATURES-BACKLOG.md (P1).
public static class GameplayStyleExtensions
{
    // Assigns backgroundImage only when the sprite reference changes.
    // When unset, the current sprite reads as null, so a real sprite is
    // always applied the first time.
    public static void SetBackgroundSpriteIfChanged(
        this VisualElement element, Sprite sprite)
    {
        if (element.style.backgroundImage.value.sprite == sprite) return;
        element.style.backgroundImage = new StyleBackground(sprite);
    }
}
