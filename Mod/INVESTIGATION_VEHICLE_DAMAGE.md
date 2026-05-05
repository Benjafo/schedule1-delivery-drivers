# Investigation: AI-Driven Vehicle Striking NPC Pedestrians

## Context
During M4 testing, an AI-driven vehicle (player-owned LandVehicle with NPC driver via VehicleAgent.Navigate) struck a pedestrian NPC. This investigation determines what the game engine does with that event.

---

## 1. Damage Application

**Yes, damage IS applied.**

When a vehicle hits an NPC at >10 km/h, the collision is processed in `NPCMovement.CheckHit()`:

- **File:** `ScheduleOne/NPCs/NPCMovement.cs:408-469`
- **Gate condition (line 456):** `if (landVehicle.LocalPlayerIsDriver || (landVehicle.DriverPlayer == null && InstanceFinder.IsServer))`
- **Our scenario:** `LocalPlayerIsDriver = false`, `DriverPlayer = null` (NPC driver, see below), `InstanceFinder.IsServer = true` (single player) → **condition is TRUE**

The damage path:
1. Ragdoll activated (`ActivateRagdoll_Server()`) — line 443
2. `onHitByCar` event fires — line 448-450
3. Impact created with `impactSource = null` (because `DriverPlayer` is null) — lines 458-461
4. `npc.SendImpact(impact)` called — line 466
5. In `NPC.RpcLogic___ReceiveImpact_427288424` (NPC.cs:1704-1741):
   - `Health.TakeDamage(impactDamage, false)` — line 1733 (non-lethal since `EImpactType.BluntMetal` is not in the lethal list)
   - Damage formula: `120f * (relativeVelocity * 3.6 / 100)` — at 30 km/h relative ≈ 36 damage

**Why `DriverPlayer` is null:** `LandVehicle.DriverPlayer` (line 188-198) returns `Seats[0].Occupant` which is typed as `Player` (VehicleSeat.cs:24). NPCs are tracked separately via `OccupantNPCs` (LandVehicle.cs:215). Our mod's NPC driver is NOT a `Player`, so `DriverPlayer` returns null.

**Confidence: HIGH** — direct code path, no ambiguity.

---

## 2. Attribution Model

**No attribution.** The impact source is explicitly null.

The attribution chain:
- `NPCMovement.cs:458-461`: `impactSource` is set to `landVehicle.DriverPlayer.NetworkObject` — but only if `DriverPlayer != null`. In our case it stays null.
- `Impact.cs:48-57`: `IsPlayerImpact()` checks if `ImpactSource` has a `Player` component. With null source → returns false.
- `NPC.cs:1729-1731`: `NotifyAttackedByPlayer` only called if `IsPlayerImpact` returns true → **skipped**.

The game does NOT:
- Attribute damage to the player because they own the vehicle
- Attribute damage to the NPC driver (no concept of NPC-as-attacker in this system)
- Check `IsPlayerOwned` for blame purposes

**Confidence: HIGH** — the null propagation is unambiguous through the entire chain.

---

## 3. Relationship Impact

**Confirmed NO for our scenario.**

Two pathways could modify relationships after a vehicle hit:

### Path A: ImpactReceived response system
- `NPCResponses.ImpactReceived()` (NPCResponses.cs:127-160)
- Calls `RespondToFirstNonLethalAttack`, `RespondToLethalAttack`, etc. — but ONLY if `impact.IsPlayerImpact(out perpetrator)` returns true
- With null ImpactSource → `IsPlayerImpact` returns false → **all relationship-modifying response code is skipped**
- `ChangeRelationship(-0.25f)` at NPCResponses.cs:167 is never reached
- `ChangeRelationship(-1f)` at NPCResponses.cs:185 is never reached

### Path B: HitByCar response system
- `NPCResponses.HitByCar()` (NPCResponses.cs:104-124)
- **Line 110:** `if (vehicle.DriverPlayer != null && this.npc.Movement.TimeSinceHitByCar > 2f)`
- `DriverPlayer` is null → **entire block skipped**, no relationship penalty, no crime recorded

