# STS2-BetterRockPaperScissors[Rps]

A Slay the Spire 2 mod that replaces the game's random rock-paper-scissors — and the multiplayer
"split vote" roulette — with **player-chosen gestures on a countdown, synced across multiplayer**.
It loads as a DLL via STS2-RitsuLib and patches the game with Harmony.

Three decisions are replaced:

| Vanilla decision | Replaced with | Toggle |
| --- | --- | --- |
| Multiple players contest the same treasure relic (random RPS) | manual gestures, one elimination round at a time | always on |
| Players vote for different map coordinates (roulette picks a random voter) | manual RPS; the **winning side's coordinate** is where the party travels | `map_conflict_rps` (host, default off) |
| Players vote for different options in a shared event (roulette) | manual RPS; the **winning side's option** is executed | `event_conflict_rps` (host, default off) |

The settings page is localized in English and Simplified Chinese and follows the game's language.

## Vanilla compatibility

This mod does not ship replacement game files. It changes behavior entirely at runtime by
(1) eight Harmony patches on vanilla methods, (2) reflection-driven calls into private members of the
relic-fight nodes and of the two vote synchronizers, and (3) consuming a number of stock APIs and
types as-is. The sections below list exactly what is touched, why, and where other mods are likely to
collide.

Every new patch is guarded by `[HarmonyPrepare]`: when a target member cannot be resolved (e.g. a game
update renamed it) that patch is simply not installed, and the feature it powers falls back to stock
behavior instead of throwing.

### Vanilla code this mod patches

#### Treasure relic fights

1. `NTreasureRoomRelicCollection.OnRelicsAwarded`

   1. kind: **Prefix returning `false`** (suppresses the original)
   2. Source: `Patches/RelicFightInteractivePatch.cs` 
   3. Runs interactive RPS for contested relics and recomputes consolation prizes. The matching `AnimateRelicAwards` overload handles the remaining awards and completes the room. If a fight animation fails, the game replays and awards that fight using the synced moves.

2. `NHandImageCollection.UpdateHandVisibility()` 

   1. kind: **Postfix** 

   2. Source: `Patches/RelicRoomCursorPatch.cs` 

   3.  Purpose: Re-asserts the OS mouse cursor

      (`NGame.Instance.CursorManager.SetCursorShown(true)`). Stock hides the cursor for the whole `SharedRelicPicking` screen and re-runs on every input change, which would hide the cursor needed to click a gesture. Still respects controller mode, so controller players are unaffected. |

#### Map path conflicts and shared-event option conflicts

3. `MapSelectionSynchronizer.MoveToMapCoord()` (private)

   1. kind: **Prefix returning `false`** when the mod takes the decision over
   2. Source: `Patches/MapConflictDecisionPatch.cs`
   3. Purpose: The stock host-side decision point for a split map vote. When both sides are a real
      multiplayer host, the matching setting is on, and the votes are genuinely split, the prefix
      snapshots the votes, starts an interactive fight and suppresses the random pick. A repeated
      call while a fight is in flight (the game makes one when a vote changes mid-fight) is swallowed.
      When the fight cannot run, the original method executes untouched.

4. `EventSynchronizer.ChooseSharedEventOption()` (private)

   1. kind: **Prefix returning `false`** when the mod takes the decision over
   2. Source: `Patches/EventConflictDecisionPatch.cs`
   3. Purpose: Same takeover for a split vote on a shared event; non-shared events never reach this method.

5. `MapSplitVoteAnimation.TryPlay(MapCoord)`

   1. kind: **Prefix returning `false`** with `__result` replaced
   2. Source: `Patches/MapSplitVoteAnimationSkipPatch.cs`
   3. Purpose: Suppresses the stock roulette for a conflict the mod decided (it highlights a *random*
      voter, so replaying it after a real fight would point at the wrong player) and hands the caller
      the 1s winner reveal to await instead. A travel that the stock code decided still plays the
      vanilla animation.

6. `EventSplitVoteAnimation.TryPlay(NEventOptionButton)`

   1. kind: **Prefix returning `false`** with `__result` replaced
   2. Source: `Patches/EventSplitVoteAnimationSkipPatch.cs`
   3. Purpose: Same suppression for shared events; the vanilla post-choice presentation (chosen option
      flashes, others grey out) is deliberately kept.

7. `MapSelectionSynchronizer` constructor

   1. kind: **Postfix**
   2. Source: `Patches/RunStartNetRegistrationPatch.cs`
   3. Purpose: Registers the mod's conflict message handlers once per run on every peer (host and
      client alike). Registration has to happen before the first conflict — which can be in the first
      event room — and the map-selection synchronizer is built with the run state, before any room.

### Vanilla private members invoked by reflection

The relic fight uses these reflected members to run animations one round at a time and hand off the awards:

- `NTreasureRoomRelicCollection` private fields: `_hands`, `_fightBackstop`, `_holdersInUse`
- `NHandImageCollection._hands`
- `NTreasureRoomRelicCollection.AnimateRelicAwards`, with the matching stable or beta signature
- `RelicPickingResult.relicIndex` on beta

If the animation fields are unavailable, players still choose moves and the game plays the full fight at the end.

The conflict replacements resolve their targets once, in `Conflict/ConflictRpsHandles.cs`:

- `MapSelectionSynchronizer._acceptingVotesFromSource` (read + write; the struct's `coord` is rewritten
  to reject votes for the old map location, and the travel action is queued by the mod)
- `MapSelectionSynchronizer.MoveToMapCoord` (invoked with a re-entrancy guard when the mod falls back to
  the stock random decision)
