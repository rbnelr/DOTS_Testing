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
				Mode = authoring.Mode,
				Respawn = true, // Respawn whenever inspector changes TODO: add way to change entity count in actual build?
				SpawnPrefab = GetEntity(authoring.SpawnPrefab, TransformUsageFlags.Dynamic),
				CustomSpawnPrefab = Entity.Null, // Create in ControllerECSSystem, as we can't really create custom entities here

				Ratio         = authoring.Ratio  ,
				Max           = authoring.Max    ,
				Tiling        = authoring.Tiling ,
				Spacing       = authoring.Spacing,
				LODBias       = authoring.LODBias,
				ChunkGridSize = authoring.ChunkGridSize,
				Dynamic = authoring.Dynamic,
				DebugSpatialGrid = authoring.DebugSpatialGrid,
			});;
		}
	}
}

public struct ControllerECS : IComponentData {
	public int Mode;
	public bool Respawn;

	public Entity SpawnPrefab;
	public Entity CustomSpawnPrefab;
	
	public float Ratio;
	public int Max;
	public int SpawnCount => (int)ceil(Max * Ratio);

	public int2 Tiling;
	public float Spacing;

	public float LODBias;

	public int3 ChunkGridSize;
	
	public bool Dynamic;

	public bool DebugSpatialGrid;

	public void SwitchMode (int Mode) {
		this.Mode = Mode;
		Respawn = true;
	}
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[RequireMatchingQueriesForUpdate]
[BurstCompile]
public unsafe partial class ControllerECSSystem : SystemBase {
	
	NativeList<Entity> spawnedEntities;

	protected override void OnCreate () {
		RequireForUpdate<ControllerECS>(); // Baked scene gets streamed in, so ControllerECS does not exist for a few frames

		spawnedEntities = new NativeList<Entity>(1000, Allocator.Persistent);
	}
	protected override void OnDestroy () {
		EntityManager.DestroyEntity(spawnedEntities.AsArray());
		spawnedEntities.Dispose();
	}

	void Init () {
		var c = SystemAPI.GetSingleton<ControllerECS>();

		if (c.SpawnPrefab != Entity.Null && !EntityManager.HasComponent<MyEntityData>(c.SpawnPrefab)) {
			EntityManager.AddComponent<MyEntityData>(c.SpawnPrefab);
		}
		
		if (c.SpawnPrefab != Entity.Null && c.CustomSpawnPrefab == Entity.Null) {
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

				c.CustomSpawnPrefab = prefab;
			}
		}

		ref var sys = ref EntityManager.WorldUnmanaged.GetExistingSystemState<DynamicEntityUpdateSystem>();
		sys.Enabled = c.Dynamic;

		SystemAPI.SetSingleton(c);
	}
	
	protected override void OnStartRunning () {
		Init();
	}

	protected override void OnUpdate () {
		Init();

		var c = SystemAPI.GetSingleton<ControllerECS>();

		if (Keyboard.current.digit0Key.wasPressedThisFrame) c.SwitchMode(0);
		if (Keyboard.current.digit1Key.wasPressedThisFrame) c.SwitchMode(1);
		if (Keyboard.current.digit2Key.wasPressedThisFrame) c.SwitchMode(2);

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

		//UpdateSpawnEntities(ref c);

		SystemAPI.SetSingleton(c);
	}

	//[BurstCompile]
	void UpdateSpawnEntities (ref ControllerECS c) {
		if (spawnedEntities.Length < c.SpawnCount) {
			const int MaxSpawnPerFrame = 500;

			Entity SpawnEntity = c.Mode == 1 ? c.SpawnPrefab : c.CustomSpawnPrefab;

			// bulk instantiate entities from prefabs
			int count = math.min(c.SpawnCount - spawnedEntities.Length, MaxSpawnPerFrame);
			//int count = c.SpawnCount - spawnedEntities.Length;
			var newEntities = EntityManager.Instantiate(SpawnEntity, count, Allocator.Temp);
		
			// initialize entity positions via job
			//new InitJob{ ctrl = ctrl }.ScheduleParallel();
		
			spawnedEntities.AddRange(newEntities);
		
			//newEntities.Dispose();

			Debug.Log($"{count} Entities Spawned => {spawnedEntities.Length}");
		}
		else if (spawnedEntities.Length > c.SpawnCount) {
			int count = spawnedEntities.Length - c.SpawnCount;
		
			var toRemove = spawnedEntities.AsArray().GetSubArray(spawnedEntities.Length - count, count);
		
			EntityManager.DestroyEntity(toRemove);
		
			spawnedEntities.RemoveRange(spawnedEntities.Length - count, count);
		}
		else {
			Debug.Log($"jhj");
		}
	}
}
