using System;
using UnityEngine;

namespace Snitch.UI
{
    /// <summary>
    /// Frees the mouse cursor and stops the camera look while the expanded overlay is open, through the game's own
    /// PlayerCamera (look flag, active-UI-element list, FreeMouse/LockMouse), and puts the previous state back.
    /// </summary>
    internal static class OverlayInput
    {
        private const string Element = "Snitch overlay";

        private static bool _freed;
        private static bool _wasLocked;
        private static bool _prevCanLook;

        internal static bool Freed => _freed;

        /// <summary>Cursor unlocked and visible, camera look off. No-op when already freed.</summary>
        internal static void Free()
        {
            if (_freed) return;
            if (!PlayerSingleton<PlayerCamera>.InstanceExists)
            {
                Core.Log?.Warning("[overlay] no player camera yet, the cursor stays as it is.");
                return;
            }
            try
            {
                PlayerCamera cam = PlayerSingleton<PlayerCamera>.Instance;
                _wasLocked = Cursor.lockState == CursorLockMode.Locked;
                _prevCanLook = cam.CanLook;
                cam.AddActiveUIElement(Element);
                cam.SetCanLook(false);
                cam.FreeMouse(false);
                // FreeMouse skips the unlock for a gamepad; the overlay is a mouse surface, so force it.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _freed = true;
                Core.Log?.Msg("[overlay] cursor freed.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] could not free the cursor: " + e.Message);
            }
        }

        /// <summary>Gives the camera and cursor back as they were. No-op when not freed.</summary>
        internal static void Restore(string why)
        {
            if (!_freed) return;
            _freed = false;
            try
            {
                if (PlayerSingleton<PlayerCamera>.InstanceExists)
                {
                    PlayerCamera cam = PlayerSingleton<PlayerCamera>.Instance;
                    cam.RemoveActiveUIElement(Element);
                    cam.SetCanLook(_prevCanLook);
                    if (_wasLocked) cam.LockMouse(true);
                }
                else if (_wasLocked)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                Core.Log?.Msg("[overlay] cursor restored (" + why + ").");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] could not restore the cursor: " + e.Message);
            }
        }

        /// <summary>True while the game has taken the cursor back (it locked it again) or the camera is gone, so the
        /// overlay must not keep fighting the game's own menus.</summary>
        internal static bool LostToGame()
        {
            if (!_freed) return false;
            if (!PlayerSingleton<PlayerCamera>.InstanceExists) return true;
            return Cursor.lockState == CursorLockMode.Locked;
        }
    }
}
