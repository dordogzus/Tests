# Balance model

All formulas live in `src/core/rules.c`, are unit tested, and are tunable in
`dccoop.ini`. `n` = players (1..12), `e = n − 1`.

## Enemies

More players means more total damage output (roughly `n×`). Pure HP scaling
makes fights spongy, and pure spawn scaling makes corridors chaotic, so both
levers are combined.

| Quantity | Formula | n=1 | n=2 | n=4 | n=8 | n=12 |
|---|---|---|---|---|---|---|
| Spawn density `D` | `min(1 + 0.20e, 2.5)` | 1.00 | 1.20 | 1.60 | 2.40 | 2.50 |
| Pressure target `R` | `1 + 0.03e` | 1.00 | 1.03 | 1.09 | 1.21 | 1.33 |
| Enemy HP | `n·R / D` | 1.00 | 1.72 | 2.73 | 4.03 | 6.38 |
| Elite HP | `HP · (1 + 0.02e)` | 1.00 | 1.75 | 2.89 | 4.60 | 7.79 |
| Boss HP | `n · max(0.5, 1 − 0.015e)` | 1.00 | 1.97 | 3.82 | 7.16 | 10.02 |
| Enemy damage | `min(1 + 0.05e, 1.5)` | 1.00 | 1.05 | 1.15 | 1.35 | 1.50 |
| Elite chance | `1 + 0.04e` | 1.00 | 1.04 | 1.12 | 1.28 | 1.44 |

The design invariant: the total enemy HP pool per player
(`HP · D / n = R`) stays between 1.0 and 1.33 of solo. It rises slightly
because groups gain coordination, revives, and focus fire. Bosses have no
density lever and see near-100% uptime in groups, so they scale almost
linearly. Enemy damage grows modestly because revives make downs cheaper.

**Live updates:** when someone joins or leaves, every living enemy is rescaled
from its stored base max-life, preserving its HP ratio. Rescaling never kills.

## Downed and revive

- A lethal hit while any ally is alive downs you at 1 HP instead of killing
  you. With nobody left to revive you, death is real.
- Bleed-out is `30 s · 0.75^downs` (min 6 s) and resets each level, so
  repeated downs get riskier.
- Revive takes `3 s · 0.7^(revivers − 1)` (min 1.2 s) while allies stand
  within 2.5 cells. Bleed-out pauses while you're being revived, and
  interrupted progress decays slowly (0.15/s) instead of resetting.
- Revive HP is `max(25%, 40% − 1.5%·e)`, so big groups still care about downs.
- A team wipe (nobody alive) ends the run.

## Enemy targeting (threat)

Each enemy on the host keeps per-player threat: damage dealt, decaying with a
4 s half-life. The target score is `(10·proximity + 0.1·threat) · taunt`.
Players already targeted by more than their fair share
(`ceil(enemies / players)`) get a ×0.6 penalty per extra enemy, so 12 players
don't all watch one person get swarmed. The current target is kept for at
least 1.5 s and until a rival scores 30% higher (hysteresis), which prevents
jitter. Downed and invisible players are never targeted.

## PvP

Mutual opt-in (both players toggled), no self-damage, and no damage in safe
zones or between team-mates in team mode. Damage ×0.35 (minimum 1), and 0.6 s
PvP hit immunity after each hit to prevent stun-lock. PvP damage uses the
downed/revive rules, so duels don't end runs.
