using System;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using float4x4 = Unity.Mathematics.float4x4;
using quaternion = Unity.Mathematics.quaternion;

using Unity.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Transforms;
using Unity.Profiling;

public class CustomEntityRenderer : MonoBehaviour {
	public static CustomEntityRenderer inst;

	public Mesh mesh;
	public Material material;

	BatchRendererGroup brg = null;

	BatchID batchID = BatchID.Null;
	BatchMeshID meshID;
	BatchMaterialID materialID;
	
	GraphicsBuffer instanceGraphicsBuffer;
	NativeArray<MetadataValue> metadata;

	public unsafe struct InstanceDataBuffer {
		public NativeArray<byte> raw;

		//public NativeSlice<float3x4> obj2world;
		//public NativeSlice<float3x4> world2obj;
		//public NativeSlice<float4> color;
		public float3x4* obj2world;
		public float3x4* world2obj;
		public float4* color;
	};
	InstanceDataBuffer instanceBuf;

	void Start () {
		//Debug.Log("CustomEntityRenderer.Start");
		inst = this;

		brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
		// Register meshes and Materials, in my use case these would be fixed and remain in VRAM
		// but CustomEntityRenderer might observe asset managers for potential reloads
		// we might want to use BatchMeshID directly inside the to be rendered entities
		meshID = brg.RegisterMesh(mesh);
		materialID = brg.RegisterMaterial(material);
	}
	void OnDisable () {
		if (brg != null) brg.Dispose();
	}
	

	public static float3x4 pack_matrix (float4x4 mat) {
		return new float3x4(
			mat.c0.xyz, mat.c1.xyz, mat.c2.xyz, mat.c3.xyz
		);
	}
	static int alignup (int byte_offset, int alignment) {
		return (byte_offset + alignment - 1) / alignment * alignment;
	}
	static int AddAligned (ref int cur_offset, int size, int alignment) {
		cur_offset = alignup(cur_offset, alignment);
		int offset = cur_offset;
		cur_offset += size;
		return offset;
	}

	int NumInstances;
	
	static readonly ProfilerMarker perfAlloc = new ProfilerMarker(ProfilerCategory.Ai, "CustomEntityRenderer.ReallocateInstances");
	static readonly ProfilerMarker perfUpload = new ProfilerMarker(ProfilerCategory.Ai, "CustomEntityRenderer.ReuploadInstanceGraphicsBuffer");
	static readonly ProfilerMarker perfCmd = new ProfilerMarker(ProfilerCategory.Ai, "CustomEntityRenderer.OnPerformCulling");

	public unsafe InstanceDataBuffer ReallocateInstances (int NumInstances) {
		perfAlloc.Begin();

		this.NumInstances = NumInstances;

		int total_size = 64; // 64 bytes of zeroes, so loads from address 0 return zeroes
		int offs0 = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_ObjectToWorld
		int offs1 = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_WorldToObject
		int offs2 = AddAligned(ref total_size, NumInstances * sizeof(float4  ), sizeof(float4  )); // _BaseColor

		var instanceBufRaw = new NativeArray<byte>(total_size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		var ptr = (byte*)instanceBufRaw.GetUnsafePtr();
		UnsafeUtility.MemSet(ptr, 0, 64);

		instanceBuf = new InstanceDataBuffer {
			raw = instanceBufRaw,
			obj2world = (float3x4*)(ptr + offs0),
			world2obj = (float3x4*)(ptr + offs1),
			color     = (float4  *)(ptr + offs2),

			// These should be safer to use, but I get weird errors trying to pass them to jobs
			//obj2world = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<float3x4>(ptr + offs0, sizeof(float3x4), NumInstances),
			//world2obj = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<float3x4>(ptr + offs1, sizeof(float3x4), NumInstances),
			//color     = NativeSliceUnsafeUtility.ConvertExistingDataToNativeSlice<float4  >(ptr + offs2, sizeof(float4  ), NumInstances),
		};
		
		// Set up metadata values to point to the instance data. Set the most significant bit 0x80000000 in each
		// which instructs the shader that the data is an array with one value per instance, indexed by the instance index.
		// Any metadata values that the shader uses and not set here will be zero. When such a value is used with
		// UNITY_ACCESS_DOTS_INSTANCED_PROP (i.e. without a default), the shader interprets the
		// 0x00000000 metadata value and loads from the start of the buffer. The start of the buffer which is
		// is a zero matrix so this sort of load is guaranteed to return zero, which is a reasonable default value.
		metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
		metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | (uint)offs0 };
		metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | (uint)offs1 };
		metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor")         , Value = 0x80000000 | (uint)offs2 };

