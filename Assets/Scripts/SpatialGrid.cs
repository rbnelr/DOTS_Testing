using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using float3x4 = Unity.Mathematics.float3x4;
using float4x4 = Unity.Mathematics.float4x4;
using quaternion = Unity.Mathematics.quaternion;

using UnityEngine;
using Unity.Burst;
using Unity.Transforms;
using Unity.Profiling;
using Unity.Burst.Intrinsics;

namespace CustomEntity {

public struct SpatialGrid : ISharedComponentData {
	public int3 Key;

	public static int3 InvalidKey = int.MinValue;
	public static SpatialGrid Invalid => new SpatialGrid { Key = InvalidKey };
}
public struct ChunkBounds : IComponentData {
	public AABB bounds;
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(DynamicEntityUpdateSystem))]
[BurstCompile]
public partial struct UpdateSpatialGridSystem : ISystem {
	
	EntityQuery query;

	EntityTypeHandle c_entities;
	ComponentTypeHandle<LocalTransform> c_transformsRO;
	SharedComponentTypeHandle<SpatialGrid> c_spatialGridRO;
		
	static readonly ProfilerMarker perf = new ProfilerMarker("UpdateSpatialGridSystem.Move");

	public void OnCreate (ref SystemState state) {
		query = new EntityQueryBuilder(Allocator.Temp).WithAll<Asset, LocalTransform, SpatialGrid>().Build(ref state);
		//query.AddChangedVersionFilter(typeof(Asset)); // Not really needed since entities don't really need to be able to change their asset (just delete and create new entity at some position if really needed)
		//query.AddChangedVersionFilter(typeof(LocalTransform));
		query.SetChangedVersionFilter(typeof(LocalTransform));

		c_entities = state.GetEntityTypeHandle();
		c_transformsRO = state.GetComponentTypeHandle<LocalTransform>(true);
		c_spatialGridRO = state.GetSharedComponentTypeHandle<SpatialGrid>();

		state.RequireForUpdate<ControllerECS>();
	}

	[BurstCompile]
	public void OnUpdate (ref SystemState state) {
		var controller = SystemAPI.GetSingleton<ControllerECS>();

		// Show chunk outlines one frame delayed because it's easier to schedule while using BeginPresentationEntityCommandBufferSystem
		if (controller.DebugSpatialGrid) {
			state.EntityManager.GetAllUniqueSharedComponents<SpatialGrid>(out var grid, Allocator.Temp);
			foreach (var cell in grid) {
				DebugGridCell(controller, cell);
			}
		}

		if (query.IsEmpty) return;
		
		c_entities.Update(ref state);
		c_transformsRO.Update(ref state);
		c_spatialGridRO.Update(ref state);
		
		int NumEntities = query.CalculateEntityCount();
		
		// Inspired by https://github.com/ITR13/DOTS-Particle-Life/blob/main/Assets/Scripts/SwapChunkSystem.cs
		// I would prefer to only allocate memory for however many entities actually moved though
		// Either we could somehow limit sorting to some number per frame and simply defer moves for any additional entities (increasing bounds) of chunks they were in
		// which would probably be fine if memory size is tuned close to actual number of moved entities per frame (eg. usually 100 entities switch chunks per frame, but sometimes, like on loading 10000k entities change, if we allow for a 1000 sized buffer we can catch up)
		// this hash map is presumably unsafe if Add is called too often, but I could limit using a Interlocked.Add
		// Alternatively we could push into a dynamically growing list (only possible with thread-local lists?)
		// thread-local lists (or hashmaps) could quickly be processed in a separate IJob (iterate hashmap from each thread and apply chunk changes, or aggregate into single hashmap and do in order of chunk)
		var movedEntities = new NativeParallelMultiHashMap<int3, Entity>(NumEntities, Allocator.TempJob);

		var spatialJob = new UpdateSpatialGridJob{
			Entities = c_entities,
			LocalTransforms = c_transformsRO,
			SpatialGrid = c_spatialGridRO,
			LastSystemVersion = state.LastSystemVersion,
			controller = controller,
			//Ecb = ecb.AsParallelWriter(),
			MovedEntities = movedEntities.AsParallelWriter(),
		}.ScheduleParallel(query, state.Dependency);
			
		spatialJob.Complete();

		perf.Begin();
		//var ecb = SystemAPI.GetSingleton<BeginPresentationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
			
		foreach (var key in movedEntities.GetKeyArray(Allocator.Temp)) {
			int count = movedEntities.CountValuesForKey(key);
			var values = new NativeArray<Entity>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

			int i=0;
			foreach (var val in movedEntities.GetValuesForKey(key)) {
				values[i++] = val;
			}

			state.EntityManager.SetSharedComponent(values, new SpatialGrid { Key = key });
		}
		perf.End();

		if (controller.DebugSpatialGrid) {
			Debug.Log($"UpdateSpatialGridSystem: Moved Entities: {movedEntities.Count()}");
		}

		movedEntities.Dispose(spatialJob);
		state.Dependency = spatialJob;

		//NativeTimer.test();
	}

