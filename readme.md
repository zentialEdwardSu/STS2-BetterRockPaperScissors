# STS2-BetterRockPaperScissors[Rps]

A Slay the Spire 2 mod that replaces the random rock-paper-scissors used when multiple players
contest the same treasure relic with **player-chosen gestures on a countdown, synced across
multiplayer**. It loads as a DLL via STS2-RitsuLib and patches the
game with Harmony.

## Vanilla compatibility

This mod does not ship replacement game files. It changes behavior entirely at runtime by
(1) three Harmony patches on vanilla methods, (2) reflection-driven calls into private members of the
relic-fight nodes, and (3) consuming a number of stock APIs and types as-is. The sections below list
exactly what is touched, why, and where other mods are likely to collide.

### Vanilla code this mod patches

1. `NTreasureRoomRelicCollection.OnRelicsAwarded(List<RelicPickingResult>)`

   1. kind: **Prefix returning `false`** (suppresses the original)
   2. Source: `Patches/RelicFightInteractivePatch.cs` 
   3. Purpose: Intercepts relic awarding. Runs the interactive RPS resolver over every `FoughtOver` result, rewrites each fight's moves/winner, recomputes consolation/skip relics, then re-invokes the private `AnimateRelicAwards` so the rest of the stock award flow still runs. Falls back to letting the original run untouched on any error. 

2. `NHandImageCollection.UpdateHandVisibility()` 

   1. kind: **Postfix** 

   2. Source: `Patches/RelicRoomCursorPatch.cs` 

   3.  Purpose: Re-asserts the OS mouse cursor

      (`NGame.Instance.CursorManager.SetCursorShown(true)`). Stock hides the cursor for the whole `SharedRelicPicking` screen and re-runs on every input change, which would hide the cursor needed to click a gesture. Still respects controller mode, so controller players are unaffected. |

3. `NHandImage.AnimateIn()`

   1. kind: **Postfix**

   2. Source: `Patches/HandRestoreOnResumePatch.cs`

   3. Purpose: Works around a **stable-branch** bug where a fighter hand that retracted off-screen while a player had the pause/ESC menu open never returns. When the menu closes the hand animates back in, but stock `AnimateIn` on stable never restores a frozen hand's target position, so it slides toward the stale off-screen point. In real multiplayer this is driven by each peer's own screen state, so a *remote* player's paused hand stays hidden on **every** peer's screen until the relic grab. The postfix reseats a frozen hand's `_desiredPosition` to its on-screen resting spot (reproducing the fix the beta branch already applies in-engine). Gated by the **Fix pause-hidden hands** setting (default on) — turn it **off** on the beta branch to avoid double-applying the engine's own restore.



### Vanilla private members invoked by reflection

`Fight/FightAnimator.cs` drives the stock fight animation one round at a time. It does **not** patch
these — it reflects them out and calls them — but it is tightly coupled to their existence and
signatures:

- `NTreasureRoomRelicCollection` private fields: `_hands`, `_fightBackstop`, `_holdersInUse`
- `NTreasureRoomRelicCollection.AnimateRelicAwards(List<RelicPickingResult>)` (private; re-invoked after the prefix)
- `NHandImageCollection.GetHand`, `BeforeFightStarted`, `BeforeRelicsAwarded`
- `NHandImage.DoFightMove`, `DoLoseShake`, `GrabRelic`, `SetIsInFight`

If any handle is missing (e.g. a future game update renames them), `FightAnimator.Available` becomes
`false` and the mod degrades gracefully: winners are still resolved correctly and the stock
end-of-fight replay acts as the visual fallback.

`Patches/HandRestoreOnResumePatch.cs` additionally reflects `NHandImage`'s private `_desiredPosition`
and `_state` fields for the pause-hidden-hand fix above. If either is missing the postfix simply
no-ops, leaving hand behavior untouched.

### Vanilla APIs and types consumed unchanged

These are read or called but not modified. They are listed because a game update or another mod that alters them changes this mod's behavior:

- **Relic picking model** — `RelicPickingResult`, `RelicPickingResultType`, `RelicPickingFight`, `RelicPickingFightRound`, `RelicPickingFightMove`. The mod mirrors the stock elimination rule (a round resolves only with exactly two distinct gestures; `(a+1)%3 == b` loses).
- **Relic granting** — `RelicCmd.Obtain`, `RelicInventory.AnimateRelic`, `Player.RelicGrabBag.MoveToFallback`. Grants happen per-peer; the mod keeps them in sync by resolving identical winners everywhere.
- **Run / player state** — `RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics` (the network key for each relic), `Player.RunState`, `IRunState.GetPlayerSlotIndex` / `Players`, `LocalContext.IsMe`.
- **Networking** — `RunManager.Instance.NetService` (`INetGameService`): `RegisterMessageHandler` / `UnregisterMessageHandler` / `SendMessage` / `GetStatsForPeer`, `NetGameType`, `ConnectionStats.PingMsec`.
- **Custom net messages** — `RpsMoveChosenMessage`, `RpsRoundResolvedMessage`, `RpsRoundBeganMessage` implement stock `INetMessage` / `IPacketSerializable`.
- **UI / assets** — stock GodotSharp nodes only for the picker overlay, `CharacterModel.ArmRock/Paper/ScissorsTexture` for the gesture art, `ImageHelper.GetImagePath`, `SfxCmd.Play("event:/sfx/ui/clicks/ui_click")`, `NRun.Instance.GlobalUi.RelicInventory`, `NGame.Instance.CursorManager`.
- **RNG** — `Rng.Chaotic`, used only to fill a move for a fighter who times out or a bot.