### Customer/Dealer specifics
- `Customer` has `[RequireComponent(typeof(NPC))]` (Customer.cs:49) — Customers ARE physical NPCs that can be hit
- `Dealer` extends `NPC` (Dealer.cs:42) — Dealers can also be hit
- Both use `NPC.RelationData.ChangeRelationship()` for relationship changes
- Neither class overrides `HitByCar` — they inherit the base `NPCResponses` or `NPCResponses_Civilian` behavior
- `NPCResponses_Civilian` does NOT override `HitByCar` (confirmed: no match in file)
- Since the base `HitByCar` guards on `DriverPlayer != null`, **no relationship penalty occurs for Customers or Dealers**

**Confidence: HIGH** — two independent paths both gate on player presence, neither fires.

---

## 4. Wanted Level / Police Response

**Confirmed NO for our scenario.**

The game HAS a crime/police system:
- `PlayerCrimeData` class tracks crimes, pursuit levels (ScheduleOne/PlayerScripts/PlayerCrimeData.cs)
- `VehicularAssault` crime type exists (ScheduleOne/Law/VehicularAssault.cs)
- `NPCResponses_Police.HitByCar()` (line 24-45) applies `VehicularAssault` crime and escalates pursuit
- `NPCResponses.HitByCar()` (line 112-118) records collisions and calls `RecordVehicleCollision`
- 3+ collisions in 30 seconds triggers `VehicularAssault` + police (PlayerCrimeData.cs:151-157)
- `NPCResponses_Civilian.RespondToLethalOrRepeatedAttack()` (line 149-167) can call `CallPolice_Networked`

**All paths require `vehicle.DriverPlayer != null` or `impact.IsPlayerImpact() == true`:**
- `NPCResponses_Police.HitByCar` line 35: `if (vehicle.DriverPlayer != null && vehicle.DriverPlayer.IsOwner ...)`
- `NPCResponses.HitByCar` line 110: `if (vehicle.DriverPlayer != null ...)`
- `NPCResponses_Civilian` crime-calling in `RespondToLethalOrRepeatedAttack`: only reached via `IsPlayerImpact` check

With null driver player → **no crime recorded, no police called, no wanted level change.**

**Confidence: HIGH** — every crime path gates on a non-null Player reference.

---

## 5. Witness Behavior

**No witness propagation for NPC-driver vehicle collisions.**

### ISightable system
- `NPC` implements `ISightable` (NPC.cs:57) — NPCs are sightable objects
- `NPCAwareness` uses a `VisionCone` to detect `EVisualState` changes (NPCAwareness.cs:24, 39-137)
- `EVisualState` enum (Vision/EVisualState.cs) contains: Visible, Suspicious, DisobeyingCurfew, Vandalizing, PettyCrime, DrugDealing, Wanted, Pickpocketing, DischargingWeapon, Brandishing
- **There is NO visual state for "vehicle hitting NPC" or "vehicular assault"**
- ISightable on NPC exposes the NPC's NetworkObject (NPC.cs:1391-1393) — it's for the NPC being SEEN, not for detecting vehicle incidents

### onHitByCar event on bystanders
- `NPCAwareness.HitByCar(vehicle)` (NPCAwareness.cs:172-181) is a handler on the HIT NPC's awareness, not on witnesses
- `NPCMovement.onHitByCar` (UnityEvent) fires on the struck NPC only (NPCMovement.cs:448-450)
- No code propagates a "vehicle hit someone" event to nearby NPCs
- The noise/hearing system (`NoiseEvent`) handles gunshots and explosions (NPCAwareness.cs:141-168) but not vehicle collisions

### What happens to nearby NPCs
- Nothing. Witness NPCs don't react to seeing another NPC hit by a car.
- The only exception: if a **police officer** is personally hit (`NPCResponses_Police.HitByCar`), but even that requires `DriverPlayer != null`.

**Confidence: HIGH** — exhaustive check of EVisualState, no vehicle-collision visual state exists, no propagation mechanism found.

---

## 6. Bottom-Line Recommendation

**Ignore for now; revisit during polish (low priority).**

