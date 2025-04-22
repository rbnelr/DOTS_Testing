using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Unity.Entities;
using Unity.Transforms;
using Unity.Collections;
using Unity.Burst;
using Unity.Profiling;
using Unity.Jobs;

// TODO: Delete/Spawn only the difference in entity count, to proof on concept actually being able to init new entities
// Instead of this being a system that knows when to spawn things, in practice numerous systems need to spawn things in different ways
// I think the right way to solve this is to have a class/system for spawning (per type of entity to spawn)
// Maybe we can say: VehicleSpawner.Get(), which returns something containing the ECB which can be passed into jobs
// vehSpawn.SpawnAt(pos, ...) will then compute the init component data and create the entity using the ecb, which will later execute
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ControllerECSSystem))]
[RequireMatchingQueriesForUpdate]
[BurstCompile]
public partial struct SpawnerSystem : ISystem {
	
	static readonly ProfilerMarker perfDestroy = new ProfilerMarker("SpawnerSystem.Destroy");
	static readonly ProfilerMarker perfSpawn  = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.Spawn");
	static readonly ProfilerMarker perfInit  = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.Init");
	
	EntityQuery query;

	NativeList<Entity> spawnedEntities;
	int spawned;

	public void OnCreate (ref SystemState state) {
		query = new EntityQueryBuilder(Allocator.Temp).WithAll<LocalTransform, MyEntityData>().Build(ref state);
		spawnedEntities = new NativeList<Entity>(1000, Allocator.Persistent);
		spawned = 0;
	}
	public void OnStartRunning (ref SystemState state) {
		state.RequireForUpdate<ControllerECS>();
	}
	public void OnStopRunning (ref SystemState state) {
		Debug.Log("SpawnerSystem.OnStopRunning");
		DestroyAll(ref state);
		spawnedEntities.Dispose();
	}
	
	[BurstCompile]
	public void OnUpdate (ref SystemState state) {
		//Debug.Log("SpawnerSystem.OnUpdate");
		
		var c = SystemAPI.GetSingleton<ControllerECS>();
		UpdateSpawnEntities(ref state, c);
	}

	[BurstCompile]
	void DestroyAll (ref SystemState state) {
		state.EntityManager.DestroyEntity(spawnedEntities.AsArray());
		spawnedEntities.Clear();
	}
	
	[BurstCompile]
	void UpdateSpawnEntities (ref SystemState state, in ControllerECS c) {
		if (c.SpawnCount > spawnedEntities.Length) {
			Entity SpawnEntity = c.Mode == 1 ? c.SpawnPrefab : c.CustomSpawnPrefab;

			const int MaxSpawnPerFrame = 1024;
			int startIdx = spawnedEntities.Length;
			int count = math.min(c.SpawnCount - spawnedEntities.Length, MaxSpawnPerFrame);
			//int startIdx = spawned;
			//int count = math.min(c.SpawnCount - spawned, MaxSpawnPerFrame);

		#if true
			// Single threaded, ~0.382ms
			perfSpawn.Begin();
			var newEntities = state.EntityManager.Instantiate(SpawnEntity, count, Allocator.Temp);
			spawnedEntities.AddRange(newEntities);
			perfSpawn.End();
			
			perfInit.Begin();
			for (int idx=startIdx; idx<spawnedEntities.Length; idx++) {
				var data = init_entity(idx, c);
				
				state.EntityManager.SetComponentData(spawnedEntities[idx], LocalTransform.FromPosition(data.BasePositon));
				state.EntityManager.SetComponentData(spawnedEntities[idx], data);
			}
			perfInit.End();
		#elif false
			// Threaded with ECB, 0.366ms for job, 1.07ms for Playback
			// also seemingly no way to get list of spawned entities
			var ecb = new EntityCommandBuffer(Allocator.TempJob);

			// initialize entity positions via job
			var job = new SpawnJob{
				ctrl = c,
				SpawnEntity = SpawnEntity,
				Ecb = ecb.AsParallelWriter(),
				StartIndex = startIdx,
			}.Schedule(count, 128, state.Dependency);

			job.Complete();
		perfSpawn.Begin();
			ecb.Playback(state.EntityManager);
			ecb.Dispose();

			spawned += count;
		perfSpawn.End();
		#else
			// Single threaded, ~0.382ms
			perfSpawn.Begin();
			var newEntities = state.EntityManager.Instantiate(SpawnEntity, count, Allocator.Temp);
			spawnedEntities.AddRange(newEntities);
			perfSpawn.End();
			
			var job = new InitJob{
				ctrl = c,
				SpawnEntity = SpawnEntity,
				StartIndex = startIdx,
				entities = spawnedEntities.AsArray(),
				c_transform = state.GetComponentLookup<LocalTransform>(false),
				c_data = state.GetComponentLookup<MyEntityData>(false),
			}.Schedule(count, 128, state.Dependency);
			
			//job.Complete();
			state.Dependency = job;
		#endif
			
			Debug.Log($"{count} Entities Spawned => Count now {spawnedEntities.Length}");
		}
		else if (c.SpawnCount < spawnedEntities.Length) {
		perfDestroy.Begin();

			int count = spawnedEntities.Length - c.SpawnCount;
			var toRemove = spawnedEntities.AsArray().GetSubArray(spawnedEntities.Length - count, count);
		
			state.EntityManager.DestroyEntity(toRemove);
		
			spawnedEntities.RemoveRange(spawnedEntities.Length - count, count);

		perfDestroy.End();

			Debug.Log($"{count} Entities Destroyed => Count now {spawnedEntities.Length}");
		}
	}
	
	static MyEntityData init_entity (int idx, in ControllerECS c) {
		int i = idx;
		int x = i % c.Tiling.x;
		i        /= c.Tiling.x;
		int z = i % c.Tiling.y;
		i        /= c.Tiling.y;
		int y = i;
		
		return new MyEntityData {
			BasePositon = float3(x,1+y,z) * c.Spacing,
			Color =  MyEntityData.RandColor(idx)
		};
	}

	//[WithAll(typeof(LocalTransform), typeof(MyEntityData))]
	//[WithNone(typeof(Parent))]
	[BurstCompile]
	public partial struct SpawnJob : IJobParallelFor {
		[ReadOnly] public ControllerECS ctrl;
		public Entity SpawnEntity;
		public EntityCommandBuffer.ParallelWriter Ecb;
		public int StartIndex;
		
		[BurstCompile]
		public void Execute (int idx) {
			var entity = Ecb.Instantiate(0, SpawnEntity);
			var data = init_entity(StartIndex + idx, ctrl);
	
			Ecb.SetComponent(0, entity, LocalTransform.FromPosition(data.BasePositon));
			Ecb.SetComponent(0, entity, data);
		}
	}

	[BurstCompile]
	public partial struct InitJob : IJobParallelFor {
		[ReadOnly] public ControllerECS ctrl;
		public Entity SpawnEntity;
		public int StartIndex;

		[ReadOnly] public NativeArray<Entity> entities;
		[NativeDisableParallelForRestriction] // Not sure which safety check is affected here, just aliasing (writing to same entity from two threads) or also job dependecy?
		public ComponentLookup<LocalTransform> c_transform;
		[NativeDisableParallelForRestriction]
		public ComponentLookup<MyEntityData> c_data;
		
		[BurstCompile]
		public void Execute (int idx) {
			var entity = entities[StartIndex + idx];
			var data = init_entity(StartIndex + idx, ctrl);
	
			c_transform[entity] = LocalTransform.FromPosition(data.BasePositon);
			c_data[entity] = data;
		}
	}
}
