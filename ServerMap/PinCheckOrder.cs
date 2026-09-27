using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ServerMap
{
    [HarmonyPatch(typeof(Minimap), "UpdatePins")]
    internal static class PinCheckOrder
    {
        private static System.Reflection.FieldInfo _pins;

        static void Postfix(Minimap __instance)
        {
            if (__instance == null) return;
            if (_pins == null) _pins = AccessTools.Field(typeof(Minimap), "m_pins");
            List<Minimap.PinData> pins = _pins == null ? null : _pins.GetValue(__instance) as List<Minimap.PinData>;
            if (pins == null) return;
            for (int i = 0; i < pins.Count; i++) Lift(pins[i]);
        }

        private static void Lift(Minimap.PinData pin)
        {
            if (pin == null || pin.m_uiElement == null || pin.m_checkedElement == null) return;
            RectTransform mark = pin.m_checkedElement.transform as RectTransform;
            RectTransform icon = pin.m_uiElement;
            if (mark == null) return;
            if (!pin.m_checked || icon.parent == null)
            {
                if (mark.parent != icon) mark.SetParent(icon, false);
                return;
            }
            if (mark.parent != icon.parent) mark.SetParent(icon.parent, false);
            int iconIndex = icon.GetSiblingIndex();
            int target = mark.GetSiblingIndex() < iconIndex ? iconIndex : iconIndex + 1;
            if (mark.GetSiblingIndex() != target) mark.SetSiblingIndex(target);
            mark.anchorMin = icon.anchorMin;
            mark.anchorMax = icon.anchorMax;
            mark.pivot = icon.pivot;
            mark.anchoredPosition = icon.anchoredPosition;
            mark.sizeDelta = icon.sizeDelta;
            mark.localRotation = icon.localRotation;
            mark.localScale = Vector3.one;
        }
    }

    [HarmonyPatch(typeof(Minimap), "DestroyPinMarker")]
    internal static class PinCheckDestroy
    {
        static void Prefix(Minimap.PinData pin)
        {
            if (pin == null || pin.m_uiElement == null || pin.m_checkedElement == null) return;
            Transform mark = pin.m_checkedElement.transform;
            if (mark.parent != pin.m_uiElement) mark.SetParent(pin.m_uiElement, false);
        }
    }
}
