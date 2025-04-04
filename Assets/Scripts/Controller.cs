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

	public GameObject SpawnPrefab;

	class Baker : Baker<Controller> {
		public override void Bake (Controller authoring) {
			var entity = GetEntity(TransformUsageFlags.None);

			// Create CustomSpawnPrefab during baking, which lets us put it into ControllerECS so the spawner can access it
			var customPrefab = CreateAdditionalEntity(TransformUsageFlags.ManualOverride, false, "CustomSpawnPrefab");
			{
				AddComponent<Prefab>(customPrefab);
				AddComponent<SpinningSystem.Tag>(customPrefab);
				AddComponent<LocalTransform>(customPrefab);

				var mesh = authoring.SpawnPrefab.GetComponent<MeshFilter>().sharedMesh;
				var mats = authoring.SpawnPrefab.GetComponent<MeshRenderer>().sharedMaterials;
				AddSharedComponentManaged(customPrefab, new CustomRenderAsset(mesh, mats));

				// Implement LOD later
				//var rma = EntityManager.GetSharedComponentManaged<RenderMeshArray>(SpawnPrefab);
				//EntityManager.SetSharedComponentManaged(customPrefab,
				//	new CustomRenderAsset(rma.MeshReferences[0], rma.MaterialReferences.Select(x => x.Value).ToArray()));
			}


			AddComponent(entity, new ControllerECS {
				Mode = authoring.Mode,
				Respawn = true, // Respawn whenever inspector changes TODO: add way to change entity count in actual build?
				SpawnPrefab = GetEntity(authoring.SpawnPrefab, TransformUsageFlags.Dynamic),
				CustomSpawnPrefab = customPrefab,
				Ratio   = authoring.Ratio  ,
				Max     = authoring.Max    ,
				Tiling  = authoring.Tiling ,
				Spacing = authoring.Spacing,
				LODBias = authoring.LODBias,
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

	public void SwitchMode (int Mode) {
		this.Mode = Mode;
		Respawn = true;
	}
}

[UpdateInGroup(typeof(SimulationSystemGroup))]
[RequireMatchingQueriesForUpdate]
public unsafe partial class ControllerECSSystem : SystemBase {
	
	bool init;
	//Entity CustomSpawnPrefab; // putting this in ControllerECS causes issues as entity get forgotten as CustomSpawnPrefab variable is reset when rebaking due to inspector changes
	
	protected override void OnCreate () {
		RequireForUpdate<ControllerECS>(); // Baked scene gets streamed in, so ControllerECS does not exist for a few frames
		init = true;
	}

	void Init () {
		init = false;

		var c = SystemAPI.GetSingleton<ControllerECS>();
		
		// Cant do this inside Baker<Controller> for some reason, despite the fact that this is where the SpawnPrefab entity is created,
		// apparently I could create a seperate script with its own baker which does this and add it to the prefab
		EntityManager.AddComponent<SpinningSystem.Tag>(c.SpawnPrefab);

		////
		//var prefab = EntityManager.CreateEntity(
		//	typeof(Prefab),
		//	typeof(SpinningSystem.Tag),
		//	typeof(LocalTransform),
		//	typeof(CustomRenderAsset)
		//);
		//
		//// Implement LOD later
		//var rma = EntityManager.GetSharedComponentManaged<RenderMeshArray>(c.SpawnPrefab);
		//EntityManager.SetSharedComponentManaged(prefab,
		//	new CustomRenderAsset(rma.MeshReferences[0], rma.MaterialReferences.Select(x => x.Value).ToArray()));
		//
		//CustomSpawnPrefab = prefab;

		//SystemAPI.SetSingleton(c);
	}

	protected override void OnUpdate () {
		if (init)
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
			var rand = new Unity.Mathematics.Random(hash(int2(entity.Index, entity.Version)));

			var spin_axis = rand.NextFloat3Direction();
			var speed = rand.NextFloat(min_speed, max_speed);

			var rotation = Unity.Mathematics.quaternion.AxisAngle(spin_axis, speed * dt);
			transform = transform.Rotate(rotation);
		}
	}
}
