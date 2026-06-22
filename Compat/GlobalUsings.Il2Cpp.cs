// IL2CPP backend (net6.0) global usings.
//
// Import the Il2Cpp* game namespaces used across the mod so the rest of the source uses UNQUALIFIED
// game type names (NPC, NPCManager, NPCMovement, Player, PersistentSingleton, NetworkSingleton).
//
// NOTE: because UnityEngine is imported here and System is imported implicitly, the bare identifiers
// `Object` and `Random` are ambiguous - always write `UnityEngine.Object` / `UnityEngine.Random`
// (or `System.Random`) explicitly.

global using UnityEngine;
global using Il2CppScheduleOne.NPCs;            // NPC, NPCManager, NPCMovement, NPCScheduleManager
global using Il2CppScheduleOne.DevUtilities;     // PersistentSingleton<T>, NetworkSingleton<T>, Singleton<T>
global using Il2CppScheduleOne.PlayerScripts;    // Player (Player.Local, Player.PlayerList)
