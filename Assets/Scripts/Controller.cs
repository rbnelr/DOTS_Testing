using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Unity.Transforms;
using Unity.Rendering;
using System.Linq;
using Unity.Collections;
using Unity.Burst;
using System.Runtime.InteropServices;
using Unity.Profiling;

public class Controller : MonoBehaviour {
	public int Mode = 2;
	
	[Range(0.0f, 1.0f)]
	public float Ratio = 0.1f;

	public int Max = 100000;
	public int2 Tiling = int2(50, 50);

	public float Spacing = 3;

	public float LODBias = 1;

	public int3 ChunkGridSize = int3(10, 10, 10);

	public GameObject SpawnPrefab;

	public bool Dynamic = false;

	public bool DebugSpatialGrid = false;

	class Baker : Baker<Controller> {
		public override void Bake (Controller authoring) {
			var entity = GetEntity(TransformUsageFlags.None);
			AddComponent(entity, new ControllerECS {
				SpawnPrefab = GetEntity(authoring.SpawnPrefab, TransformUsageFlags.Dynamic),

				Mode             = authoring.Mode,
				Ratio            = authoring.Ratio  ,
				Max              = authoring.Max    ,
				Tiling           = authoring.Tiling ,
				Spacing          = authoring.Spacing,
				LODBias          = authoring.LODBias,
				ChunkGridSize    = authoring.ChunkGridSize,
				Dynamic          = authoring.Dynamic,
				DebugSpatialGrid = authoring.DebugSpatialGrid,
			});;
		}
	}
}

public struct ControllerECS : IComponentData {
	public int Mode;

	public Entity SpawnPrefab;
	
	public float Ratio;
	public int Max;
	public int SpawnCount => (int)ceil(Max * Ratio);

	public int2 Tiling;
	public float Spacing;

	public float LODBias;

	public int3 ChunkGridSize;
	
	[MarshalAs(UnmanagedType.U1)]
	public bool Dynamic;

	[MarshalAs(UnmanagedType.U1)]
	public bool DebugSpatialGrid;
}

// Only way to directly call Burst from managed via static class?
[BurstCompile]
public static class Spawner {
	
	static readonly ProfilerMarker perfDestroy  = new ProfilerMarker(ProfilerCategory.Loading, "Spawner.DestroyAll");
	static readonly ProfilerMarker perfSpawn  = new ProfilerMarker(ProfilerCategory.Loading, "Spawner.UpdateSpawnEntities");

	[BurstCompile]
	public static void DestroyAll (in EntityManager em, ref NativeList<Entity> entities) {
		perfDestroy.Begin();

		em.DestroyEntity(entities.AsArray());
		entities.Clear();

		perfDestroy.End();
	}
	
	[BurstCompile]
	public static void UpdateSpawnEntities (in EntityManager em, ref NativeList<Entity> entities, in ControllerECS c, in Entity SpawnEntity) {
		perfSpawn.Begin();

		if (c.SpawnCount > entities.Length && SpawnEntity != Entity.Null) {

			const int MaxSpawnPerFrame = 1024 * 4;
			int startIdx = entities.Length;
			int count = math.min(c.SpawnCount - entities.Length, MaxSpawnPerFrame);
			//int startIdx = spawned;
			//int count = math.min(c.SpawnCount - spawned, MaxSpawnPerFrame);

			// Single threaded
			var newEntities = em.Instantiate(SpawnEntity, count, Allocator.Temp);
			entities.AddRange(newEntities);
			
			for (int idx=startIdx; idx<entities.Length; idx++) {
				var ent = entities[idx];
				var data = init_entity(idx, c);
				
				var transf = LocalTransform.FromPosition(data.BasePositon);
				em.SetComponentData(ent, transf);
				em.SetComponentData(ent, data);
				//if (c.Mode == 2) {
				//	// TODO: faster to defer to UpdateSpatialGridSystem, or faster to spawn single entities with correct shared value instantly?
				//	em.SetSharedComponent(ent, CustomEntity.SpatialGrid.ForTransform(c, transf));
				//}
			}

			//Debug.Log($"{count} Entities Spawned => Count now {spawnedEntities.Length}");
		}
		else if (c.SpawnCount < entities.Length) {

			int count = entities.Length - c.SpawnCount;
			var toRemove = entities.AsArray().GetSubArray(entities.Length - count, count);
		
			em.DestroyEntity(toRemove);
		
			entities.RemoveRange(entities.Length - count, count);

			//Debug.Log($"{count} Entities Destroyed => Count now {spawnedEntities.Length}");
		}

		perfSpawn.End();
	}
	
