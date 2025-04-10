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

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ControllerECSSystem))]
[RequireMatchingQueriesForUpdate]
[BurstCompile]
public partial struct SpawnerSystem : ISystem {
	
	static readonly ProfilerMarker perfDestroyAll = new ProfilerMarker("SpawnerSystem.DestroyAll");
	static readonly ProfilerMarker perfSpawnAll  = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.SpawnAll");
	static readonly ProfilerMarker perfSpawnAll1 = new ProfilerMarker(ProfilerCategory.Loading, "SpawnerSystem.SpawnAll.Instantiate");
	
	public void OnStartRunning (ref SystemState state) {
		state.RequireForUpdate<ControllerECS>();
	}
	public void OnStopRunning (ref SystemState state) {
		Debug.Log("SpawnerSystem.OnStopRunning");
		DestroyAll(ref state);
	}
	
	[BurstCompile]
	public void OnUpdate (ref SystemState state) {
		//Debug.Log("SpawnerSystem.OnUpdate");
		
		var ctrl = SystemAPI.GetSingletonRW<ControllerECS>();
		if (!ctrl.ValueRO.Respawn)
			return;

		ctrl.ValueRW.Respawn = false;
		
		perfDestroyAll.Begin();
		//using (Timer.Start(t => Debug.Log($"DestroyAll took {t*1000}ms")))
			DestroyAll(ref state);
		perfDestroyAll.End();
		
		ctrl = SystemAPI.GetSingletonRW<ControllerECS>();
		if (ctrl.ValueRO.Mode <= 0)
			return; // GameObjects will be spawned

		Entity SpawnEntity = ctrl.ValueRW.Mode == 1 ? ctrl.ValueRO.SpawnPrefab : ctrl.ValueRO.CustomSpawnPrefab;

		perfSpawnAll.Begin();
		//using (Timer.Start(t => Debug.Log($"SpawnAll took {t*1000}ms")))
			SpawnAll(ref state, ctrl.ValueRO, SpawnEntity);
		perfSpawnAll.End();
	}
	
	[WithAll(typeof(LocalTransform))]
	[WithNone(typeof(Parent))]
	[BurstCompile]
	public partial struct InitJob : IJobEntity {
		[ReadOnly] public ControllerECS ctrl;
		
		[BurstCompile]
		void Execute ([EntityIndexInQuery] int idx, ref LocalTransform transform) {
			int x = idx % ctrl.Tiling.x;
			idx        /= ctrl.Tiling.x;
			int z = idx % ctrl.Tiling.y;
			idx        /= ctrl.Tiling.y;
			int y = idx;
		
			transform = LocalTransform.FromPosition(float3(x,1+y,z) * ctrl.Spacing);
		}
	}

	[BurstCompile]
	void SpawnAll (ref SystemState state, in ControllerECS ctrl, Entity SpawnEntity) {
		// bulk instantiate entities from prefabs
		perfSpawnAll1.Begin();
		var entities = state.EntityManager.Instantiate(SpawnEntity, ctrl.SpawnCount, Allocator.Temp);
		entities.Dispose();
		perfSpawnAll1.End();

		// initialize entity positions via job
		new InitJob{ ctrl = ctrl }.ScheduleParallel();
	}
	
	[BurstCompile]
	void DestroyAll (ref SystemState state) {
		var query = new EntityQueryBuilder(Allocator.Temp).WithAll<LocalTransform>().Build(state.EntityManager);
		using (var entities = query.ToEntityArray(Allocator.Temp)) {
			state.EntityManager.DestroyEntity(entities);
		}
	}
}
