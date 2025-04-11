using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Unity.Transforms;
using Unity.Rendering;
using System.Linq;
using Unity.Burst;
using Unity.Scenes;

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

	public bool DebugSpatialGrid;

	public void SwitchMode (int Mode) {
		this.Mode = Mode;
		Respawn = true;
	}
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
public unsafe partial class ControllerECSSystem : SystemBase {
	
	protected override void OnCreate () {
		RequireForUpdate<ControllerECS>(); // Baked scene gets streamed in, so ControllerECS does not exist for a few frames
	}

	void Init () {
		var c = SystemAPI.GetSingleton<ControllerECS>();

		if (!EntityManager.HasComponent<SpinningSystem.Data>(c.SpawnPrefab)) {
			EntityManager.AddComponent<SpinningSystem.Data>(c.SpawnPrefab);
		}
		
		if (c.CustomSpawnPrefab == Entity.Null) {
			var prefab = EntityManager.CreateEntity(
				typeof(LocalTransform),
				typeof(Prefab),
				typeof(SpinningSystem.Data),
				typeof(CustomRenderAsset),
				typeof(CustomEntitySpatialGrid)
			);
			
			var rma = EntityManager.GetSharedComponentManaged<RenderMeshArray>(c.SpawnPrefab);
			EntityManager.SetSharedComponentManaged(prefab,
				new CustomRenderAsset(rma.MeshReferences[0], rma.MaterialReferences.Select(x => x.Value).ToArray()));

			EntityManager.AddChunkComponentData<CustomEntityChunkBounds>(prefab);

			c.CustomSpawnPrefab = prefab;
		}

		SystemAPI.SetSingleton(c);
	}
	
	protected override void OnStartRunning () {
		Init();
	}

	protected override void OnUpdate () {
		Init();

		var c = SystemAPI.GetSingletonRW<ControllerECS>();

		char active0 = c.ValueRO.Mode == 0 ? '*':' ';
		char active1 = c.ValueRO.Mode == 1 ? '*':' ';
		char active2 = c.ValueRO.Mode == 2 ? '*':' ';

		DebugHUD.Show($"{active0}[0]: GameObjects {active1}[1]: ECS Unity Graphics {active2}[2]: ECS CustomRenderEntity");
		
		if (Keyboard.current.digit0Key.wasPressedThisFrame) c.ValueRW.SwitchMode(0);
		if (Keyboard.current.digit1Key.wasPressedThisFrame) c.ValueRW.SwitchMode(1);
		if (Keyboard.current.digit2Key.wasPressedThisFrame) c.ValueRW.SwitchMode(2);

		QualitySettings.lodBias = 2.0f * c.ValueRO.LODBias;

		GO_Spawner.inst.enabled = c.ValueRO.Mode == 0;
	}
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(SpawnerSystem))]
[BurstCompile]
public partial struct SpinningSystem : ISystem {
	public struct Data : IComponentData {
		public float3 BasePositon;
		public Color Color;
	}
	
	float time01;

	[BurstCompile]
	public void OnCreate (ref SystemState state) {
		time01 = 0;
	}

	[BurstCompile]
	public void OnUpdate (ref SystemState state) {
		time01 += Time.deltaTime / 6;
		time01 %= 1;

		new Job{
			dt = SystemAPI.Time.DeltaTime,
			time01 = time01,
		}.ScheduleParallel();
	}
	
	static float min_speed => radians(20);
	static float max_speed => radians(360);
	const float bob_height = 36.0f;

	[BurstCompile]
	public partial struct Job : IJobEntity {
		public float dt;
		public float time01;
		
		[BurstCompile]
		void Execute (Entity entity, ref LocalTransform transform, in Data data) {
			var rand = new Unity.Mathematics.Random(hash(int2(entity.Index, entity.Version)));
			
			var spin_axis = rand.NextFloat3Direction();
			var spin_speed = rand.NextFloat(min_speed, max_speed);
			var rotation = Unity.Mathematics.quaternion.AxisAngle(spin_axis, spin_speed * dt);

			var pos = data.BasePositon;
			pos.y += bob_height * noise.pnoise(pos / 200 + float3(time01,time01,0), float3(1, 1, 5));

			transform = LocalTransform.FromPositionRotation(pos, mul(transform.Rotation, rotation));
		}
	}
}
