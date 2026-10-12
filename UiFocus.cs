using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GTLive
{
    /// <summary>
    /// The game's menus are Unity UI scroll views with keyboard navigation left on. Clicking or dragging a scrollbar
    /// or slider gives it the keyboard focus, and Unity then moves the focused one with the "Vertical"/"Horizontal"
    /// inputs: W/A/S/D, the arrow keys and any gamepad stick, the same keys that pan the map. Panning south pushes
    /// the list to the bottom and holds it there until a click lands somewhere else (a middle click did it).
    /// Once the mouse button is up, take the focus back so only the mouse moves them.
    /// </summary>
    public static class UiFocus
    {
        public static bool Enabled = true;
        public static int Released;

        public static void Tick()
        {
            if (!Enabled) return;
            var es = EventSystem.current;
            if (es == null) return;
            var go = es.currentSelectedGameObject;
            if (go == null) return;
            // still dragging it
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)) return;
            if (go.GetComponent<Scrollbar>() == null && go.GetComponent<Slider>() == null) return;
            es.SetSelectedGameObject(null);
            Released++;
        }
    }
}
