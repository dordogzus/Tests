DC-Coop - LAN co-op for up to 12 players (Dead Cells, HashLink)
================================================================

INSTALL
  Copy these files into the Dead Cells folder (where deadcells.exe and
  hlboot.dat are):
      winmm.dll  dccoop.hdll  dccoop.ini  dccoop_hooks.txt
  Start the game normally. Everyone on the LAN does the same.

UNINSTALL
  Delete winmm.dll and dccoop.hdll (and hlboot.dccoop.*, dccoop*.log).
  The original hlboot.dat is never modified.

PLAYING
  mode = auto (default): the first player to start hosts, the others join
  automatically. Or set mode = host / join in dccoop.ini.
  Allow the game through Windows Firewall (UDP 47770-47771) on the host.
  F9 toggles your PvP opt-in (both players must opt in).

LOGS
  dccoop_loader.log  patching result (which hooks were applied or skipped)
  dccoop.log         session events

FALLBACK (if winmm.dll is not picked up)
  tools\dccoop-patch.exe hlboot.dat hlboot.coop.dat
  then back up hlboot.dat and replace it with hlboot.coop.dat.

To disable the mod without deleting files, set the environment variable
DCCOOP_DISABLE=1 or set mode = off.