	void DebugGridCell (in ControllerECS controller, in SpatialGrid cell) {
		float3 size = controller.ChunkGridSize;
		float3 lower = (float3)cell.Key * size;

		float3 a = lower + size * float3(0,0,0);
		float3 b = lower + size * float3(1,0,0);
		float3 c = lower + size * float3(1,1,0);
		float3 d = lower + size * float3(0,1,0);
		float3 e = lower + size * float3(0,0,1);
		float3 f = lower + size * float3(1,0,1);
		float3 g = lower + size * float3(1,1,1);
		float3 h = lower + size * float3(0,1,1);

		Color col = MyEntityData.RandColor(cell.Key);

		Debug.DrawLine(a,b, col);
		Debug.DrawLine(b,c, col);
		Debug.DrawLine(c,d, col);
		Debug.DrawLine(d,a, col);
		Debug.DrawLine(e,f, col);
		Debug.DrawLine(f,g, col);
		Debug.DrawLine(g,h, col);
		Debug.DrawLine(h,e, col);
		Debug.DrawLine(a,e, col);
		Debug.DrawLine(b,f, col);
		Debug.DrawLine(c,g, col);
		Debug.DrawLine(d,h, col);
	}
		
	public static int3 CalcGridCell (in ControllerECS controller, float3 cur_pos) {
		float3 gridMul = 1.0f / (float3)controller.ChunkGridSize;

		// TODO: use ChunkEntityEnumerator everywhere?
		int3 spatialKey = (int3)floor(cur_pos * gridMul);
		return spatialKey;
	}

	[BurstCompile]
	unsafe partial struct UpdateSpatialGridJob : IJobChunk {
		[ReadOnly] public EntityTypeHandle Entities;
		[ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransforms;
		[ReadOnly] public SharedComponentTypeHandle<SpatialGrid> SpatialGrid;
		[ReadOnly] public ControllerECS controller;

		public uint LastSystemVersion;

		//public NativeArray<int> DebugCount;
		//public EntityCommandBuffer.ParallelWriter Ecb;

		public NativeParallelMultiHashMap<int3, Entity>.ParallelWriter MovedEntities;

		[BurstCompile]
		public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
			NativeArray<Entity> entities = chunk.GetNativeArray(Entities);
			NativeArray<LocalTransform> transforms = chunk.GetNativeArray(ref LocalTransforms);
			var curSpatialGrid = chunk.GetSharedComponent(SpatialGrid);

			float3 gridMul = 1.0f / (float3)controller.ChunkGridSize;

			// TODO: use ChunkEntityEnumerator everywhere?
			for (int i=0; i<chunk.Count; i++) {
				int3 spatialKey = (int3)floor(transforms[i].Position * gridMul);

				if (any(spatialKey != curSpatialGrid.Key)) {
					//Ecb.SetSharedComponent(unfilteredChunkIndex, entities[i], new CustomEntitySpatialGrid { Key = spatialKey });

					MovedEntities.Add(spatialKey, entities[i]);

					//Interlocked.Add(ref ((int*)DebugCount.GetUnsafePtr())[0], 1);
				}
			}
		}
	}
}

}
