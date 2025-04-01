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
using Unity.Burst.Intrinsics;
using Unity.Assertions;
using FrustumPlanes = Unity.Rendering.FrustumPlanes;

public class CustomEntityRenderer : MonoBehaviour {
	public Mesh mesh;
	public Material material;

#if UNITY_EDITOR
	public static CustomEntityRenderer inst;
	public CustomRendering.VisCulling dbg = new();

	void LateUpdate () {
		dbg.Draw();
	}
#endif

	void OnValidate () {
		refresh();
	}

	void Start () {
		refresh();
	}

	void refresh () {
#if UNITY_EDITOR
		inst = this;
#endif

		var world = World.DefaultGameObjectInjectionWorld;
		var sys = world?.GetExistingSystem<CustomEntityRendererSystem>();
		if (sys.HasValue && sys != SystemHandle.Null) {
			world.EntityManager.SetComponentData(sys.Value, new CustomEntityRendererSystem.Input {
				mesh = mesh,
				material = material,
			});
		}
	}
}
[UpdateInGroup(typeof(PresentationSystemGroup))]
[UpdateBefore(typeof(UpdatePresentationSystemGroup))]
[BurstCompile]
public unsafe partial class CustomEntityRendererSystem : SystemBase {
	
	public class Input : IComponentData {
		public Mesh mesh;
		public Material material;
	}

	BatchRendererGroup brg = null;
	BatchMeshID meshID = BatchMeshID.Null;
	BatchMaterialID materialID = BatchMaterialID.Null;

	GraphicsBuffer instanceGraphicsBuffer = null;
	NativeArray<MetadataValue> metadata;
	BatchID batchID = BatchID.Null;
	
	EntityQuery query; // all renderable entities

	public unsafe struct InstanceDataBuffer {
		// Pointers into instanceGraphicsBuffer which is locked for writing
		public float3x4* obj2world;
		public float3x4* world2obj;
		public float4* color;
	};
	public InstanceDataBuffer instanceData { get; private set; }
	
	protected override void OnCreate () {
		Debug.Log("CustomEntityRendererSystem.OnCreate");

		World.EntityManager.AddComponent<Input>(SystemHandle);
	}
	protected override void OnStartRunning () {
		Debug.Log("CustomEntityRendererSystem.OnStartRunning");

		var input = SystemAPI.ManagedAPI.GetComponent<Input>(SystemHandle);

		brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
		// Register meshes and Materials, in my use case these would be fixed and remain in VRAM
		// but CustomEntityRenderer might observe asset managers for potential reloads
		// we might want to use BatchMeshID directly inside the to be rendered entities
		meshID = brg.RegisterMesh(input.mesh);
		materialID = brg.RegisterMaterial(input.material);

		query = new EntityQueryBuilder(Allocator.Temp).WithAll<CustomRenderAsset, LocalTransform>().Build(this);
	}
	protected override void OnStopRunning () {
		Debug.Log("CustomEntityRendererSystem.OnStopRunning");

		// Probably no need to remove batchID from brg
		DisposeOf(ref brg);
		DisposeOf(ref instanceGraphicsBuffer);
	}
	