	public static MyEntityData init_entity (int idx, in ControllerECS c) {
		int i = idx;
		int x = i % c.Tiling.x;
		i        /= c.Tiling.x;
		int z = i % c.Tiling.y;
		i        /= c.Tiling.y;
		int y = i;
		
		return new MyEntityData {
			BasePositon = float3(x,5+y,z) * c.Spacing,
			Color =  MyEntityData.RandColor(idx)
		};
	}
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[RequireMatchingQueriesForUpdate]
public unsafe partial class ControllerECSSystem : SystemBase {
	
	Entity CustomSpawnPrefab; // not in ControllerECS as it resets when changed from inspector

	NativeList<Entity> spawnEntities;

	protected override void OnCreate () {
		RequireForUpdate<ControllerECS>(); // Baked scene gets streamed in, so ControllerECS does not exist for a few frames

		spawnEntities = new NativeList<Entity>(1000, Allocator.Persistent);
	}
	protected override void OnDestroy () {
		Spawner.DestroyAll(EntityManager, ref spawnEntities);
		spawnEntities.Dispose();
	}

	void Init () {
		var c = SystemAPI.GetSingleton<ControllerECS>();

		if (c.SpawnPrefab != Entity.Null && !EntityManager.HasComponent<MyEntityData>(c.SpawnPrefab)) {
			// Add MyEntityData so it can be moved just like custom entity (but still gets rendered using Entity.Graphics
			// NOTE: color will not respect my random coloring
			EntityManager.AddComponent<MyEntityData>(c.SpawnPrefab);
		}
		
		if (c.SpawnPrefab != Entity.Null && CustomSpawnPrefab == Entity.Null) {
			var prefab = EntityManager.CreateEntity(
				typeof(LocalTransform),
				typeof(Prefab),
				typeof(MyEntityData),
				typeof(CustomEntity.Asset),
				typeof(CustomEntity.SpatialGrid)
			);
			
			var rma = EntityManager.GetSharedComponentManaged<RenderMeshArray>(c.SpawnPrefab);
			if (rma.MeshReferences.Length > 0 && rma.MeshReferences[0].IsValid()) { // RenderMeshArray streamed in later?
				EntityManager.SetSharedComponentManaged(prefab,
					new CustomEntity.Asset(rma.MeshReferences[0], rma.MaterialReferences.Select(x => x.Value).ToArray()));

				EntityManager.SetSharedComponent(prefab, CustomEntity.SpatialGrid.Invalid);

				EntityManager.AddChunkComponentData<CustomEntity.ChunkBounds>(prefab);

				CustomSpawnPrefab = prefab;
			}
		}

		ref var sys = ref EntityManager.WorldUnmanaged.GetExistingSystemState<DynamicEntityUpdateSystem>();
		sys.Enabled = c.Dynamic;

		SystemAPI.SetSingleton(c);
	}
	
	protected override void OnStartRunning () {
		Init();
	}
	
	void SwitchMode (ref ControllerECS c, int Mode) {
		c.Mode = Mode;
		Spawner.DestroyAll(EntityManager, ref spawnEntities);
	}

	protected override void OnUpdate () {
		Init();

		var c = SystemAPI.GetSingleton<ControllerECS>();

		if (Keyboard.current.digit0Key.wasPressedThisFrame) SwitchMode(ref c, 0);
		if (Keyboard.current.digit1Key.wasPressedThisFrame) SwitchMode(ref c, 1);
		if (Keyboard.current.digit2Key.wasPressedThisFrame) SwitchMode(ref c, 2);

		if (Keyboard.current.gKey.wasPressedThisFrame) c.Dynamic = !c.Dynamic;

		if (Keyboard.current.numpadPlusKey .isPressed) c.Ratio += 0.5f * SystemAPI.Time.DeltaTime;
		if (Keyboard.current.numpadMinusKey.isPressed) c.Ratio -= 0.5f * SystemAPI.Time.DeltaTime;
		c.Ratio = clamp(c.Ratio, 0.0f, 1.0f);

		char active0 = c.Mode == 0 ? '*':' ';
		char active1 = c.Mode == 1 ? '*':' ';
		char active2 = c.Mode == 2 ? '*':' ';
		var dyn = c.Dynamic ? "Dynamic":"Static";

		DebugHUD.Show($"{active0}[0]: GameObjects {active1}[1]: ECS Unity Graphics {active2}[2]: ECS CustomRenderEntity");
		DebugHUD.Show($" [G] {dyn} Entities");

		DebugHUD.Show($" [+/-] Spawn {c.SpawnCount} Entities");

		QualitySettings.lodBias = 2.0f * c.LODBias;

		GO_Spawner.inst.enabled = c.Mode == 0;

		Entity SpawnEntity = c.Mode == 1 ? c.SpawnPrefab : CustomSpawnPrefab;
		Spawner.UpdateSpawnEntities(EntityManager, ref spawnEntities, c, SpawnEntity);

		SystemAPI.SetSingleton(c);
	}

}
