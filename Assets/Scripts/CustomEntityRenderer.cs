using System;
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

using Unity.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Burst;
using Unity.Transforms;
using Unity.Profiling;
using Unity.Burst.Intrinsics;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CustomEntity {

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
			var sys = world?.GetExistingSystemManaged<RendererSystem>();
			if (sys != null) world.EntityManager.SetComponentData(sys.SystemHandle, new RendererSystem.Input {
				mesh = mesh,
				material = material,
			});
		}
	}

	// TODO: actually use, but can't be used like this since this is managed, should simply turn Mesh+Material into registered BRG IDs
	// which then go to Asset components, this works as long as meshes or materials are not added at runtime
	// tracking meshes per entity is not really needed in practice I think
	[System.Serializable]
	public struct Asset : ISharedComponentData, IEquatable<Asset> {
		public UnityObjectRef<Mesh> Mesh;
		//public UnityObjectRef<Material>[] Materials;
		public UnityObjectRef<Material> Material;

		public AABB RenderBoundsObj; // Do I need this? Or can my rendering simply access Mesh.Value.bounds.ToAABB()

		// Memoize the expensive 128-bit hash
		uint4 Hash128;

		public AABB CalcWorldBounds (in LocalTransform transform) {
			//return new AABB { Center = transform.Position, Extents = 1 }; // TODO: actually transform bounds
			return AABB.Transform(transform.ToMatrix(), RenderBoundsObj);
		}

		public Asset (Mesh mesh, Material[] materials) {
			Mesh = mesh;

			//Materials = new UnityObjectRef<Material>[materials.Length];
			//for (int i = 0; i < materials.Length; i++)
			//	Materials[i] = materials[i];
			Material = materials[0];

			RenderBoundsObj = Mesh.Value.bounds.ToAABB();

			Hash128 = 0;
			Hash128 = ComputeHash128();
		}

		// All derived from RenderMeshArray
		uint4 ComputeHash128 () {
			var hash = new xxHash3.StreamingState(false);

			//int numMaterials = Materials?.Length ?? 0;

			//hash.Update(numMaterials);

			AssetHash.UpdateAsset(ref hash, ref Mesh);

			//for (int i = 0; i < numMaterials; ++i)
			//	AssetHash.UpdateAsset(ref hash, ref Materials[i]);
			AssetHash.UpdateAsset(ref hash, ref Material);

			uint4 H = hash.DigestHash128();

			// Make sure the hash is never exactly zero, to keep zero as a null value
			if (math.all(H == 0))
				return new uint4(1, 0, 0, 0);

			return H;
		}

		public override int GetHashCode () => (int)Hash128.x;

		public bool Equals (Asset other) => math.all(Hash128 == other.Hash128);

		public override bool Equals (object obj) => obj is Asset other && Equals(other);

		public static bool operator == (Asset left, Asset right) => left.Equals(right);
		public static bool operator != (Asset left, Asset right) => !left.Equals(right);


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

	public struct InstanceData {
		
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float3x4 LocalTransform2float3x4 (in LocalTransform t)
		{
			float3x3 r = float3x3(t.Rotation);
			return float3x4(r.c0 * t.Scale,
			                r.c1 * t.Scale,
			                r.c2 * t.Scale,
			                t.Position);
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

		public unsafe struct BufferLayout {
			public int total_size;

			public int offs_obj2world;
			public int offs_world2obj;
			public int offs_color;

			public BufferLayout (int NumInstances) {
				total_size = 64; // 64 bytes of zeroes, so loads from address 0 return zeroes
				offs_obj2world = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_ObjectToWorld
				offs_world2obj = AddAligned(ref total_size, NumInstances * sizeof(float3x4), sizeof(float3x4)); // unity_WorldToObject
				offs_color     = AddAligned(ref total_size, NumInstances * sizeof(float4), sizeof(float4)); // _BaseColor

				total_size = alignup(total_size, 4); // GraphicsBuffer must be 4 byte aligned

				Debug.Log($"GPU BufferLayout: NumInstances: {NumInstances} -> total_size: {total_size / 1024/1204} MB");
			}
			
			public NativeArray<MetadataValue> GetMetadata () {
				var metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
				metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | (uint)offs_obj2world };
				metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | (uint)offs_world2obj };
				metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor"), Value = 0x80000000 | (uint)offs_color };
				return metadata;
			}
		}


		public struct Batch {
			public GraphicsBuffer buf;
			public SparseUploader upload;
			public BufferLayout layout;
			public BatchID batchID;

			public int InstanceCount;
			public int ChunkCount;

			ThreadedSparseUploader tsu;

			public struct ThreadedUpload {
				ThreadedSparseUploader tsu;
				BufferLayout layout;

				public ThreadedUpload (ThreadedSparseUploader tsu, BufferLayout layout) {
					this.tsu = tsu;
					this.layout = layout;
				}
				
				[MethodImpl(MethodImplOptions.AggressiveInlining)]
				public unsafe void WriteTransform (float3x4* obj2world, int baseIndex, int count) {
					tsu.AddMatrixUploadAndInverse(obj2world, count,
						layout.offs_obj2world + baseIndex*sizeof(float3x4),
						layout.offs_world2obj + baseIndex*sizeof(float3x4),
						ThreadedSparseUploader.MatrixType.MatrixType3x4, ThreadedSparseUploader.MatrixType.MatrixType3x4);
				}
				[MethodImpl(MethodImplOptions.AggressiveInlining)]
				public unsafe void WriteColor (float4* color, int baseIndex, int count) {
					tsu.AddUpload(color, count*sizeof(float4), layout.offs_color + baseIndex*sizeof(float4));
				}

			}

			public Batch (ref BatchRendererGroup brg, int instanceCount, int chunkCount) {
				layout = new BufferLayout(instanceCount);
				buf = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, layout.total_size/4, 4);
				upload = new SparseUploader(buf);

				InstanceCount = instanceCount;
				ChunkCount = chunkCount;

				batchID = brg.AddBatch(layout.GetMetadata(), buf.bufferHandle);
			}
			public void Dispose (ref BatchRendererGroup brg) {
				if (InstanceCount > 0) {
					brg.RemoveBatch(batchID);
					upload.Dispose();
					buf.Dispose();
					InstanceCount = 0;
				}
			}

			public unsafe ThreadedUpload BeginUpload () {
				//Debug.Log("BeginUpload");

				tsu = upload.Begin(
					maxDataSizeInBytes: (sizeof(float3x4) + sizeof(float4)) * InstanceCount,
					biggestDataUpload: sizeof(float3x4) * 128, // float3x4 * Max Instances per Chunk
					maxOperationCount: 2 * ChunkCount);
				return new ThreadedUpload(tsu, layout);
			}
			public void EndUpload () {
				//Debug.Log("EndUpload");

				upload.EndAndCommit(tsu);
				upload.FrameCleanup();
				tsu = default;
			}
		}

		
		#if false
		//HashSet<GpuMemoryBlock> blocks;
		//Dictionary<int, List<Block>> freelist;

		// Temp solution: One block per chunk
		Dictionary<ArchetypeChunk, GpuMemoryBlock> chunkBlocks;

		public InstanceData () {
			chunkBlocks = new Dictionary<ArchetypeChunk, GpuMemoryBlock>(32);
		}
		public void Dispose () {
			foreach (var b in chunkBlocks.Values) {
				b.Dispose();
			}
			chunkBlocks.Clear();
		}

		public void UpdateGpuAllocation (in EntityQuery query) {
			var chunks = query.ToArchetypeChunkArray(Allocator.Temp);

			foreach (var chunk in chunks) {
				if (!chunkBlocks.ContainsKey(chunk)) {
					var block = new GpuMemoryBlock(new BlockLayout(chunk.Capacity));
					chunkBlocks.Add(chunk, block);

					Debug.Log($"New Chunk");
				}
			}
			
			var deletedChunks = new NativeList<ArchetypeChunk>(0, Allocator.Temp);
			
			Debug.Log($"New Count: {chunkBlocks.Count}");

			// None of these are working for some reason
			foreach (var chunk in chunkBlocks.Keys) {
				if (chunk.Invalid()) {
					Debug.Log("Chunk Null");
					deletedChunks.Add(chunk);
				}
				if (chunk == ArchetypeChunk.Null) {
					Debug.Log("Chunk Null");
					deletedChunks.Add(chunk);
				}
				if (chunk.Count == 0) {
					Debug.Log("chunk.Count == 0");
					deletedChunks.Add(chunk);
				}
			}

			foreach (var chunk in deletedChunks) {
				chunkBlocks[chunk].Dispose();
				chunkBlocks.Remove(chunk);
			}

			deletedChunks.Dispose();
			chunks.Dispose();
		}
		#else
		public Batch batch;

		public void Dispose (ref BatchRendererGroup brg) {
			batch.Dispose(ref brg);
		}

		public void UpdateGpuAllocation (ref BatchRendererGroup brg, in EntityQuery query) {
			//Debug.Log("UpdateGpuAllocation");

			var instanceCount = query.CalculateEntityCountWithoutFiltering();

			if (instanceCount > batch.InstanceCount) {
				Debug.Log($"Resize InstanceData {batch.InstanceCount} -> {instanceCount}");

				batch.Dispose(ref brg);
				
				var chunkCount = query.CalculateEntityCountWithoutFiltering();
				batch = new Batch(ref brg, instanceCount, chunkCount);
			}
		}
		#endif
	}

	[UpdateInGroup(typeof(PresentationSystemGroup))]
	[UpdateBefore(typeof(UpdatePresentationSystemGroup))]
	//[BurstCompile]
	public unsafe partial class RendererSystem : SystemBase {

		public class Input : IComponentData {
			public Mesh mesh;
			public Material material;
		}

		BatchRendererGroup brg;
		BatchMeshID meshID;
		BatchMaterialID materialID;

		JobHandle ComputeInstanceDataJobHandle;
		bool NeedEndComputeInstanceData;

		InstanceData instanceData;

		EntityQuery query;

		ComponentTypeHandle<LocalTransform> c_transformsRO;
		ComponentTypeHandle<MyEntityData> c_dataRO;
		SharedComponentTypeHandle<Asset> c_Asset;
		ComponentTypeHandle<ChunkBounds> c_ChunkBounds;

		protected override void OnCreate () {
			Debug.Log("CustomEntityRendererSystem.OnCreate");

			EntityManager.AddComponent<Input>(SystemHandle);

			query = new EntityQueryBuilder(Allocator.Temp).WithAll<Asset, LocalTransform, SpatialGrid, MyEntityData>().Build(this);

			c_transformsRO = GetComponentTypeHandle<LocalTransform>(isReadOnly: true);
			c_dataRO = GetComponentTypeHandle<MyEntityData>(isReadOnly: true);
			c_Asset = GetSharedComponentTypeHandle<Asset>();
			c_ChunkBounds = GetComponentTypeHandle<ChunkBounds>(isReadOnly: true);

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

			instanceData = new InstanceData();
		}
		protected override void OnStopRunning () {
			Debug.Log("CustomEntityRendererSystem.OnStopRunning");

			// Probably no need to remove batchID from brg
			instanceData.Dispose(ref brg);
			brg.Dispose();
		}

		NativeArray<int> entityIndices;

		static readonly ProfilerMarker perfUpload = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.ReuploadInstanceGraphicsBuffer");
		static readonly ProfilerMarker perfCmd = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.OnPerformCulling");

		//[BurstCompile]
		protected override void OnUpdate () {
			//Debug.Log($"CustomEntityRendererSystem.OnUpdate");

			instanceData.UpdateGpuAllocation(ref brg, query);
			
			ComputeInstanceDataJobHandle = new JobHandle();
			NeedEndComputeInstanceData = false;

			if (query.IsEmpty)
				return;

			UploadInstanceData();
		}
		
		void UploadInstanceData () {
			var controller = SystemAPI.GetSingleton<ControllerECS>();

			entityIndices = query.CalculateBaseEntityIndexArrayAsync(World.UpdateAllocator.Handle, Dependency, out var entityIndexJob);

			c_transformsRO.Update(this);
			c_dataRO.Update(this);
			c_Asset.Update(this);

			var upload = instanceData.batch.BeginUpload();

			ComputeInstanceDataJobHandle = new ComputeInstanceDataJob {
				ChunkBaseEntityIndices = entityIndices,
				LocalTransforms = c_transformsRO,
				Data = c_dataRO,
				Controller = controller,
				Upload = upload,
				LastSystemVersion = LastSystemVersion,
			}.ScheduleParallel(query, entityIndexJob);
			
			ComputeInstanceDataJobHandle.Complete();
			instanceData.batch.EndUpload();
			NeedEndComputeInstanceData = false;

			// For some reason this is needed, is this right? We want to explictly only finish this job later in OnPerformCulling
			// I guess so that other jobs which might modify the relevant components for the instance data correctly wait (even though they should never be modified in practice or our rendering is broken)
			Dependency = ComputeInstanceDataJobHandle;
		}

		//[BurstCompile]
		unsafe JobHandle OnPerformCulling (
				BatchRendererGroup rendererGroup,
				BatchCullingContext cullingContext,
				BatchCullingOutput cullingOutput,
				IntPtr userContext) {
#if UNITY_EDITOR
			CustomEntityRenderer.inst.dbg.OnCulling(ref cullingContext);
#endif
			//Debug.Log("OnPerformCulling");

			//if (NeedEndComputeInstanceData) {
			//	ComputeInstanceDataJobHandle.Complete();
			//
			//	instanceData.batch.EndUpload();
			//	NeedEndComputeInstanceData = false;
			//}

			if (query.IsEmpty)
				return new JobHandle();

			c_transformsRO.Update(this);
			c_Asset.Update(this);
			c_ChunkBounds.Update(this);

			//Debug.Log($"CustomEntityRenderer.OnPerformCulling() NumInstances: {NumInstances}");

			perfCmd.Begin();

			////
			// Reuse Unity Culling Utils
			var cullingData = CullingSplits.Create(&cullingContext, QualitySettings.shadowProjection, World.UpdateAllocator.Handle);

			int NumChunks = query.CalculateChunkCount();
			var chunkVisibilty = new NativeList<ChunkVisiblity>(NumChunks, Allocator.TempJob);
			var drawCommands = new NativeArray<DrawCommand>(16, Allocator.TempJob, NativeArrayOptions.ClearMemory);

			var cullJob = new CullEntityInstancesJob {
				ChunkBaseEntityIndices = entityIndices,
				LocalTransformHandle = c_transformsRO,
				AssetHandle = c_Asset,
				ChunkBounds = c_ChunkBounds,
				chunkVisibilty = chunkVisibilty.AsParallelWriter(),
				drawCommands = drawCommands,
				CullingData = cullingData,
				CullingViewType = cullingContext.viewType,
			}.ScheduleParallel(query, ComputeInstanceDataJobHandle);

			var allocCmdsJob = new AllocDrawCommandsJob {
				meshID = meshID,
				materialID = materialID,
				batchID = instanceData.batch.batchID, // Instead set up BatchCullingOutputDrawCommands inside here and only modify?
				cullingOutputCommands = cullingOutput.drawCommands,
				drawCommands = drawCommands,
			}.Schedule(cullJob);

			var writeInstancesJob = new WriteDrawInstanceIndicesJob {
				chunkVisibilty = chunkVisibilty.AsDeferredJobArray(),
				drawCommands = drawCommands,
				cullingOutputCommands = cullingOutput.drawCommands,
			}.Schedule(chunkVisibilty, 8, allocCmdsJob);

			chunkVisibilty.Dispose(writeInstancesJob);
			drawCommands.Dispose(writeInstancesJob);

			perfCmd.End();
			return writeInstancesJob;
		}
	}

	// Could be optimized by only uploading if not culled, but since culling happens seperately for camera and shadows, this is non-trivial
	// Possibly flag visible chunks and lather upload data if gpu instance data is not needed for culling/lod
	// Actually this makes a lot of sense, since some the the data needed on gpu depends on LOD, like having only LOD0 be animated for example
	// Might also be able to lazily upload during cull
	[BurstCompile]
	unsafe partial struct ComputeInstanceDataJob : IJobChunk {
		[ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
		[ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransforms;
		[ReadOnly] public ComponentTypeHandle<MyEntityData> Data;
		[ReadOnly] public ControllerECS Controller;

		public InstanceData.Batch.ThreadedUpload Upload;

		//public bool BaseEntityIndicesChanged;
		public uint LastSystemVersion;

		[BurstCompile]
		public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {

			if (  //!chunk.DidOrderChange(LastSystemVersion) &&
			      !chunk.DidChange(ref LocalTransforms, LastSystemVersion) &&
			      !chunk.DidChange(ref Data, LastSystemVersion)) {
				return;
			}

			NativeArray<LocalTransform> transforms = chunk.GetNativeArray(ref LocalTransforms);
			NativeArray<MyEntityData> spinningData = chunk.GetNativeArray(ref Data);

			int BaseInstanceIdx = ChunkBaseEntityIndices[unfilteredChunkIndex];

			var obj2world = stackalloc float3x4[chunk.Count];
			var color = stackalloc float4[chunk.Count];

			for (int i = 0; i < chunk.Count; i++) {
				//int idx = BaseInstanceIdx + i;
				var transform = transforms[i];

				var col = Controller.DebugSpatialGrid ?
					MyEntityData.RandColor(UpdateSpatialGridSystem.CalcGridCell(Controller, transform.Position)) :
					spinningData[i].Color;
				
				obj2world[i] = InstanceData.LocalTransform2float3x4(transform);
				color[i] = float4(col.r, col.g, col.b, col.a); // Could avoid uploading color if it did not actually change
			}
			
			Upload.WriteTransform(obj2world, BaseInstanceIdx, chunk.Count);
			Upload.WriteColor(color, BaseInstanceIdx, chunk.Count);
		}
	}

}