The game's attribution system completely fails to assign blame when `DriverPlayer` is null. No relationship penalty, no crime, no police, no witness reaction. The only observable effect is: the struck NPC ragdolls, takes damage (potentially dying if enough damage), plays hurt VO, and gets an "Angry" emotion for 5 seconds. Since there are no gameplay consequences to the player, and the visual effect (NPC getting ragdolled) is infrequent and cosmetic, this is a polish-tier issue at most. If NPC health/death becomes a concern (e.g., killing a Customer), that would be a separate, more serious issue worth mitigating via pathfinding avoidance rather than post-collision handling.

---

## 7. Confidence Summary

| Question | Confidence | Notes |
|----------|-----------|-------|
| (1) Damage application | **High** | Direct code path traced end-to-end |
| (2) Attribution model | **High** | Null propagation confirmed through all layers |
| (3) Relationship impact | **High** | Both response paths (ImpactReceived + HitByCar) confirmed gated |
| (4) Wanted/police | **High** | All crime paths require non-null Player reference |
| (5) Witness behavior | **High** | No visual state or event propagation for vehicle hits |

---

## Key File References

| File | Lines | Relevance |
|------|-------|-----------|
| `ScheduleOne/NPCs/NPCMovement.cs` | 396-469 | Vehicle collision detection and impact creation |
| `ScheduleOne/NPCs/NPCMovement.cs` | 456 | Critical gate: `LocalPlayerIsDriver \|\| (DriverPlayer == null && IsServer)` |
| `ScheduleOne/Vehicles/LandVehicle.cs` | 188-198 | `DriverPlayer` property — returns `Seats[0].Occupant` (typed as Player) |
| `ScheduleOne/Vehicles/VehicleSeat.cs` | 24 | `Occupant` field is type `Player` |
| `ScheduleOne/Vehicles/LandVehicle.cs` | 215 | `OccupantNPCs` — separate NPC tracking |
| `ScheduleOne/Combat/Impact.cs` | 48-57 | `IsPlayerImpact()` — checks ImpactSource for Player component |
| `ScheduleOne/Combat/Impact.cs` | 44 | `IsLethal()` — BluntMetal is NOT lethal |
| `ScheduleOne/NPCs/NPC.cs` | 1704-1741 | `ReceiveImpact` — damage applied, NotifyAttackedByPlayer gated |
| `ScheduleOne/NPCs/NPCHealth.cs` | 190-193 | `NotifyAttackedByPlayer` — sets HoursSinceAttackedByPlayer |
| `ScheduleOne/NPCs/Responses/NPCResponses.cs` | 104-124 | `HitByCar` — gates on `DriverPlayer != null` |
| `ScheduleOne/NPCs/Responses/NPCResponses.cs` | 127-160 | `ImpactReceived` — response system gates on `IsPlayerImpact` |
| `ScheduleOne/NPCs/Responses/NPCResponses.cs` | 163-192 | Relationship change methods (-0.25f, -1f) |
| `ScheduleOne/NPCs/Responses/NPCResponses_Civilian.cs` | 96-167 | Civilian responses — fight/callpolice, all require Player perpetrator |
| `ScheduleOne/Police/NPCResponses_Police.cs` | 24-45 | Police HitByCar — gates on `DriverPlayer != null` |
| `ScheduleOne/PlayerScripts/PlayerCrimeData.cs` | 486-490 | `RecordVehicleCollision` — only called with Player.CrimeData |
| `ScheduleOne/PlayerScripts/PlayerCrimeData.cs` | 151-157 | 3-collision escalation — unreachable without Player |
| `ScheduleOne/NPCs/NPCAwareness.cs` | 39-137 | Vision event handler — no vehicle-hit visual state |
| `ScheduleOne/Vision/EVisualState.cs` | 6-28 | Full enum — no vehicular assault state |
| `ScheduleOne/Economy/Customer.cs` | 49, 168 | Customer is RequireComponent(NPC), has .NPC property |
| `ScheduleOne/Economy/Dealer.cs` | 42 | Dealer extends NPC directly |