- `EventSynchronizer._netService`, `_messageBuffer`, `_pageIndex` (read to reproduce the stock
  chosen-option broadcast)
- `EventSynchronizer.ChooseOptionForSharedEvent(uint)` (invoked after the broadcast) and
  `ChooseSharedEventOption` (invoked for the fallback)

Missing handles degrade **per feature**: `MapResolved` / `EventResolved` are checked separately, so a
renamed event member does not disable the map replacement.

### Vanilla APIs and types consumed unchanged

These are read or called but not modified. They are listed because a game update or another mod that alters them changes this mod's behavior:

- **Relic picking model** — `RelicPickingResult`, `RelicPickingResultType`, `RelicPickingFight`, `RelicPickingFightRound`, `RelicPickingFightMove`. The mod mirrors the stock elimination rule (a round resolves only with exactly two distinct gestures; `(a+1)%3 == b` loses).
- **Relic granting** — `RelicCmd.Obtain`, `RelicInventory.AnimateRelic`, `Player.RelicGrabBag.MoveToFallback`. Grants happen per-peer; the mod keeps them in sync by resolving identical winners everywhere.
- **Run / player state** — `RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics` (the original relic slots on stable), `Player.RunState`, `IRunState.GetPlayerSlotIndex` / `Players`, `LocalContext.IsMe`.
- **Map + event vote synchronizers** — `MapSelectionSynchronizer.GetVote(Player)` / `MapVote.coord`,
  `EventSynchronizer.GetPlayerVote(Player)` / `IsShared`, `MapCoord`, `MapLocation.coord`. These are
  only read: the votes themselves are snapshotted, never rewritten.
- **Decision plumbing** — `MoveToMapCoordAction` + `ActionQueueSynchronizer.RequestEnqueue`,
  `SharedEventOptionChosenMessage` + `RunLocationTargetedMessageBuffer.CurrentLocation`. The mod
  reproduces the stock tail of each decision so travel / option execution, history and checksums keep
  flowing through vanilla code.
- **Screens used for layout and seams** — `NMapScreen.Instance` / `PlayerVoteDictionary` /
  `TravelToMapCoord`, `NEventRoom.Instance`, `NEventLayout.OptionButtons`,
  `NRun.Instance.GlobalUi.MultiplayerPlayerContainer` and `NMultiplayerPlayerState.Player` (the HUD
  avatars grown by the map winner reveal, located via `GetNodeOrNull("%CharacterIcon")`).
- **Networking** — `RunManager.Instance.NetService` (`INetGameService`): `RegisterMessageHandler` / `UnregisterMessageHandler` / `SendMessage` / `GetStatsForPeer`, `NetGameType`, `ConnectionStats.PingMsec`.
- **Custom net messages** — the relic-fight messages `RpsMoveChosenMessage`, `RpsRoundResolvedMessage`,
  `RpsRoundBeganMessage`, and the conflict messages `ConflictRpsBeganMessage`,
  `ConflictRpsRoundBeganMessage`, `ConflictRpsMoveMessage`, `ConflictRpsRoundResolvedMessage`,
  `ConflictRpsResolvedMessage` all implement the stock `INetMessage` / `IPacketSerializable` and are
  auto-discovered by the game's message registry. **All peers need the same mod build**, because those
  types have to line up on both ends.
- **UI / assets** — stock GodotSharp nodes only for the picker and hand overlays, `CharacterModel.ArmRock/Paper/ScissorsTexture` for the gesture art and `ArmPointingTexture` for the choosing phase, `ImageHelper.GetImagePath`, `SfxCmd.Play("event:/sfx/ui/clicks/ui_click")`, `NRun.Instance.GlobalUi.RelicInventory`, `NGame.Instance.CursorManager`.
- **RNG** — `Rng.Chaotic`, used only to fill a move for a player who times out, and by the stock fallback.
- **Settings / localization** — `RitsuLibFramework.RegisterModSettings` / `CreateLocalization`, `I18N`,
  `ModSettingsText.I18N`. The eng/zhs tables are embedded in the DLL as
  `STS2BetterRockPaperScissors.localization.settings.<lang>.json`, so nothing extra is deployed.

### Stable / beta compatibility

The same Mod DLL targets Steam v0.107.1 and public-beta v0.113.0. Players in a lobby
use the same game version and Mod build.

- On v0.107.1, the relic award callback takes the results list and the fight holder stays in its
  original parent until the grab. On v0.113.0, the callback also takes `isFinished`, results have
  `relicIndex`, and the fight holder moves beside the backstop. Non-final award batches use the
  game's original flow.
- Both versions wait for Godot's `Finished` signal and stop waiting when the tween is killed or
  its owner exits.
- Run-start message registration resolves the map synchronizer constructor with `RunState` on
  v0.107.1 and `IRunState` on v0.113.0. The map roulette is replaced after a completed map fight
  picks the travel destination.

## Settings

All settings live on the mod's RitsuLib page and are saved globally between sessions.

| Key | Default | Meaning |
| --- | --- | --- |
| `countdown_seconds` | 3 | seconds per round before an unchosen gesture is filled at random |
| `allow_reset` | off | host-only; let players clear/re-choose during the countdown (always rides the full countdown) |
| `restore_hands_on_resume` | off | local fix for relic-fight hands that stay off-screen after closing the pause menu |
| `map_conflict_rps` | off | manual RPS for split map votes (host-only) |
| `event_conflict_rps` | off | manual RPS for split shared-event option votes (host-only) |
