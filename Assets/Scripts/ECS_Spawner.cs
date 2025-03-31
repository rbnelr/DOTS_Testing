using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;
using System.Linq;
using Unity;
using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Rendering;
using Unity.Profiling;
using Unity.Entities.Graphics;

public class ECS_SpawnerAuthoring : MonoBehaviour {
	
	public GameObject Prefab;

	[Range(0.0f, 1.0f)]
	public float Ratio = 0.1f;

	public int Max = 100000;
	public int2 Tiling = int2(50, 50);

	public float Spacing = 3;

	public float LODBias = 1;

	public bool CustomRenderEntity = false;

	private class Baker : Baker<ECS_SpawnerAuthoring> {
		public override void Bake (ECS_SpawnerAuthoring authoring) {
			var entity = GetEntity(TransformUsageFlags.None);

			var s = new Spawner {
				Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
				Ratio   = authoring.Ratio  ,
				Max     = authoring.Max    ,
				Tiling  = authoring.Tiling ,
				Spacing = authoring.Spacing,
				LODBias = authoring.LODBias,
				CustomRenderEntity = authoring.CustomRenderEntity,
				has_run = false,
				prefab_entity = Entity.Null,
			};
			AddComponent(entity, s);
		}
	}
}

public struct Spawner : IComponentData {
	public Entity Prefab; // Actual baked prefab
	public Entity prefab_entity; // Own replication of prefab to test which components are actually needed (shrink memory use and try to avoid slow instantiate with normal prefab)
	
	public float Ratio;

	public int Max;
	public int2 Tiling;

	public float Spacing;

	public float LODBias;

	public bool CustomRenderEntity;

	public int SpawnCount => (int)ceil(Max * Ratio);

	public bool has_run;
}

[UpdateInGroup(typeof(InitializationSystemGroup))]
public partial struct SpawnerSystem : ISystem {
	
	static readonly ProfilerMarker perfDestroyAll = new ProfilerMarker("SpawnerSystem.DestroyAll");
	static readonly ProfilerMarker perfSpawnAll  = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.SpawnAll");
	static readonly ProfilerMarker perfSpawnAll1 = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.SpawnAll1");
	static readonly ProfilerMarker perfSpawnAll2 = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.SpawnAll2");

	public void OnCreate (ref SystemState state) {
		//Debug.Log(">>>>> OnCreate");
	}
	
	public void OnStopRunning (ref SystemState state) {
		Debug.Log(">>>>> OnStopRunning");
		DestroyAll(ref state);

		//if (SystemAPI.TryGetSingleton<Spawner>(out var spawn) && spawn.prefab_entity != Entity.Null) {
		//	state.EntityManager.DestroyEntity(spawn.prefab_entity);
		//	spawn.prefab_entity = Entity.Null;
		//	SystemAPI.SetSingleton(spawn);
		//}
	}
	
