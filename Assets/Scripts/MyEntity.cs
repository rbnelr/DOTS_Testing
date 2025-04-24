using Unity.Entities;
using UnityEngine;
using Unity.Transforms;
using Unity.Burst;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;


public struct MyEntityData : IComponentData {
	public float3 BasePositon;
	public Color Color;
	
	public static Color RandColorH (uint hash) {
		var rand = new Random(hash);
		return Color.HSVToRGB(rand.NextFloat(), 1, 0.6f);
	}
	//public static Color RandColor (Entity entity) {
	//	return RandColor(hash(int2(entity.Index, entity.Version)));
	//}
	public static Color RandColor (int idx) {
		return RandColorH(hash(int2(idx, 0))); // no hash for int?
	}
	public static Color RandColor (int3 cell) {
		return RandColorH(hash(cell));
	}
}


[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(ControllerECSSystem))]
[BurstCompile]
public partial struct DynamicEntityUpdateSystem : ISystem {
	
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
		void Execute (Entity entity, ref LocalTransform transform, in MyEntityData data) {
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
