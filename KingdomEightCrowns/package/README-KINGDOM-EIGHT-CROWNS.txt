KINGDOM EIGHT CROWNS 0.14.21-alpha
==================================
Extends Kingdom Two Crowns co-op from 2 to 8 players.
Requires Kingdom Two Crowns version 2.1.4 (Steam) and BepInEx 6 IL2CPP
(be.753 or compatible) already installed in the game folder.

WHAT'S IN THE PACKAGE
---------------------
BepInEx/plugins/KingdomEightCrowns/KingdomEightCrowns.dll
    Core plugin (0.6.23-alpha): connection limits, player ID limits,
    expanded appearance/kingdom lookups.
BepInEx/plugins/KingdomEightCrowns/KingdomEightCrowns.AppearanceFlow.dll
    Companion plugin (0.14.21-alpha): dynamic 8-player session handling,
    join catch-up, packet relay, roster crossfade + scroll lock,
    scrollbar, Arabic day numbers, pixel-font nicknames, version badge.
BepInEx/config/openai.kingdomtwocrowns.eightcrowns.cfg
BepInEx/config/openai.kingdomtwocrowns.eightcrowns.appearanceflow.cfg
    Mod settings. MaxPlayers=2 reverts to pure vanilla networking
    (parity mode); MaxPlayers=8 enables the extended topology.

INSTALL
-------
1. Copy the included "BepInEx" folder over your game's folder so the
   files land in:
   <game>\BepInEx\plugins\KingdomEightCrowns\
   <game>\BepInEx\config\
2. EVERY PLAYER who joins must install the same mod version.
3. Launch the game. The BepInEx console should list both plugins:
   "Kingdom Eight Crowns 0.6.23-alpha" and
   "Kingdom Eight Crowns - Appearance Flow 0.14.21-alpha".
   Overwrite the two .cfg files too: older configs had developer probes
   switched on.

REPORTING PROBLEMS
------------------
Send <game>\BepInEx\LogOutput.log from the HOST and from EVERY client of
the same session (they are overwritten on each launch, so copy them before
restarting), plus what each player saw and roughly when. Only set
[Diagnostics] HotPathTracing = true when asked; it adds per-RPC logging.

WHAT'S FIXED IN 0.14.21
-----------------------
Players / bodies
- Players 3-8 no longer drop out of the game's player list (coins,
  building, interactions) when someone with a lower number leaves or is
  still joining; the list is rebuilt without gaps.
- The host keeps building and removing bodies after Player 2 leaves (it
  used to freeze and keep destroyed bodies in the player list).
- A body that fails to build retries with a short back-off instead of
  re-cloning a whole Player every frame, and no longer blocks the others.
- Bodies destroyed by a level change are rebuilt instead of failing
  forever with "NetID ... is occupied".
- Developer probes are off by default (they swapped in fake bodies for
  15 seconds if the companion ever failed to load).

Rulers / appearance
- Each remote ruler is applied to a body once, and only from the model
  the player actually picked. Duplicate deliveries (raw relay + host
  re-broadcast + echo back to the sender) re-ran the game's model setup
  on live bodies, which is what corrupted them.
- A joining player no longer receives the host's placeholder as its own
  look; Player 2's clone is dressed on Player 3+ machines; a client can
  only publish its own ruler; a Player 3+ pick no longer overwrites the
  real Player 2's ruler on the host.
- Rulers of players who left are cleared, so the next joiner in that
  slot does not inherit them.

Network
- One exception inside the game's receive or catch-up code no longer
  leaves the host routing everything to a single player for the rest of
  the session (lag, desync, "ghost" world state).
- One bad packet or one failing player no longer stalls everyone's
  outgoing traffic; relays no longer go to the same player twice.
- The first player's join no longer gets live world updates mixed into
  its catch-up (affected plain 2-player joins too).
- A player who needs catch-up again (level load) is no longer kicked by
  the join watchdog within a second; slow ruler picks get 15 minutes.
- Leaving the Steam lobby now clears all peer state, so the next hosted
  session does not start with phantom players (early "session full",
  wrong player numbers, a join that never starts).
- [Multiplayer] MaxPlayers is honoured by the companion too; MaxPlayers=2
  really is vanilla networking now.

Menu / UI
- Short game texts made of Roman-numeral letters ("I", "Mix", "DLC", a
  nickname like "Mimi"...) are no longer turned into numbers and
  restyled; only the day counter is converted (now up to day 3999).
- Closing the menu or the Online panel no longer leaves a stray copy of
  the scrollbar banner on screen; its textures are freed.
- Less per-frame reflection work; companion settings are read from
  BepInEx's config folder even under non-standard launches.

Still open (needs logs from a real session): see REPORTING PROBLEMS.

WHAT'S FIXED IN 0.14.20
-----------------------
- ADDITIONAL PLAYERS NOW WEAR THEIR OWN CHOSEN RULER ON EVERY MACHINE. The
  joining client pushes its own monarch model straight to the host the
  moment its body activates (the stock skin-select chain never fired for
  additional players, so every machine kept the static placeholder look for
  them). The host stores it, forwards it to all other ready clients, and
  re-applies it to the local body clone once; clients re-apply it the same
  way. A body whose model attach ever faulted is never touched again
  (re-running a faulted attach corrupts it), so those keep the fallback
  look by design.
- Watch the host log for "Pushed this client's own ruler model to the
  host", "Stored remote Player 3's appearance", "Forwarded Player 3's
  ruler model", and "Re-applied Player 3's stored appearance" — that chain
  is the new appearance pipeline working end to end.

