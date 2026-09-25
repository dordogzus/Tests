# Netcode

## Topology and authority

Star topology: the host relays all traffic, owns slots (0 = host, 1..11 = clients),
and is the time base. This suits LAN now and maps directly onto Steam P2P
later (only the transport changes).

| State | Authority | Replication |
|---|---|---|
| Hero movement/animation | Owner | Client → host → others, unreliable 30 Hz |
| Enemies (AI, HP, death) | Host | Snapshots, priority-limited; deaths reliable |
| Client hits on enemies | Attacker predicts, host applies | `M_HIT_MOB` reliable, threat added for that player |
| Enemy hits on remote hero | Host detects on ghost, owner applies | `M_HIT_PLAYER` reliable; owner's i-frames/dodge still apply |
| PvP hits | Attacker detects, host validates consent | `M_HIT_PLAYER` via host |
| Downed/revive | Owner | `M_LIFE` reliable |
| Level seed, roster, PvP flags | Host | Reliable |

Owner authority for heroes means Dead Cells' fast movement never waits on the
network. Host authority for enemies keeps one consistent world.

## Transport (src/core/channel.c, session.c)

- Datagram header: magic, type, 64-bit session token (random salts exchanged
  in a challenge handshake so spoofed packets are ignored).
- Channel header: `seq`, `ack`, 32-bit `ack_bits`, and a flag marking whether
  the ack fields are valid yet. Each packet acknowledges the last 33 received.
- Reliable messages ride along with packets and are resent after an RTO
  (`rtt + 4·rttvar`, RFC 6298 smoothing) until a carrying packet is acked.
  They're delivered in order from a 256-message window.
- Unreliable payload (latest state wins) fills the rest of the 1200-byte MTU.
- Tested with 25% loss and 0–80 ms jitter: 2000 reliable messages arrive in order.

## Snapshots

- Entity state is quantized: position 18 bits/axis (1/64 cell over
  4096 cells), velocity 12 bits (skipped when still), life as varint, anim
  10 bits, facing, flags. That's about 13 bytes per entity.
- Enemies are sent by a per-client priority accumulator (nearby enemies build
  priority 4× faster, far ones 0.25×). Each snapshot fits a 1000-byte budget,
  so distant crowds degrade gracefully instead of overflowing the MTU.
- Measured: ~165 KiB/s host upload for 11 clients with 40 enemies at 30 Hz.

## Time and interpolation

- Clients send their clock with each state; the host echoes it with host time.
  The offset estimate uses the minimum-RTT sample of the last 16 (least
  queueing error), snaps if off by more than 250 ms, and otherwise slews at
  ≤5% of elapsed time, so time never runs backwards.
- Remote entities render at `host_time − delay`, where
  `delay = 2·snapshot_interval + 2.5·jitter` (clamped to 50–350 ms). Samples use
  cubic Hermite curves through positions *and* velocities, which removes the
  kinks of linear interpolation. Teleports (>6 cells) snap, and gaps
  extrapolate for at most 120 ms.
- Measured in the 12-player test (10% loss): worst ghost deviation from the
  true path is about 0.01 cells.
