// Removed in v2.2: runtime prefab clones never registered in the garage
// (the catalog is baked at build time - confirmed via garage log: 308 rows,
// 0 clones visible). Per feedback, grants are now MORE OF THE ORIGINAL parts
// through the game's own budget query (see CrewParts.ApplyBonus hook on
// Core/Singleton.GetMaxAvailableComponents). This file is intentionally empty.
