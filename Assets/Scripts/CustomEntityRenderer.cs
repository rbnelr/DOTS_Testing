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

	[BurstCompile]
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
		
		
		public const int InstancesPerChunk = 128;
		public const int ChunksPerBatch = 128;
		public const int InstancesPerBatch = ChunksPerBatch * InstancesPerChunk;

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

				Debug.Log($"GPU BufferLayout: NumInstances: {NumInstances} -> total_size: {total_size} B");
			}
			
			public NativeArray<MetadataValue> GetMetadata () {
				var metadata = new NativeArray<MetadataValue>(3, Allocator.Temp);
				metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000 | (uint)offs_obj2world };
				metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000 | (uint)offs_world2obj };
				metadata[2] = new MetadataValue { NameID = Shader.PropertyToID("_BaseColor"), Value = 0x80000000 | (uint)offs_color };
				return metadata;
			}
		}

		// A batch sounds like a group of instances to be drawn, ie a drawcall (BatchDrawCommand)
		// but for BatchRendererGroup it seems more like a packed set of instance data,
		// which can still have separate meshes/materials and thus require more actual draws
		public class Batch {
			GraphicsBuffer buf;
			public SparseUploader upload;
			public BatchID batchID;
			
			public int UsedChunks;
			public NativeList<int> freelist;
			public bool Full => UsedChunks >= ChunksPerBatch;

			public Batch (ref BatchRendererGroup brg, in BufferLayout layout) {
				buf = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, layout.total_size/4, 4);
				upload = new SparseUploader(buf);

				batchID = brg.AddBatch(layout.GetMetadata(), buf.bufferHandle);

				UsedChunks = 0;
				freelist = new(ChunksPerBatch, Allocator.Persistent);

				Debug.Log($"new Batch {layout.total_size} B");
			}
			public void Dispose (ref BatchRendererGroup brg) {
				brg.RemoveBatch(batchID);
				upload.Dispose();
				buf.Dispose();
				freelist.Dispose();
			}

			// Allocate chunk slot in arbirary slot in fixed size batch memory
			public BatchChunk AllocChunk () {
				Debug.Assert(!Full);

				var res = new BatchChunk { batchID = batchID };
				// Allocate at back if no free slots
				if (freelist.IsEmpty) {
					res.Slot = UsedChunks++;
					return res;
				}

				// Allocate random empty slot
				res.Slot = freelist[0];
				freelist.RemoveAtSwapBack(0);

				UsedChunks++;
				return res;
			}
			// Free up chunk slot
			public void FreeChunk (int slot) {
				Debug.Assert(UsedChunks > 0);
				UsedChunks--;
				freelist.AddNoResize(slot);
			}
		}
		public struct BatchChunk {
			public BatchID batchID;
			public int Slot;
			public int InstanceOffset => Slot * InstancesPerChunk;
		}

		public BufferLayout layout;

		public Dictionary<BatchID, Batch> batches;
		public NativeHashMap<ArchetypeChunk, BatchChunk> ChunkBatches; // TODO: Any hashmap values like this could be turned into a chunk component!
		
		public InstanceData (ref BatchRendererGroup brg) {
			layout = new BufferLayout(InstancesPerBatch);
			batches = new();

			ChunkBatches = new(16, Allocator.Persistent);
		}
		public void Dispose (ref BatchRendererGroup brg) {
			foreach (var b in batches.Values) {
				b.Dispose(ref brg);
			}
			batches = null;

			ChunkBatches.Dispose();
		}

		BatchChunk AllocChunkData (ref BatchRendererGroup brg, in ArchetypeChunk chunk) {
			// Scan Batches for free chunk, could use freelist but filling empty slots from the front _feels_ more optimal (is it?)
			BatchChunk entry;
			foreach (var b in batches.Values) {
				if (!b.Full) {
					entry = b.AllocChunk();
					ChunkBatches.Add(chunk, entry);
					return entry;
				}
			}
			
			// All batches full, alloc new batch
			var batch = new Batch(ref brg, layout);
			batches.Add(batch.batchID, batch);

			entry = batch.AllocChunk();
			ChunkBatches.Add(chunk, entry);
			return entry;
		}
		void FreeChunkData (ref BatchRendererGroup brg, in ArchetypeChunk chunk) {
			var entry = ChunkBatches[chunk];
			var batch = batches[entry.batchID];

			batch.FreeChunk(entry.Slot);

			if (batch.UsedChunks == 0) {
				Debug.Log($"delete Batch");

				batches.Remove(batch.batchID);
				batch.Dispose(ref brg);
			}

			ChunkBatches.Remove(chunk);
		}
		
		static readonly ProfilerMarker perf = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.FindNewAndDeletedChunks");
		
		[BurstCompile]
		static void FindNewAndDeletedChunks (in EntityQuery query, in NativeHashMap<ArchetypeChunk, BatchChunk> ChunkBatches,
				ref NativeList<ArchetypeChunk> newChunks, ref NativeList<ArchetypeChunk> deletedChunks) {
			perf.Begin();

			var queryChunks = query.ToArchetypeChunkArray(Allocator.Temp);
			foreach (var chunk in queryChunks) {
				if (!ChunkBatches.ContainsKey(chunk)) {
					newChunks.Add(chunk);
				}
			}

			foreach (var chunk in ChunkBatches) {
				if (chunk.Key.Invalid()) {
					deletedChunks.Add(chunk.Key);
				}
			}

			queryChunks.Dispose();

			perf.End();
		}

		public void UpdateGpuAllocation (ref BatchRendererGroup brg, in EntityQuery query) {
			NativeList<ArchetypeChunk> newChunks = new(32, Allocator.Temp);
			NativeList<ArchetypeChunk> deletedChunks = new(32, Allocator.Temp);
			FindNewAndDeletedChunks(query, ChunkBatches, ref newChunks, ref deletedChunks);

			foreach (var chunk in newChunks) {
				AllocChunkData(ref brg, chunk);
			}
			foreach (var chunk in deletedChunks) { // Can't Remove from hashmap while iterating!
				FreeChunkData(ref brg, chunk);
			}
			
		#if false
			{
				int totalUsed = 0;
				int totalAllocated = 0;
				string usage = "";
				foreach (var batch in batches) {
					totalUsed += batch.Value.UsedChunks;
					totalAllocated += ChunksPerBatch;
					usage += $"{batch.Value.UsedChunks}, ";
				}
				float wasted = (totalAllocated - totalUsed) / (float)totalAllocated;
				Debug.Log($"Chunks allocated {totalUsed}, Wasted {wasted*100} % in {batches.Count} Batches ({usage})!");
			}
		#endif
		}
		
		public struct ThreadedUploads {
			public BufferLayout layout;
			public NativeHashMap<BatchID, ThreadedSparseUploader> uploaders;
			
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public unsafe void Write (in BatchChunk entry, float3x4* obj2world, float4* color, int count) {
				var up = uploaders[entry.batchID];
				up.AddMatrixUploadAndInverse(obj2world, count,
					layout.offs_obj2world + entry.InstanceOffset*sizeof(float3x4),
					layout.offs_world2obj + entry.InstanceOffset*sizeof(float3x4),
					ThreadedSparseUploader.MatrixType.MatrixType3x4, ThreadedSparseUploader.MatrixType.MatrixType3x4);
				up.AddUpload(color, count*sizeof(float4),
					layout.offs_color + entry.InstanceOffset*sizeof(float4));
			}
		}
		
		public unsafe ThreadedUploads BeginUpload () {
			//Debug.Log("BeginUpload");
			ThreadedUploads u;
			u.layout = layout;
			u.uploaders = new(batches.Count, Allocator.TempJob);
			
			foreach (var b in batches.Values) {
				var tsu = b.upload.Begin(
					maxDataSizeInBytes: (sizeof(float3x4) + sizeof(float4)) * InstancesPerBatch,
					biggestDataUpload: sizeof(float3x4) * InstancesPerChunk, // float3x4 * Max Instances per Chunk
					maxOperationCount: 2 * ChunksPerBatch);
				u.uploaders.Add(b.batchID, tsu);
			}

			return u;
		}
		public void EndUpload (ref ThreadedUploads uploads) {
			//Debug.Log("EndUpload");
			
			foreach (var b in batches.Values) {
				b.upload.EndAndCommit(uploads.uploaders[b.batchID]);
				b.upload.FrameCleanup();
			}

			uploads.uploaders.Dispose();
		}
	}
	
	public unsafe struct ChunkVisiblity {
		public ArchetypeChunk chunk;
		public InstanceData.BatchChunk Batch;

		public fixed byte EntityVisible[128]; // either bool for camera culling or split mask for light culling
		public fixed int InstanceOffset[128]; // per-instance offset within their draw
	}

	public struct DrawSettings : IEquatable<DrawSettings> {
		public const int SplitPermut = 16;

		public BatchID BatchID;
		public ushort SplitMask;

		int cachedHash;
		
		public override string ToString () {
			return $"DrawSettings(BatchID: {BatchID.value}, SplitMask: {SplitMask})";
		}

		//public static int FastHash<T>(T value) where T : struct {
		//	// TODO: Replace with hardware CRC32?
		//	return (int)xxHash3.Hash64(UnsafeUtility.AddressOf(ref value), UnsafeUtility.SizeOf<T>()).x;
		//}


		public bool Equals (DrawSettings other) {
			return BatchID == other.BatchID && SplitMask == other.SplitMask;
		}

		public override int GetHashCode () => cachedHash;

		// Not called from ctor because Entities-Graphics does it manually
		public void ComputeHashCode () {
			cachedHash = (int)hash(int2((int)BatchID.value, SplitMask));
		}
	}
	public struct DrawData {
		public unsafe struct Command {
			public DrawSettings DrawSettings;
			public int visibleInstances;
			public BatchDrawCommand* cmd;
		}
		
		// TODO: exclude useless 0 for efficiency
		public const int SplitPermut = 16;

		// Later would map from (Mesh,Material,Batch)

		// Can't Interlocked.Increment on NativeHashMap values, have to use indirection
		public NativeArray<Command> commands;
		[ReadOnly] public NativeHashMap<DrawSettings, int> cmdLookup;

		// Preallocated command hashmap, TODO: entities graphics dynamically adds to this from jobs, maybe I should do that later as well
		public unsafe DrawData (in InstanceData instanceData) {
			int numBatches = instanceData.batches.Count;
			commands = new(SplitPermut * numBatches, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
			cmdLookup = new(SplitPermut * numBatches, Allocator.TempJob);
			
			int i = 0;
			foreach (var batchID in instanceData.batches.Keys) {
				for (int split=0; split<SplitPermut; split++) {
					var settings = new DrawSettings {
						BatchID = batchID,
						SplitMask = (ushort)split
					};
					settings.ComputeHashCode();

					cmdLookup.Add(settings, i);

					commands[i++] = new Command {
						DrawSettings = settings,
						visibleInstances = 0,
						cmd = default
					};
				}
			}
		}
		public void Dispose (JobHandle job) {
			commands.Dispose(job);
			cmdLookup.Dispose(job);
		}
		
		// Allocate space in per-draw visible instance list by atomically adding to instance count
		// returns old count ie. start index where count instances will go
		public unsafe int AddInstances (in DrawSettings settings, int count) {
			var cmdIdx = cmdLookup[settings];

			ref var cmd = ref ((Command*)commands.GetUnsafePtr())[cmdIdx];
			int newCount = Interlocked.Add(ref cmd.visibleInstances, count);
			return newCount - count; // return offset that was assigned
		}
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

			instanceData = new InstanceData(ref brg);
		}
		protected override void OnStopRunning () {
			Debug.Log("CustomEntityRendererSystem.OnStopRunning");

			// Probably no need to remove batchID from brg
			instanceData.Dispose(ref brg);
			brg.Dispose();
		}

		static readonly ProfilerMarker perfUpload = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.UpdateGpuAllocation");
		static readonly ProfilerMarker perfJobs = new ProfilerMarker(ProfilerCategory.Render, "CustomEntityRenderer.OnPerformCulling");

		//[BurstCompile]
		protected override void OnUpdate () {
			//Debug.Log($"CustomEntityRendererSystem.OnUpdate");

			perfUpload.Begin();
			instanceData.UpdateGpuAllocation(ref brg, query);
			perfUpload.End();
			
			ComputeInstanceDataJobHandle = new JobHandle();

			if (query.IsEmpty)
				return;

			UploadInstanceData();
		}
		
		void UploadInstanceData () {
			var controller = SystemAPI.GetSingleton<ControllerECS>();

			c_transformsRO.Update(this);
			c_dataRO.Update(this);
			c_Asset.Update(this);

			var uploads = instanceData.BeginUpload();

			ComputeInstanceDataJobHandle = new ComputeInstanceDataJob {
				LocalTransforms = c_transformsRO,
				Data = c_dataRO,
				Controller = controller,
				Uploads = uploads,
				ChunkBatches = instanceData.ChunkBatches,
				LastSystemVersion = LastSystemVersion,
			}.ScheduleParallel(query, Dependency);
			
			ComputeInstanceDataJobHandle.Complete();
			instanceData.EndUpload(ref uploads);

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

			if (query.IsEmpty)
				return new JobHandle();

			c_transformsRO.Update(this);
			c_Asset.Update(this);
			c_ChunkBounds.Update(this);

			perfJobs.Begin();

			////
			// Reuse Unity Culling Utils
			var cullingData = CullingSplits.Create(&cullingContext, QualitySettings.shadowProjection, World.UpdateAllocator.Handle);

			int NumChunks = query.CalculateChunkCount();
			var chunkVisibilty = new NativeList<ChunkVisiblity>(NumChunks, Allocator.TempJob);

			var drawData = new DrawData(instanceData);

			var cullJob = new CullEntityInstancesJob {
				LocalTransformHandle = c_transformsRO,
				AssetHandle = c_Asset,
				ChunkBounds = c_ChunkBounds,
				chunkVisibilty = chunkVisibilty.AsParallelWriter(),
				drawData = drawData,
				ChunkBatches = instanceData.ChunkBatches,
				CullingData = cullingData,
				CullingViewType = cullingContext.viewType,
			}.ScheduleParallel(query, ComputeInstanceDataJobHandle);

			var allocCmdsJob = new AllocDrawCommandsJob {
				meshID = meshID,
				materialID = materialID,
				cullingOutputCommands = cullingOutput.drawCommands,
				drawData = drawData,
			}.Schedule(cullJob);

			var writeInstancesJob = new WriteDrawInstanceIndicesJob {
				chunkVisibilty = chunkVisibilty.AsDeferredJobArray(),
				drawData = drawData,
				cullingOutputCommands = cullingOutput.drawCommands,
			}.Schedule(chunkVisibilty, 8, allocCmdsJob);

			writeInstancesJob.Complete();

			chunkVisibilty.Dispose(writeInstancesJob);
			drawData.Dispose(writeInstancesJob);

			perfJobs.End();
			return writeInstancesJob;
		}
	}

	// Could be optimized by only uploading if not culled, but since culling happens seperately for camera and shadows, this is non-trivial
	// Possibly flag visible chunks and lather upload data if gpu instance data is not needed for culling/lod
	// Actually this makes a lot of sense, since some the the data needed on gpu depends on LOD, like having only LOD0 be animated for example
	// Might also be able to lazily upload during cull
	[BurstCompile]
	unsafe partial struct ComputeInstanceDataJob : IJobChunk {
		[ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransforms;
		[ReadOnly] public ComponentTypeHandle<MyEntityData> Data;
		[ReadOnly] public ControllerECS Controller;

		[ReadOnly] public InstanceData.ThreadedUploads Uploads;
		[ReadOnly] public NativeHashMap<ArchetypeChunk, InstanceData.BatchChunk> ChunkBatches;

		public uint LastSystemVersion;

		void ReuploadChunkInstances (in ArchetypeChunk chunk, int unfilteredChunkIndex) {
			
			NativeArray<LocalTransform> transforms = chunk.GetNativeArray(ref LocalTransforms);
			NativeArray<MyEntityData> spinningData = chunk.GetNativeArray(ref Data);

			var obj2world = stackalloc float3x4[chunk.Count];
			var color = stackalloc float4[chunk.Count];

			for (int i = 0; i < chunk.Count; i++) {
				var transform = transforms[i];

				var col = Controller.DebugSpatialGrid ?
					MyEntityData.RandColor(UpdateSpatialGridSystem.CalcGridCell(Controller, transform.Position)) :
					spinningData[i].Color;
				
				obj2world[i] = InstanceData.LocalTransform2float3x4(transform);
				color[i] = float4(col.r, col.g, col.b, col.a); // Could avoid uploading color if it did not actually change
			}
			
			Uploads.Write(ChunkBatches[chunk], obj2world, color, chunk.Count);
		}

		[BurstCompile]
		public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
			//if (  //chunk.DidOrderChange(LastSystemVersion) ||
			//      chunk.DidChange(ref LocalTransforms, LastSystemVersion) ||
			//      chunk.DidChange(ref Data, LastSystemVersion)) {
				ReuploadChunkInstances(chunk, unfilteredChunkIndex);
			//}
		}
	}

}
