using System;
using Il2CppScheduleOne.Networking;   // Lobby (PersistentSingleton<Lobby>)

namespace Snitch.Compat
{
    /// <summary>
    /// Multiplayer detection + host authority. Snitch is read-only by default, but some advanced features
    /// (the ablation A/B harness and the built-in NPC/trash "off" levers) mutate simulation state and must
    /// only run on the authoritative peer. Profiling/measurement itself is local and safe on every peer.
    ///
    /// Uses the game's own Lobby singleton (Il2CppScheduleOne.Networking.Lobby : PersistentSingleton&lt;Lobby&gt;).
    /// In Schedule I single-player the host still runs a local FishNet server, so "IsServer || IsClient" is TRUE
    /// even solo - the Lobby is the reliable signal: co-op is IsInLobby with PlayerCount &gt; 1, host is Lobby.IsHost.
    /// Conservative on any failure: IsMultiplayer -&gt; false, IsAuthoritative -&gt; true (behave as single-player host).
    /// Same known gap as the family template: direct-UDP dedicated transport keeps LobbyID == 0.
    /// </summary>
    internal static class Net
    {
        internal static bool IsMultiplayer()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null)
                {
                    return false;   // no lobby manager yet -> treat as single-player
                }
                return lobby.IsInLobby && lobby.PlayerCount > 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when this peer owns simulation: single-player, or the host of a co-op lobby. Any
        /// state-mutating lever (ablation / NPC-off) is only allowed when this is true.</summary>
        internal static bool IsAuthoritative()
        {
            try
            {
                Lobby lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null || !lobby.IsInLobby)
                {
                    return true;    // single-player: the local peer is authoritative by definition
                }
                return lobby.IsHost;   // multiplayer: only the host simulates
            }
            catch
            {
                return true;
            }
        }
    }
}