	static void DisposeOf<T> (ref T obj) where T: IDisposable {
		if (obj != null) {
			obj.Dispose();
			obj = default(T);
		}
	}
	void Remove (ref BatchID obj) {
		if (obj != null) {
			brg.RemoveBatch(obj);
			obj = BatchID.Null;
		}
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
	JobHandle cullJobDep;
	NativeArray<int> entityIndices;

	
	static readonly ProfilerMarker perfAlloc  = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.ReallocateInstances");
	static readonly ProfilerMarker perfUpload = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.ReuploadInstanceGraphicsBuffer");
	static readonly ProfilerMarker perfCmd    = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.OnPerformCulling");
	
	public unsafe void ReallocateInstances () {
		perfAlloc.Begin();
		
		//Debug.Log($"CustomEntityRenderer.ReallocateInstances() NumInstances: {NumInstances}");

		DisposeOf(ref instanceGraphicsBuffer); // TODO: Only if size changed?

		instanceData = default;

		if (NumInstances > 0) {

			int total_size = 64; // 64 bytes of zeroes, so loads from address 0 return zeroes
			int offs0 = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_ObjectToWorld
			int offs1 = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_WorldToObject
			int offs2 = AddAligned(ref total_size, NumInstances * sizeof(float4), sizeof(float4)); // _BaseColor

			instanceGraphicsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Raw,
				GraphicsBuffer.UsageFlags.LockBufferForWrite,
				total_size / sizeof(int), sizeof(int));

			NativeArray<int> write_buf = instanceGraphicsBuffer.LockBufferForWrite<int>(0, instanceGraphicsBuffer.count);
			var ptr = (byte*)write_buf.GetUnsafePtr();
			UnsafeUtility.MemSet(ptr, 0, 64);

			instanceData = new InstanceDataBuffer {
				obj2world = (float3x4*)(ptr + offs0),
				world2obj = (float3x4*)(ptr + offs1),
				color = (float4*)(ptr + offs2),
			};

			metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
			metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | (uint)offs0 };
			metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | (uint)offs1 };
			metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor"         ), Value = 0x80000000 | (uint)offs2 };
		}

		perfAlloc.End();
	}

	public void ReuploadInstanceGraphicsBuffer () {
		perfUpload.Begin();

		Remove(ref batchID);

		if (NumInstances > 0) {
			instanceGraphicsBuffer.UnlockBufferAfterWrite<int>(instanceGraphicsBuffer.count);
		
			batchID = brg.AddBatch(metadata, instanceGraphicsBuffer.bufferHandle);
		}

		perfUpload.End();
	}
	
	protected override void OnUpdate () {
		NumInstances = query.CalculateEntityCount();

		//Debug.Log($"CustomEntityRendererSystem.OnUpdate {NumInstances}");
		
		ReallocateInstances();

		if (NumInstances > 0) {
			var instDataJob = new ComputeInstanceDataJob{ instanceData = instanceData }
				.ScheduleParallel(query, Dependency);

			entityIndices = query.CalculateBaseEntityIndexArrayAsync(World.UpdateAllocator.Handle, Dependency, out cullJobDep);

			instDataJob.Complete(); // Have to complete for ReuploadInstanceGraphicsBuffer!
		}

		ReuploadInstanceGraphicsBuffer();
		// Dispose of GraphicsBuffer?
	}
	
	[BurstCompile]
	unsafe JobHandle OnPerformCulling (
			BatchRendererGroup rendererGroup,
			BatchCullingContext cullingContext,
			BatchCullingOutput cullingOutput,
			IntPtr userContext) {
		//Debug.Log($"CustomEntityRenderer.OnPerformCulling {cullingContext.viewType}");
		
	#if UNITY_EDITOR
		CustomEntityRenderer.inst.dbg.OnCulling(ref cullingContext);
	#endif

		if (NumInstances <= 0)
			return new JobHandle();
		
		//Debug.Log($"CustomEntityRenderer.OnPerformCulling() NumInstances: {NumInstances}");

		perfCmd.Begin();

		var cmds_out = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();
		// These are supposedly auto-freed by the batch renderer
		var cmds   = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>(), UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
		var ranges = (BatchDrawRange  *)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>()  , UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
		
		var visibleInstances = new UnsafeList<int>(NumInstances, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		
		// Reuse Unity Culling Utils
		var cullingData = CustomRendering.CullingSplits.Create(&cullingContext, QualitySettings.shadowProjection, World.UpdateAllocator.Handle);
		
		var cullJob = new CullEntityInstancesJob {
			ChunkBaseEntityIndices = entityIndices,
			LocalTransformHandle = this.GetComponentTypeHandle<LocalTransform>(true),
			visibleInstances = visibleInstances.AsParallelWriter(),
			CullingData = cullingData,
			CullingViewType = cullingContext.viewType,
		}.ScheduleParallel(query, cullJobDep);

		// Need to somehow have visibleInstances.Length be finalized before outputting BatchCullingOutputDrawCommands
		// TODO: How does Entities.Graphics solve this? Maybe we simply use a reference to BatchCullingOutputDrawCommands and update this from jobs?
		cullJob.Complete();

		cmds_out[0] = new BatchCullingOutputDrawCommands {
			drawCommands     = cmds,
			drawRanges       = ranges,
			visibleInstances = visibleInstances.Ptr, // Does this cause UnsafeList to be freed correctly?
			drawCommandPickingInstanceIDs = null,

			drawCommandCount = 1,
			drawRangeCount = 1,
			visibleInstanceCount = NumInstances,

			instanceSortingPositions = null,
			instanceSortingPositionFloatCount = 0,
		};

		cmds[0] = new BatchDrawCommand {
			visibleOffset = 0,
			visibleCount = (uint)visibleInstances.Length,
			batchID = batchID,
			materialID = materialID,
			meshID = meshID,
			submeshIndex = 0,
			splitVisibilityMask = 0xff, // TODO: Currently rendering in all shadow cascades!
			flags = 0,
			sortingPosition = 0,
		};

		ranges[0] = new BatchDrawRange {
			drawCommandsType = BatchDrawCommandType.Direct,
			drawCommandsBegin = 0,
			drawCommandsCount = 1,
			filterSettings = new BatchFilterSettings {
				renderingLayerMask = 0xffffffff,
				receiveShadows = true,
				shadowCastingMode = ShadowCastingMode.On // Should I not get this from the material? How?
			},
		};

		perfCmd.End();
		//return cullJob;
		return new JobHandle();
	}
	
	[BurstCompile]
	unsafe partial struct ComputeInstanceDataJob : IJobEntity {
		[NativeDisableUnsafePtrRestriction]
		public InstanceDataBuffer instanceData;
		
		[BurstCompile]
		unsafe void Execute ([EntityIndexInQuery] int idx, in LocalTransform transform) {
			instanceData.obj2world[idx] = pack_matrix(transform.ToMatrix());
			instanceData.world2obj[idx] = pack_matrix(transform.ToInverseMatrix());
			instanceData.color[idx] = (idx & 2) == 0 ? float4(1,0,0,1) : float4(0,1,0,1);
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