WHAT'S FIXED IN 0.14.19
-----------------------
- Guarded the object-registration table against negative NetID entries: a
  clone hierarchy's invalidated stamp children could reach the game's
  registration with NetID -1, colliding with a previous -1 entry and
  throwing "An item with the same key has already been added" mid-receive.
  Those registrations are skipped and logged once.
- No other behaviour changed; this is the stable 0.14.18 baseline plus the
  guard. The next milestone is delivering each ruler's chosen skin to every
  machine so additional players render their own monarch instead of the
  static placeholder.

WHAT'S FIXED IN 0.14.18
-----------------------
- URGENT ROLLBACK: 0.14.17's automatic model-attach retry corrupted the
  additional players' clone bodies (the game's attach step must not be
  re-run on a live body — the second pass faults mid-attach and leaves the
  clone permanently broken, freezing the visible skin, hiding the ruler,
  and flooding the log with per-frame Player.Update errors). The retry is
  fully removed; the clones behave exactly as in the stable 0.14.16 build.
  The frozen-skin issue will be fixed properly by delivering each ruler's
  chosen appearance to every machine instead of re-running the attach.

WHAT'S FIXED IN 0.14.17
-----------------------
- A remote ruler's skin that stayed frozen at the spawn point while the
  player's invisible position kept moving (the "frozen body with a moving
  ghost" report) is repaired automatically: the game's model-attach step
  sometimes faults for an additional player's clone during the join; the
  mod now detects the fault and retries the attach every second until the
  skin is on, logging "clone model setup retry succeeded".
- The log's "body-mapping desync" red lines no longer fire for ordinary
  world entities (mounts, coins, animals); only a NetID the local machine
  has truly never registered is reported. CRPC volume samples are logged
  every 5000 instead of every 500.

WHAT'S FIXED IN 0.14.16
-----------------------
- The mod caption's size is now absolute instead of inherited from whichever
  menu label happened to be captured at startup (the source label's point
  size differs per menu state, which silently grew the caption 25% and
  shifted its K off the vanilla caption's K). It is pinned to the approved
  caption-matching size, so both KINGDOM lines start at the same x and the
  mod line sits directly below the vanilla one, 1:1.
- Counter digits are forced back to the vanilla white every tick. The
  day-15 save presents the game's own cursed-mode blue (blue currency icon,
  red nights) — a game-side save state, not mod rendering; the digits now
  read normal regardless. The blue currency icon itself is the save's
  state and needs a fresh/normal save to revert.

WHAT'S FIXED IN 0.14.15
-----------------------
- The mod's own version caption is back under the vanilla one and can no
  longer land on the wrong side of the screen. The 0.14.14 experiment that
  anchored it to a canvas corner shipped it off-screen on every machine
  (the menu hierarchy has no canvas at its root); it is back on the proven
  banner-relative world placement, now re-measured every tick instead of
  freezing during roster crossfades — which is what had stranded it on the
  left on a joining client.

WHAT'S FIXED IN 0.14.14
-----------------------
- Three-player sessions no longer drown both clients in red
  KeyNotFoundException walls from SteamNetworkConnection.Receive. The host's
  packet relay previously re-sent client packets through the game's own
  SteamSend, which overwrites the packet's message-count header with the
  target connection's pending counter and resets that connection's outgoing
  buffer mid-accumulation: receiving clients parsed payload bytes as message
  codes (the repeating key '46'/'0' errors), and the target's own queued
  outgoing group was silently corrupted. The relay now sends through the raw
  Steamworks SendP2PPacket call the game itself ends in, leaving every packet
  byte-for-byte intact and every connection's buffers untouched. This also
  removes the connection-lag/stutter and the ghost/desynced world state
  (trees, builders) that the corrupted groups caused.
- The third player's frozen-body-with-detached-soul symptom had its input
  CRPCs caught in those same corrupted groups; with intact relayed packets
  they now resolve against his body NetID on every machine.
- Coins and building interactions work for the third player again. The stock
  game sizes per-player coin indicators for two rulers and faults inside
  native Payable/LockIndicator code for a third; the resilience patches now
  recognize the wrapped IL2CPP form of that fault (previously they matched
  only the raw managed type, so every fault escaped) and also cover
  LockIndicator.Init and PayableUpgrade.OnSelect directly.
- The KINGDOM EIGHT CROWNS version caption no longer lands on the left side
  of the screen on some clients. It was positioned relative to the roster
  banners at whatever instant the measurement ran (mid-crossfade on a joining
  client); it is now anchored to the bottom-right corner, stateless and
  animation-proof.
- CRPC routing diagnostics now hook the game's real dispatch
  (NetworkPostbox.CallCRPC) instead of a method the live path never calls, so
  a healthy session no longer reads as zero routed CRPCs.

HOSTING FOR 8 PLAYERS
---------------------
1. Main menu -> COOP -> host a campaign as usual.
2. Friends join via Steam invite or the friends list; the lobby holds 8.
3. Each joining client picks its own ruler during catch-up.

KNOWN BEHAVIOUR
---------------
- The roster shows two banners at a time; scroll (or PageUp/PageDown)
  steps one page per gesture: (1-2) -> (3-4) -> (5-6) -> (7-8). A thin
  scrollbar beside the banners shows the position.
- Day counters render Arabic numerals (8 instead of VIII).
- Steam nicknames in the roster use the game's pixel font.
- The menu shows "KINGDOM EIGHT CROWNS <version>" under the game's own
  version caption, bottom-right.

TROUBLESHOOTING
---------------
- Logs: <game>\BepInEx\LogOutput.log and
  %USERPROFILE%\AppData\LocalLow\noio\KingdomTwoCrowns\Player.log
- If the game fails to reach the menu, remove both DLLs from
  BepInEx/plugins/KingdomEightCrowns/ to revert to vanilla.