	public void OnUpdate (ref SystemState state) {
		//Debug.Log(">>>>> OnUpdate");
		if (!SystemAPI.TryGetSingleton<Spawner>(out var spawn) || spawn.has_run)
			return;
		
		if (spawn.prefab_entity == Entity.Null) {
			var em = state.EntityManager;

			if (spawn.CustomRenderEntity) {
				spawn.prefab_entity = em.CreateEntity(
					typeof(Prefab),
					typeof(SpinningSystem.Tag),
					typeof(LocalTransform),
					typeof(CustomRenderAsset)
				);

				// Implement LOD later
				var rma = em.GetSharedComponentManaged<RenderMeshArray>(spawn.Prefab);
				em.SetSharedComponentManaged(spawn.prefab_entity, new CustomRenderAsset(rma.MeshReferences[0], rma.MaterialReferences.Select(x => x.Value).ToArray()));
			}
			else {
				int mode = 0;
				if (mode == 0) {
					spawn.prefab_entity = spawn.Prefab;
					//spawn.prefab_entity = em.Instantiate(spawn.Prefab);
					//em.AddComponent<Prefab>(spawn.prefab_entity);
					em.AddComponent<SpinningSystem.Tag>(spawn.prefab_entity);
				}
				else {
					spawn.prefab_entity = em.CreateEntity(
						typeof(Prefab),
						typeof(WorldToLocal_Tag),
						typeof(SpinningSystem.Tag),
					
						typeof(LocalTransform), // Why do we not need this?
						typeof(LocalToWorld),

						typeof(PerInstanceCullingTag),
						typeof(RenderBounds),
						typeof(WorldRenderBounds),

						typeof(RenderMeshArray),
						typeof(RenderFilterSettings),
						typeof(MaterialMeshInfo)
					);

					em.SetComponentData(spawn.prefab_entity, em.GetComponentData<RenderBounds>(spawn.Prefab));
					//em.SetComponentData<WorldRenderBounds>(spawn.prefab_entity, em.GetComponentData<WorldRenderBounds>(spawn.Prefab));

					em.SetSharedComponentManaged(spawn.prefab_entity, em.GetSharedComponentManaged<RenderMeshArray>(spawn.Prefab));
					em.SetSharedComponentManaged(spawn.prefab_entity, em.GetSharedComponentManaged<RenderFilterSettings>(spawn.Prefab));
					em.SetComponentData(spawn.prefab_entity, em.GetComponentData<MaterialMeshInfo>(spawn.Prefab));
				}
			}
		}

		{
			Debug.Log($">> spawner SpawnCount: {spawn.SpawnCount}");
		
			perfDestroyAll.Begin();
			using (Timer.Start(t => Debug.Log($"DestroyAll took {t*1000}ms")))
				DestroyAll(ref state);
			perfDestroyAll.End();
				
			perfSpawnAll.Begin();
			using (Timer.Start(t => Debug.Log($"SpawnAll took {t*1000}ms")))
				SpawnAll(ref state, ref spawn);
			perfSpawnAll.End();
		}

		//QualitySettings.lodBias = 2.0f * spawn.LODBias;
		
		spawn.has_run = true;
		SystemAPI.SetSingleton(spawn);
	}
	
	[BurstCompile]
	[WithAll(typeof(LocalTransform))]
	[WithNone(typeof(Parent))]
	public partial struct InitJob : IJobEntity {
		[ReadOnly] public Spawner s;
		
		void Execute ([EntityIndexInQuery] int idx, ref LocalTransform transform) {
			int x = idx % s.Tiling.x;
			idx        /= s.Tiling.x;
			int z = idx % s.Tiling.y;
			idx        /= s.Tiling.y;
			int y = idx;
		
			transform = LocalTransform.FromPosition(float3(x,1+y,z) * s.Spacing);
		}
	}

	[BurstCompile]
	void SpawnAll (ref SystemState state, ref Spawner s) {
		// bulk instantiate entities from prefabs
		perfSpawnAll1.Begin();
		var entities = state.EntityManager.Instantiate(s.prefab_entity, s.SpawnCount, Allocator.Temp);
		entities.Dispose();
		perfSpawnAll1.End();

		// initialize entity positions via job
		perfSpawnAll2.Begin();
		var job = new InitJob{ s = s }.ScheduleParallel(new JobHandle());
		job.Complete(); // Complete for profiler timing
		perfSpawnAll2.End();
	}
	
	[BurstCompile]
	void DestroyAll (ref SystemState state) {
		using (var entities = state.EntityManager.CreateEntityQuery(typeof(LocalTransform)).ToEntityArray(Allocator.Temp)) {
			state.EntityManager.DestroyEntity(entities);
		}
	}
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial struct SpinningSystem : ISystem {
	public struct Tag : IComponentData {}
	
	[BurstCompile]
	public void OnUpdate (ref SystemState state) {
		new Job{ dt = SystemAPI.Time.DeltaTime }.ScheduleParallel();
	}

	[BurstCompile]
	[WithAll(typeof(Tag))]
	public partial struct Job : IJobEntity {
		public float dt;
		float min_speed => radians(20);
		float max_speed => radians(360);
		
		void Execute (Entity entity, ref LocalTransform transform) {
			var rand = new Random(hash(int2(entity.Index, entity.Version)));

			var spin_axis = rand.NextFloat3Direction();
			var speed = rand.NextFloat(min_speed, max_speed);

			var rotation = Unity.Mathematics.quaternion.AxisAngle(spin_axis, speed * dt);
			transform = transform.Rotate(rotation);
		}
	}
}
