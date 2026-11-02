using System;
using Il2CppScheduleOne;
using Il2CppScheduleOne.State;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Snitch.UI
{
    /// <summary>
    /// Frees the mouse cursor while the expanded overlay is open. The game's own UI state is pushed (mouse free,
    /// crosshair hidden, equipping, inventory, movement and look off, HUD and compass kept), so the camera does not
    /// turn and a click does not reach the held item; popping it puts everything back as it was. Escape closes the
    /// expanded overlay through the game's exit-listener list, so the same press does not open the pause menu.
    /// </summary>
    internal static class OverlayInput
    {
        private static bool _freed;
        private static bool _wasLocked;
        private static GameObject _stateObject;
        private static MonoState _state;
        private static int _exitScene = -1;

        internal static bool Freed => _freed;

        /// <summary>Cursor unlocked and visible, camera look and movement off. No-op when already freed.</summary>
        internal static void Free()
        {
            if (_freed) return;
            if (!PlayerSingleton<PlayerCamera>.InstanceExists || SceneState.Current == null)
            {
                Core.Log?.Warning("[overlay] no player camera or scene state yet, the cursor stays as it is.");
                return;
            }
            try
            {
                _wasLocked = Cursor.lockState == CursorLockMode.Locked;
                _stateObject = new GameObject("Snitch_OverlayState");
                _state = _stateObject.AddComponent<MonoState>();
                _state.Properties = new StateProperties(
                    StateProperties.EMouseState.Free, StateProperties.ECrosshairState.Hidden,
                    StateProperties.EEquippingState.Disabled, StateProperties.EInventoryState.Disabled,
                    StateProperties.EHUDState.Visible, StateProperties.ECompassState.Visible,
                    StateProperties.EMovementState.Locked, StateProperties.ELookState.Locked,
                    StateProperties.EBlurState.Disabled);
                _state.InitializeDefaultParent(SceneState.Current.InGame);
                _state.PushToDefaultParent();
                // The state frees the mouse for a mouse player only; the overlay is a mouse surface, so force it.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _freed = true;
                Core.Log?.Msg("[overlay] UI state pushed: mouse free, crosshair hidden, equipping disabled, inventory disabled, "
                            + "movement locked, look locked, blur disabled, HUD visible, compass visible.");
                RegisterExit();
                Core.Log?.Msg("[overlay] cursor freed.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] could not free the cursor: " + e.Message);
                DropState();
            }
        }

        /// <summary>Gives the camera and cursor back as they were. No-op when not freed.</summary>
        internal static void Restore(string why)
        {
            if (!_freed) return;
            _freed = false;
            try
            {
                DropState();
                if (_wasLocked && PlayerSingleton<PlayerCamera>.InstanceExists)
                    PlayerSingleton<PlayerCamera>.Instance.LockMouse(true);
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

        private static void DropState()
        {
            try
            {
                if (_state != null)
                {
                    _state.PopFromDefaultParent();
                    Core.Log?.Msg("[overlay] UI state popped.");
                }
            }
            catch (Exception e) { Core.Log?.Error("[overlay] could not pop the UI state: " + e.Message); }
            if (_stateObject != null) UnityEngine.Object.Destroy(_stateObject);
            _state = null;
            _stateObject = null;
        }

        /// <summary>The game clears its exit listeners on every scene change, so this registers again per scene.</summary>
        private static void RegisterExit()
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (_exitScene == scene) return;
            try
            {
                GameInput.RegisterExitListener((Il2CppSystem.Action)OnExit,
                    (Il2CppSystem.Func<bool>)(Func<bool>)(() => _freed && Overlay.Mode == OverlayMode.Expanded), 500, false);
                _exitScene = scene;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[overlay] could not register the Escape listener - Escape will not close the overlay: " + e.Message);
            }
        }

        private static void OnExit()
        {
            Core.Log?.Msg("[overlay] Escape pressed.");
            Overlay.SetMode(OverlayMode.Hidden, "escape");
        }

#if DEBUG
        /// <summary>One line with the flags the pushed state is meant to change, read back from the game.</summary>
        internal static string Describe()
        {
            string look = PlayerSingleton<PlayerCamera>.InstanceExists ? PlayerSingleton<PlayerCamera>.Instance.CanLook.ToString() : "n/a";
            string move = PlayerSingleton<PlayerMovement>.InstanceExists ? PlayerSingleton<PlayerMovement>.Instance.CanMove.ToString() : "n/a";
            return "inputstate freed=" + _freed + " canLook=" + look + " canMove=" + move
                 + " cursorLock=" + Cursor.lockState + " cursorVisible=" + Cursor.visible;
        }

        /// <summary>Presses the game's primary exit action, the path the Escape key takes.</summary>
        internal static string PressEscape()
        {
            try
            {
                if (GameInput.Instance == null) return "escape: no GameInput";
                GameInput.Instance.Exit(ExitType.Primary);
                return "escape: sent";
            }
            catch (Exception e) { Core.Log?.Error("[overlay] escape failed: " + e.Message); return "escape: failed"; }
        }
#endif

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