		perfAlloc.End();
		return instanceBuf;
	}
	public void ReuploadInstanceGraphicsBuffer () {
		perfUpload.Begin();

		if (batchID != BatchID.Null)
			brg.RemoveBatch(batchID);

		if (NumInstances > 0) {
			instanceGraphicsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, instanceBuf.raw.Length/sizeof(int), sizeof(int));
			instanceGraphicsBuffer.SetData(instanceBuf.raw);
			instanceBuf.raw.Dispose();
			
			batchID = brg.AddBatch(metadata, instanceGraphicsBuffer.bufferHandle);
		}

		perfUpload.End();
	}

	void example_instances () {
		//void SetMatrix (int idx, float4x4 mat) {
		//	instanceBuf.obj2world[idx] = pack_matrix(mat);
		//	instanceBuf.world2obj[idx] = pack_matrix(inverse(mat));
		//}
		//
		//SetMatrix(0, float4x4.TRS(float3(-3, 2, 0), quaternion.RotateY(radians(45)), 1));
		//SetMatrix(1, float4x4.TRS(float3( 0, 2, 0), quaternion.RotateZ(radians(20)), 1.2f));
		//SetMatrix(2, float4x4.TRS(float3( 3, 2, 0), quaternion.RotateY(radians(20)), 1));
		//
		//instanceBuf.color[0] = new Vector4(1, 0, 0, 1);
		//instanceBuf.color[1] = new Vector4(0, 1, 0, 1);
		//instanceBuf.color[2] = new Vector4(0, 0, 1, 1);
	}

	public unsafe JobHandle OnPerformCulling (
			BatchRendererGroup rendererGroup,
			BatchCullingContext cullingContext,
			BatchCullingOutput cullingOutput,
			IntPtr userContext) {
		//Debug.Log("CustomEntityRenderer.OnPerformCulling");
		
		if (NumInstances <= 0)
			return new JobHandle();
		
		perfCmd.Begin();

		// UnsafeUtility.Malloc() requires an alignment, so use the largest integer type's alignment
		// which is a reasonable default.
		int alignment = UnsafeUtility.AlignOf<long>();

		// Acquire a pointer to the BatchCullingOutputDrawCommands struct so you can easily
		// modify it directly.
		var drawCommands = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

		// Allocate memory for the output arrays. In a more complicated implementation, you would calculate
		// the amount of memory to allocate dynamically based on what is visible.
		// This example assumes that all of the instances are visible and thus allocates
		// memory for each of them. The necessary allocations are as follows:
		// - a single draw command (which draws kNumInstances instances)
		// - a single draw range (which covers our single draw command)
		// - kNumInstances visible instance indices.
		// You must always allocate the arrays using Allocator.TempJob.
		drawCommands->drawCommands    = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>(), alignment, Allocator.TempJob);
		drawCommands->drawRanges      = (BatchDrawRange  *)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>()  , alignment, Allocator.TempJob);
		drawCommands->visibleInstances             = (int*)UnsafeUtility.Malloc(NumInstances * sizeof(int)              , alignment, Allocator.TempJob);
		drawCommands->drawCommandPickingInstanceIDs = null;

		drawCommands->drawCommandCount = 1;
		drawCommands->drawRangeCount = 1;
		drawCommands->visibleInstanceCount = NumInstances;

		// This example doens't use depth sorting, so it leaves instanceSortingPositions as null.
		drawCommands->instanceSortingPositions = null;
		drawCommands->instanceSortingPositionFloatCount = 0;

		// Configure the single draw command to draw kNumInstances instances
		// starting from offset 0 in the array, using the batch, material and mesh
		// IDs registered in the Start() method. It doesn't set any special flags.
		drawCommands->drawCommands[0].visibleOffset = 0;
		drawCommands->drawCommands[0].visibleCount = (uint)NumInstances;
		drawCommands->drawCommands[0].batchID = batchID;
		drawCommands->drawCommands[0].materialID = materialID;
		drawCommands->drawCommands[0].meshID = meshID;
		drawCommands->drawCommands[0].submeshIndex = 0;
		drawCommands->drawCommands[0].splitVisibilityMask = 0xff;
		drawCommands->drawCommands[0].flags = 0;
		drawCommands->drawCommands[0].sortingPosition = 0;

		// Configure the single draw range to cover the single draw command which
		// is at offset 0.
		drawCommands->drawRanges[0].drawCommandsBegin = 0;
		drawCommands->drawRanges[0].drawCommandsCount = 1;

		// This example doesn't care about shadows or motion vectors, so it leaves everything
		// at the default zero values, except the renderingLayerMask which it sets to all ones
		// so Unity renders the instances regardless of mask settings.
		drawCommands->drawRanges[0].filterSettings = new BatchFilterSettings {
			renderingLayerMask = 0xffffffff,
			receiveShadows = true,
			shadowCastingMode = ShadowCastingMode.On // Should I not get this from the material? How?
		};

		// Finally, write the actual visible instance indices to the array. In a more complicated
		// implementation, this output would depend on what is visible, but this example
		// assumes that everything is visible.
		for (int i = 0; i < NumInstances; ++i)
			drawCommands->visibleInstances[i] = i;

		// This simple example doesn't use jobs, so it returns an empty JobHandle.
		// Performance-sensitive applications are encouraged to use Burst jobs to implement
		// culling and draw command output. In this case, this function returns a
		// handle here that completes when the Burst jobs finish.

		perfCmd.End();
		return new JobHandle();
	}
}

