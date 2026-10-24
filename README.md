# Airway Airflow Simulation (Unity)

Real-time visualisation of one breathing cycle (inhale, exhale) in a bronchial tree. Particles enter at the trachea, spread through the bronchial branches down to the alveoli, and leave through the trachea on exhale. The simulation runs on the mesh `Trachea_Trachea_mat_0`.

This is a visual simulation, not full CFD. The project contains two particle-control approaches that share the same goal.

| | Approach A: VSP-EL | Approach B: Confluence path |
|---|---|---|
| Idea | Voxel grid, distance field and flow field; particles follow precomputed paths | Branch capacity from local volume; particles are assigned to free space |
| Needs an explicit tree | No | No (adapted to flat paths here) |
| Status | Main, stable | Experimental |
| Files | `Voxelizer`, `VectorFieldBaker`, `AirwayParticleFlow` | `AirwayParticleFlowV2`, `AirwayParticleFlow2`, `AirwayPathBuilder` |

---

## Approach A: VSP-EL

**VSP-EL** (Voxel-based Spatial-Partitioning Eulerian–Lagrangian Approach) divides the airway into voxels (spatial partitioning), builds an Eulerian flow field on the grid, and moves Lagrangian particles along it.

```
Airway mesh -> Voxelizer -> VectorFieldBaker -> AirwayParticleFlow
               (voxel grid)  (flow field, paths)  (inhale / exhale particles)
```

### Voxelizer.cs
Turns the airway mesh into a voxel grid: a voxel is marked when it lies inside the airway lumen. It also cleans the result (fills holes, removes small noisy clusters) and can re-voxelise when the mesh deforms with the breathing animation, spreading the work over several frames to avoid stutter.
- Keys **E / Q**: show / hide the voxels for inspection.

### VectorFieldBaker.cs
Computes the flow map from the voxel grid:
- distance from every voxel to the inlet (trachea);
- alveoli positions (the deepest point of each branch);
- one path from the inlet to every alveolus;
- the flow direction at every voxel.

It can rebake periodically in the background while the mesh moves. A new result is published only when it is complete, so the display stays stable.
- Keys **R / T**: show / hide the vector field.

### AirwayParticleFlow.cs
Drives the particles with the breathing rhythm:
- **Inhale:** particles spawn gradually at the inlet, follow the paths towards the alveoli, then wait there.
- **Exhale:** particles travel back along the same path and disappear only at the inlet. Exhale speed is paced so it matches the configured exhale time.
- Particles wobble around the path axis to fill the lumen, and the wobble shrinks towards the alveoli.
- All controls and live statistics (particle count, phase, time) are visible in the Inspector.

---

## Approach B: Confluence-path control (experimental)

This approach takes its control logic from a confluence-path particle controller and applies it without building a branching tree. Its seven steps:

1. **Region capacity.** Split the structure into directed regions running from the inlet inwards. Each region gets a capacity equal to its volume divided by the volume of one particle, so density is set by capacity and larger regions receive more particles.
2. **Auto-shrinking particles.** If the total capacity is smaller than the requested particle count, reduce the particle size until it fits (capacity scales roughly with 1 / size³). The full count is always shown without overlap.
3. **Target assignment at spawn.** Each particle is given a random target position inside a region. The region is chosen with probability proportional to its free space (capacity minus particles already assigned), which favours sparse regions and avoids full ones.
4. **Even spawning.** During inhale, particles spawn at a constant rate (particle count / inhale time), so density rises gradually.
5. **Flow, then settle.** Each particle moves from the inlet along its path to the target and stops there. The number of settled particles is the density.
6. **Local repulsion.** Particles push away neighbours within their own radius, found with a spatial grid. Flowing particles push only sideways (perpendicular to travel) so they never jam at junctions. Settled particles push in all directions, which spreads them evenly. A small random wobble adds natural micro-motion.
7. **Paced exhale.** Particles flow back along their entry path. Speed is set by (maximum depth / exhale time), so the deepest particle reaches the inlet exactly when exhale ends and shallower ones leave earlier. Particles vanish only at the inlet, so cycles chain smoothly.

It can be applied to any structure that provides four things: one inlet, the volume of each region, a path from the inlet to each region, and the depth along that path.

In this project the regions are the flat paths produced by `VectorFieldBaker`, and radius is estimated from the distance to the wall. This version is still being tuned: particles can collapse into a thin line in narrow branches, so approach A remains the default.

Files: `AirwayParticleFlowV2.cs` (adaptation to the flat paths), `AirwayParticleFlow2.cs` and `AirwayPathBuilder` (original controller and tree builder).

---

## Requirements
- Unity 6.2 (6000.2.8f1)

## Quick start
1. Add `Voxelizer` and `VectorFieldBaker` to the airway mesh object.
2. Add `AirwayParticleFlow` to an object with a Particle System and assign the `VectorFieldBaker` reference.
3. Press Play. The system voxelises the mesh, bakes the flow field and starts the simulation.
4. If the mesh has a breathing animation, enable periodic rebake in `VectorFieldBaker`.

To try approach B, use `AirwayParticleFlowV2` instead of `AirwayParticleFlow`. It needs the `GetPathAt(int index)` method in `VectorFieldBaker`.

## Notes
- Voxel resolution controls both detail and frame rate: finer is more accurate but slower.
- Inspector values override the defaults in code.
