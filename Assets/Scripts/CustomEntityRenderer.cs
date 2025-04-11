using System;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using float3x4 = Unity.Mathematics.float3x4;
using float4x4 = Unity.Mathematics.float4x4;
using quaternion = Unity.Mathematics.quaternion;
using CustomRendering;

using Unity.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Transforms;
using Unity.Profiling;
using Unity.Burst.Intrinsics;
using System.Threading;

public class CustomEntityRenderer : MonoBehaviour {
	public Mesh mesh;
	public Material material;

#if UNITY_EDITOR
	public static CustomEntityRenderer inst;
	public VisCulling dbg = new();

	void LateUpdate () {
		dbg.Draw();
	}
	void OnDisable () {
		dbg.DisposeData();
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
		var sys = world?.GetExistingSystemManaged<CustomEntityRendererSystem>();
		if (sys != null) world.EntityManager.SetComponentData(sys.SystemHandle, new CustomEntityRendererSystem.Input {
			mesh = mesh,
			material = material,
		});
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

public struct CustomEntitySpatialGrid : ISharedComponentData {
	public int3 Key;
}
public struct CustomEntityChunkBounds : IComponentData {
	public AABB bounds;
}

namespace CustomRendering {

	// Seems to work fine most of the time to use a single GraphicsBuffer (Dispose old one, allocate new one and upload)
	// But sometimes rarely for some reason unity gives a warning about GraphicsBuffer still being in flight
	class UploadBuffer {
		GraphicsBuffer[] bufs;
		int next;

		public UploadBuffer (int frames) {
			bufs = new GraphicsBuffer[frames];
			next = 0;
		}
		public void Dispose () {
			for (int i=0; i<bufs.Length; i++) {
				if (bufs[i] != null) {
					bufs[i].Dispose();
					bufs[i] = null;
				}
			}
		}

		public GraphicsBuffer GetNext (GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usage, int count, int stride) {
			ref var buf = ref bufs[next++];
			next = next % bufs.Length;

			//if (buf != null)
			//	buf.Dispose();
			//buf = new GraphicsBuffer(target, usage, count, stride);

			// This avoids visible stalls (waiting for render thread) in Vulkan, not tested if fps gain in d3d
			// But probably indicates that I need to use different method to upload data as I can't completely avoid ever allocating new GraphicsBuffer
			if (buf == null || buf.count != count)
				buf = new GraphicsBuffer(target, usage, count, stride);
			return buf;
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

		BatchRendererGroup brg;
		BatchMeshID meshID;
		BatchMaterialID materialID;

		UploadBuffer instanceGraphicsBuffers;
		GraphicsBuffer curInstanceGraphicsBuffer;

		NativeArray<MetadataValue> metadata;
		BatchID batchID;

		JobHandle ComputeInstanceDataJobHandle;

		public unsafe struct InstanceDataBuffer {
			// Pointers into instanceGraphicsBuffer which is locked for writing
			public float3x4* obj2world;
			public float3x4* world2obj;
			public float4* color;
		
			public void from_transform (int idx, in LocalTransform transform, in Color col) {
				obj2world[idx] = pack_matrix(transform.ToMatrix());
				world2obj[idx] = pack_matrix(transform.ToInverseMatrix());
				color[idx] = float4(col.r, col.g, col.b, col.a);//(idx & 2) == 0 ? float4(1,0,0,1) : float4(0,1,0,1);
			}
		};
		InstanceDataBuffer instanceData;

		ComponentTypeHandle<LocalTransform> c_transformsRO;
		ComponentTypeHandle<SpinningSystem.Data> c_spinningDataRO;

		protected override void OnCreate () {
			Debug.Log("CustomEntityRendererSystem.OnCreate");
		
			EntityManager.AddComponent<Input>(SystemHandle);

			c_transformsRO = GetComponentTypeHandle<LocalTransform>(true);
			c_spinningDataRO = GetComponentTypeHandle<SpinningSystem.Data>(true);

			RequireForUpdate<ControllerECS>();
		}
		protected override void OnStartRunning () {
			Debug.Log("CustomEntityRendererSystem.OnStartRunning");

			var input = SystemAPI.ManagedAPI.GetSingleton<Input>();

			// TODO: comment how BatchRendererGroup ends up getting called by the engine, ie how dows unity know to 'call' my system
			// Probably new BatchRendererGroup registers itself with the engine
			brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);
			// Register meshes and Materials, in my use case these would be fixed and remain in VRAM
			// but CustomEntityRenderer might observe asset managers for potential reloads
			// we might want to use BatchMeshID directly inside the to be rendered entities
			meshID = brg.RegisterMesh(input.mesh);
			materialID = brg.RegisterMaterial(input.material);

			instanceGraphicsBuffers = new UploadBuffer(3);
			curInstanceGraphicsBuffer = null;

			batchID = BatchID.Null;
		}
		protected override void OnStopRunning () {
			Debug.Log("CustomEntityRendererSystem.OnStopRunning");

			// Probably no need to remove batchID from brg
			DisposeOf(ref brg);
			DisposeOf(ref metadata);
			instanceGraphicsBuffers.Dispose();
			curInstanceGraphicsBuffer = null;
		}
	
		static void DisposeOf<T> (ref T obj) where T: IDisposable {
			if (obj != null) {
				obj.Dispose();
				obj = default(T);
			}
		}
		static void DisposeOf<T> (ref NativeArray<T> obj) where T: struct {
			if (obj.IsCreated) {
				obj.Dispose();
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
		NativeArray<int> entityIndices;

		EntityQuery query;

	
		static readonly ProfilerMarker perfAlloc  = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.ReallocateInstances");
		static readonly ProfilerMarker perfUpload = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.ReuploadInstanceGraphicsBuffer");
		static readonly ProfilerMarker perfCmd    = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.OnPerformCulling");
	
		public unsafe void ReallocateInstances () {
			perfAlloc.Begin();
		
			int total_size = 64; // 64 bytes of zeroes, so loads from address 0 return zeroes
			int offs0 = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_ObjectToWorld
			int offs1 = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_WorldToObject
			int offs2 = AddAligned(ref total_size, NumInstances * sizeof(float4), sizeof(float4)); // _BaseColor

			//instanceGraphicsBuffer = new GraphicsBuffer(
			//	GraphicsBuffer.Target.Raw,
			//	GraphicsBuffer.UsageFlags.LockBufferForWrite,
			//	total_size / sizeof(int), sizeof(int));
			curInstanceGraphicsBuffer = instanceGraphicsBuffers.GetNext(
				GraphicsBuffer.Target.Raw,
				GraphicsBuffer.UsageFlags.LockBufferForWrite,
				total_size / sizeof(int), sizeof(int));

			NativeArray<int> write_buf = curInstanceGraphicsBuffer.LockBufferForWrite<int>(0, curInstanceGraphicsBuffer.count);
			var ptr = (byte*)write_buf.GetUnsafePtr();
			UnsafeUtility.MemSet(ptr, 0, 64);

			instanceData = new InstanceDataBuffer {
				obj2world = (float3x4*)(ptr + offs0),
				world2obj = (float3x4*)(ptr + offs1),
				color = (float4*)(ptr + offs2),
			};

			metadata = new NativeArray<MetadataValue>(3, Allocator.TempJob);
			metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | (uint)offs0 };
			metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | (uint)offs1 };
			metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor"         ), Value = 0x80000000 | (uint)offs2 };

			perfAlloc.End();
		}

		public void ReuploadInstanceGraphicsBuffer () {
			perfUpload.Begin();

			curInstanceGraphicsBuffer.UnlockBufferAfterWrite<int>(curInstanceGraphicsBuffer.count);
		
			batchID = brg.AddBatch(metadata, curInstanceGraphicsBuffer.bufferHandle);
		
			curInstanceGraphicsBuffer = null; // Mark as used

			perfUpload.End();
		}
	
		[BurstCompile]
		protected override void OnUpdate () {
			//Debug.Log($"CustomEntityRendererSystem.OnUpdate {NumInstances}");
		
			Remove(ref batchID);
			DisposeOf(ref metadata);

			query = GetEntityQuery(typeof(CustomRenderAsset), typeof(LocalTransform));
			if (query.IsEmpty)
				return;
		
			var controller = SystemAPI.GetSingleton<ControllerECS>();

			NumInstances = query.CalculateEntityCount();
			entityIndices = query.CalculateBaseEntityIndexArrayAsync(World.UpdateAllocator.Handle, Dependency, out var entityIndexJob);
			
			c_transformsRO.Update(this);
			c_spinningDataRO.Update(this);

			ReallocateInstances();
			
			ComputeInstanceDataJobHandle.Complete();

			ComputeInstanceDataJobHandle = new ComputeInstanceDataJob{
				ChunkBaseEntityIndices = entityIndices,
				LocalTransforms = c_transformsRO,
				SpinningData = c_spinningDataRO,
				Controller = controller,
				InstanceData = instanceData
			}.ScheduleParallel(query, entityIndexJob);

			Dependency = ComputeInstanceDataJobHandle; // For some reason this is needed, is this right? We want to explictly only finish this job later in OnPerformCulling
		}
	
		[BurstCompile]
		unsafe JobHandle OnPerformCulling (
				BatchRendererGroup rendererGroup,
				BatchCullingContext cullingContext,
				BatchCullingOutput cullingOutput,
				IntPtr userContext) {
		#if UNITY_EDITOR
			CustomEntityRenderer.inst.dbg.OnCulling(ref cullingContext);
		#endif
		
			if (query.IsEmpty)
				return new JobHandle();
			
			c_transformsRO.Update(this);
			//c_spinningData.Update(this);

			if (curInstanceGraphicsBuffer != null) {
				ComputeInstanceDataJobHandle.Complete();
				//ComputeInstanceDataJobHandle = new JobHandle();

				ReuploadInstanceGraphicsBuffer();
			}
		
			//Debug.Log($"CustomEntityRenderer.OnPerformCulling() NumInstances: {NumInstances}");

			perfCmd.Begin();

		////
			// Reuse Unity Culling Utils
			var cullingData = CullingSplits.Create(&cullingContext, QualitySettings.shadowProjection, World.UpdateAllocator.Handle);
		
			int NumChunks = query.CalculateChunkCount();
			var chunkVisibilty = new NativeList<ChunkVisiblity>(NumChunks, Allocator.TempJob);
			var drawCommands  = new NativeArray<DrawCommand>(16, Allocator.TempJob, NativeArrayOptions.ClearMemory);

			var cullJob = new CullEntityInstancesJob {
				ChunkBaseEntityIndices = entityIndices,
				LocalTransformHandle = c_transformsRO,
				chunkVisibilty = chunkVisibilty.AsParallelWriter(),
				drawCommands = drawCommands,
				CullingData = cullingData,
				CullingViewType = cullingContext.viewType,
			}.ScheduleParallel(query, ComputeInstanceDataJobHandle);

			var allocCmdsJob = new AllocDrawCommandsJob {
				meshID = meshID, materialID = materialID, batchID = batchID, // Instead set up BatchCullingOutputDrawCommands inside here and only modify?
				cullingOutputCommands = cullingOutput.drawCommands,
				drawCommands = drawCommands,
			}.Schedule(cullJob);
		
			var writeInstancesJob = new WriteDrawInstanceIndicesJob {
				chunkVisibilty = chunkVisibilty.AsDeferredJobArray(),
				drawCommands = drawCommands,
				cullingOutputCommands = cullingOutput.drawCommands,
			}.Schedule(chunkVisibilty, 8, allocCmdsJob);

		#if false
			// Complete for testing
			writeInstancesJob.Complete();
		
			Debug.Log($"-------------");
			for (int splitMask=0; splitMask<16; ++splitMask) {
				if (drawCommands[splitMask].visibleInstances > 0)
					Debug.Log($"Cmd {Convert.ToString(splitMask, 2)}: Visible Instances: {drawCommands[splitMask].visibleInstances}");
			}
		
			chunkVisibilty.Dispose(writeInstancesJob);
			drawCommands.Dispose(writeInstancesJob);

			perfCmd.End();
			return new JobHandle();
		#else
			chunkVisibilty.Dispose(writeInstancesJob);
			drawCommands.Dispose(writeInstancesJob);

			perfCmd.End();
			return writeInstancesJob;
		#endif
		}
	}

	// Could be optimized by only uploading if not culled, but since culling happens seperately for camera and shadows, this is non-trivial
	// Possibly flag visible chunks and lather upload data if gpu instance data is not needed for culling/lod
	// Actually this makes a lot of sense, since some the the data needed on gpu depends on LOD, like having only LOD0 be animated for example
	[BurstCompile]
	unsafe partial struct ComputeInstanceDataJob : IJobChunk {
		[ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
		[ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransforms;
		[ReadOnly] public ComponentTypeHandle<SpinningSystem.Data> SpinningData;
		[ReadOnly] public ControllerECS Controller;

		[NativeDisableUnsafePtrRestriction]
		public CustomEntityRendererSystem.InstanceDataBuffer InstanceData;

		[BurstCompile]
		public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
			
			NativeArray<LocalTransform> transforms = chunk.GetNativeArray(ref LocalTransforms);
			NativeArray<SpinningSystem.Data> spinningData = chunk.GetNativeArray(ref SpinningData);

			int BaseInstanceIdx = ChunkBaseEntityIndices[unfilteredChunkIndex];

			for (int i=0; i<chunk.Count; i++) {
				int idx = BaseInstanceIdx + i;
				var transform = transforms[i];

				var col = Controller.DebugSpatialGrid ?
					SpawnerSystem.RandColor(UpdateSpatialGridSystem.CalcGridCell(Controller, transform.Position)) :
					spinningData[i].Color;

				InstanceData.from_transform(idx, transform, col);
			}
		}
	}

	//[UpdateInGroup(typeof(PresentationSystemGroup))]
	//[UpdateBefore(typeof(CustomEntityRendererSystem))]
	[UpdateInGroup(typeof(SimulationSystemGroup))]
	[UpdateAfter(typeof(SpinningSystem))]
	[BurstCompile]
	public partial struct UpdateSpatialGridSystem : ISystem {
	
		EntityQuery query;

		EntityTypeHandle c_entities;
		ComponentTypeHandle<LocalTransform> c_transformsRO;
		SharedComponentTypeHandle<CustomEntitySpatialGrid> c_spatialGridRO;

		[BurstCompile]
		public void OnCreate (ref SystemState state) {
			//query = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<CustomRenderAsset, LocalTransform, CustomEntitySpatialGrid>());
			query = state.GetEntityQuery(new EntityQueryBuilder(Allocator.Temp).WithAll<CustomRenderAsset, LocalTransform, CustomEntitySpatialGrid>());

			c_entities = state.GetEntityTypeHandle();
			c_transformsRO = state.GetComponentTypeHandle<LocalTransform>(true);
			c_spatialGridRO = state.GetSharedComponentTypeHandle<CustomEntitySpatialGrid>();

			state.RequireForUpdate<ControllerECS>();
		}

		[BurstCompile]
		public void OnUpdate (ref SystemState state) {
			var controller = SystemAPI.GetSingleton<ControllerECS>();

			c_entities.Update(ref state);
			c_transformsRO.Update(ref state);
			c_spatialGridRO.Update(ref state);
		
			EntityCommandBuffer ecb;
			if (controller.DebugSpatialGrid) ecb = new EntityCommandBuffer(Allocator.TempJob);
			else                             ecb = SystemAPI.GetSingleton<BeginPresentationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

			var debugCount = new NativeArray<int>(1, Allocator.TempJob);

			var SpatialJob = new UpdateSpatialGridJob{
				Entities = c_entities,
				LocalTransforms = c_transformsRO,
				SpatialGrid = c_spatialGridRO,
				controller = controller,
				Ecb = ecb.AsParallelWriter(),
				DebugCount = debugCount,
			}.ScheduleParallel(query, state.Dependency);

			state.Dependency = SpatialJob;

			if (controller.DebugSpatialGrid) {
				SpatialJob.Complete();
				ecb.Playback(state.EntityManager);
				ecb.Dispose();

				state.EntityManager.GetAllUniqueSharedComponents<CustomEntitySpatialGrid>(out var grid, Allocator.Temp);
				foreach (var cell in grid) {
					DebugGridCell(controller, cell);
				}

				Debug.Log($"UpdateSpatialGridSystem: Moved Entities: {debugCount[0]}");
			}

			debugCount.Dispose();
		}

		void DebugGridCell (in ControllerECS controller, in CustomEntitySpatialGrid cell) {
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

			Color col = SpawnerSystem.RandColor(cell.Key);

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
			[ReadOnly] public SharedComponentTypeHandle<CustomEntitySpatialGrid> SpatialGrid;
			[ReadOnly] public ControllerECS controller;

			public NativeArray<int> DebugCount;
			public EntityCommandBuffer.ParallelWriter Ecb;

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
						Ecb.SetSharedComponent(unfilteredChunkIndex, entities[i], new CustomEntitySpatialGrid { Key = spatialKey });

						Interlocked.Add(ref ((int*)DebugCount.GetUnsafePtr())[0], 1);
					}
				}
			}
		}
	}
}