[UpdateInGroup(typeof(PresentationSystemGroup))]
partial struct ComputeEntityInstancesSystem : ISystem {
	CustomEntityRenderer renderer => CustomEntityRenderer.inst;
	EntityQuery query;

	public void OnCreate (ref SystemState state) {
		//Debug.Log("ComputeEntityInstancesSystem.OnCreate");
		query = state.EntityManager.CreateEntityQuery(typeof(CustomRenderAsset), typeof(LocalTransform));
	}
	public void OnUpdate (ref SystemState state) {
		int NumInstances = query.CalculateEntityCount();

		//Debug.Log($"ComputeEntityInstancesSystem.OnUpdate NumInstances: {NumInstances}");

		var instanceBuf = renderer.ReallocateInstances(NumInstances);

		if (NumInstances > 0) {
			var job = new ComputeEntityInstancesJob{ instanceBuf = instanceBuf }
				.ScheduleParallel(query, state.Dependency);
			
			job.Complete();
		}

		renderer.ReuploadInstanceGraphicsBuffer();
	}

	[BurstCompile]
	unsafe partial struct ComputeEntityInstancesJob : IJobEntity {
		[NativeDisableUnsafePtrRestriction]
		public CustomEntityRenderer.InstanceDataBuffer instanceBuf;

		unsafe void Execute ([EntityIndexInQuery] int idx, in LocalTransform transform) {
			instanceBuf.obj2world[idx] = CustomEntityRenderer.pack_matrix(transform.ToMatrix());
			instanceBuf.world2obj[idx] = CustomEntityRenderer.pack_matrix(transform.ToInverseMatrix());
			instanceBuf.color[idx] = (idx & 2) == 0 ? float4(1,0,0,1) : float4(0,1,0,1);
		}
	}
}

public struct CustomRenderAsset : ISharedComponentData, IEquatable<CustomRenderAsset> {
	public UnityObjectRef<Mesh> Mesh;
	public UnityObjectRef<Material>[] Materials;

	public AABB RenderBoundsObj; // Do I need this? Or can my rendering simply access Mesh.Value.bounds.ToAABB()

	// Memoize the expensive 128-bit hash
	uint4 Hash128;

	public CustomRenderAsset (Mesh mesh, Material[] materials) {
		Mesh = mesh;

		Materials = new UnityObjectRef<Material>[materials.Length];
		for (int i = 0; i < materials.Length; i++)
			Materials[i] = materials[i];

		RenderBoundsObj = Mesh.Value.bounds.ToAABB();

		Hash128 = 0;
		Hash128 = ComputeHash128();
	}

	// All derived from RenderMeshArray
	uint4 ComputeHash128 () {
		var hash = new xxHash3.StreamingState(false);

		int numMaterials = Materials?.Length ?? 0;

		hash.Update(numMaterials);

		AssetHash.UpdateAsset(ref hash, ref Mesh);

		for (int i = 0; i < numMaterials; ++i)
			AssetHash.UpdateAsset(ref hash, ref Materials[i]);

		uint4 H = hash.DigestHash128();

		// Make sure the hash is never exactly zero, to keep zero as a null value
		if (math.all(H == 0))
			return new uint4(1, 0, 0, 0);

		return H;
	}

	public override int GetHashCode () => (int)Hash128.x;

	public bool Equals (CustomRenderAsset other) => math.all(Hash128 == other.Hash128);

	public override bool Equals (object obj) => obj is CustomRenderAsset other && Equals(other);

	public static bool operator == (CustomRenderAsset left, CustomRenderAsset right) => left.Equals(right);
	public static bool operator != (CustomRenderAsset left, CustomRenderAsset right) => !left.Equals(right);


	struct AssetHash {
		public static void UpdateAsset<T> (ref xxHash3.StreamingState hash, ref UnityObjectRef<T> asset) where T : UnityEngine.Object {
			// In the editor we can compute a stable serializable hash using an asset GUID
#if UNITY_EDITOR
			// bit dodgy, but asset.GetHashCode() == asset.instanceId, actual instanceId is internal so I can't use it
			bool success = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset.GetHashCode(), out string guid, out long localId);
			hash.Update(success);
			if (!success) {
				hash.Update(asset.GetHashCode());
				return;
			}
			var guidBytes = Encoding.UTF8.GetBytes(guid);

			hash.Update(guidBytes.Length);
			for (int j = 0; j < guidBytes.Length; ++j)
				hash.Update(guidBytes[j]);
			hash.Update(localId);
#else
			// In standalone, we have to resort to using the instance ID which is not serializable,
			// but should be usable in the context of this execution.
			hash.Update(asset.GetHashCode());
#endif
		}
	}
}
