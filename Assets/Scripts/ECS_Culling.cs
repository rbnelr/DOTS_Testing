// #define DISABLE_SHADOW_CULLING_CAPSULE_TEST
// #define DISABLE_HYBRID_SPHERE_CULLING
// #define DISABLE_HYBRID_RECEIVER_CULLING
// #define DISABLE_INCLUDE_EXCLUDE_LIST_FILTERING
// #define DEBUG_VALIDATE_VISIBLE_COUNTS
// #define DEBUG_VALIDATE_COMBINED_SPLIT_RECEIVER_CULLING
// #define DEBUG_VALIDATE_VECTORIZED_CULLING
// #define DEBUG_VALIDATE_EXTRA_SPLITS

#if UNITY_EDITOR
using UnityEditor;
#endif

using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;

using FrustumPlanes = Unity.Rendering.FrustumPlanes;
using System.Collections.Generic;
using System;
using Unity.Transforms;
using System.Threading;

public static class CullingExtensions {
	// We want to use UnsafeList to use RewindableAllocator, but PlanePacket APIs want NativeArrays
	public static unsafe NativeArray<T> AsNativeArray<T> (this UnsafeList<T> list) where T : unmanaged {
		var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(list.Ptr, list.Length, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
		return array;
	}

	public static NativeArray<T> GetSubNativeArray<T> (this UnsafeList<T> list, int start, int length)
		where T : unmanaged =>
		list.AsNativeArray().GetSubArray(start, length);


	public static void InitializeSOAPlanePackets (NativeArray<FrustumPlanes.PlanePacket4> planes, NativeArray<Plane> cullingPlanes) {
		int cullingPlaneCount = cullingPlanes.Length;
		int packetCount = planes.Length;

		for (int i = 0; i < cullingPlaneCount; i++) {
			var p = planes[i >> 2];
			p.Xs[i & 3] = cullingPlanes[i].normal.x;
			p.Ys[i & 3] = cullingPlanes[i].normal.y;
			p.Zs[i & 3] = cullingPlanes[i].normal.z;
			p.Distances[i & 3] = cullingPlanes[i].distance;
			planes[i >> 2] = p;
		}

		// Populate the remaining planes with values that are always "in"
		for (int i = cullingPlaneCount; i < 4 * packetCount; ++i) {
			var p = planes[i >> 2];
			p.Xs[i & 3] = 1.0f;
			p.Ys[i & 3] = 0.0f;
			p.Zs[i & 3] = 0.0f;

			// This value was before hardcoded to 32786.0f.
			// It was causing the culling system to discard the rendering of entities having a X coordinate approximately less than -32786.
			// We could not find anything relying on this number, so the value has been increased to 1 billion
			p.Distances[i & 3] = 1e9f;

			planes[i >> 2] = p;
		}
	}

	public static UnsafeList<FrustumPlanes.PlanePacket4> BuildSOAPlanePackets (NativeArray<Plane> cullingPlanes, AllocatorManager.AllocatorHandle allocator) {
		int cullingPlaneCount = cullingPlanes.Length;
		int packetCount = (cullingPlaneCount + 3) >> 2;
		var planes = new UnsafeList<FrustumPlanes.PlanePacket4>(packetCount, allocator, NativeArrayOptions.UninitializedMemory);
		planes.Resize(packetCount);

		InitializeSOAPlanePackets(planes.AsNativeArray(), cullingPlanes);

		return planes;
	}

	public static NativeArray<FrustumPlanes.PlanePacket4> BuildSOAPlanePackets (NativeArray<Plane> cullingPlanes, Allocator allocator) {
		int cullingPlaneCount = cullingPlanes.Length;
		int packetCount = (cullingPlaneCount + 3) >> 2;
		var planes = new NativeArray<FrustumPlanes.PlanePacket4>(packetCount, allocator, NativeArrayOptions.UninitializedMemory);

		InitializeSOAPlanePackets(planes, cullingPlanes);

		return planes;
	}

}

namespace CustomEntity {
	
	// Derived from Unity Entity Graphics rather than reused because it was internal

#if false
	[BurstCompile]
	unsafe partial struct CullEntityInstancesJob : IJobChunk {

		[ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
		[ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransformHandle;
		[ReadOnly] public SharedComponentTypeHandle<Asset> AssetHandle;
		[ReadOnly] public ComponentTypeHandle<ChunkBounds> ChunkBounds;

		[ReadOnly] public CullingSplits CullingData;
		[ReadOnly] public BatchCullingViewType CullingViewType;

		public NativeList<ChunkVisiblity>.ParallelWriter chunkVisibilty;
		public NativeArray<DrawCommand> drawCommands;

		unsafe struct SplitCounts {
			public fixed int visibleCount[16];
			public fixed int outputInstanceOffset[16];
		}

		// CullingSplits works like this:
		// For camera: SplitPlanePackets contain presumably the 6 planes of the camera frustrum against which an AABB can be tested
		//  however these are stored in a SIMD way that tests 4(?) Planes at once
		// For BatchCullingViewType.Light (Directional shadows in my case):
		//  CullingData.ReceiverPlanePackets:
		//     I think contain the entire shadowmap volume (no splits)
		//  CullingData.SplitAndReceiverPlanePackets(for split):
		//     I think contain shadowmap volume split up with additional planes in attempt to cull away shadow cascades that instance does not touch
		//  CullingData.ReceiverSphereCuller:
		//     Seems to be a way to further cull instances because bigger shadow map cascades actually overlap smaller ones?
		//     But this really confuses me

		[BurstCompile]
		public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 enabledMaskIn) {
			Assert.IsFalse(useEnabledMask);
		
			NativeArray<LocalTransform> transforms = chunk.GetNativeArray(ref LocalTransformHandle);
			
			var asset = chunk.GetSharedComponent(AssetHandle);
			if (!asset.Mesh.IsValid()) return;
		
			ChunkVisiblity vis;
			vis.chunk = chunk;
			vis.BaseInstanceIdx = ChunkBaseEntityIndices[unfilteredChunkIndex];

			for (int i=0; i<chunk.Count; i++) {
				vis.EntityVisible[i] = 0;
				vis.InstanceOffset[i] = 0;
			}

			if (CullingViewType == BatchCullingViewType.Light) {
				ref var splits = ref CullingData.Splits;
				Assert.IsTrue(splits.Length <= 4);

				// Do per-instance culling for all splits
				for (int splitIndex = 0; splitIndex < splits.Length; ++splitIndex) {
					var split = splits[splitIndex];
					var splitPlanes = CullingData.CombinedSplitAndReceiverPlanePackets.GetSubNativeArray(
						split.CombinedPlanePacketOffset, split.CombinedPlanePacketCount);
					var splitMask = (byte)(1 << splitIndex);

					for (int i=0; i<chunk.Count; i++) {
						AABB aabb = asset.CalcWorldBounds(transforms[i]);

						bool isVisible = FrustumPlanes.Intersect2NoPartial(splitPlanes, aabb) != FrustumPlanes.IntersectResult.Out;
						if (isVisible) {
							vis.EntityVisible[i] |= splitMask;
						}
					}
				}
				
				// TODO: optimize by using bitscan instructions to quickly skip culled entities,
				// this is only relevant after first culling pass or if many entities are disabled beforehand (like invisible lod level)

				// I don't understand what SphereTesting actually is for, but this mimics Entity.Graphics
				if (CullingData.SphereTestEnabled) {
					
					// Instances are culled either through CombinedSplitAndReceiverPlanePackets or later through SphereTest
					for (int i=0; i<chunk.Count; i++) {
						if (vis.EntityVisible[i] > 0) {
							AABB aabb = asset.CalcWorldBounds(transforms[i]); // cache from previous loop?
						
							int sphereSplitMask = CullingData.ReceiverSphereCuller.Cull(aabb);

							vis.EntityVisible[i] &= (byte)sphereSplitMask;
						}
					}
				}
				
				// Count visible instances for each draw (unique combination of splits an instance is drawn in)
				SplitCounts splitCounts;
				for (int splitMask=0; splitMask<16; ++splitMask) {
					splitCounts.visibleCount[splitMask] = 0;
				}
				
				for (int i=0; i<chunk.Count; i++) {
					var splitMask = vis.EntityVisible[i];
					if (splitMask > 0) {
						// Increment visible instances for this draw and chunk
						int localIdx = splitCounts.visibleCount[splitMask];
						splitCounts.visibleCount[splitMask]++;
						// remeber local index temporarily
						vis.InstanceOffset[i] = localIdx;
					}
				}
				
				// allocate space in output instances for draw in bulk
				for (int splitMask=0; splitMask<16; ++splitMask) {
					// visible instances for this draw
					int count = splitCounts.visibleCount[splitMask];
					// count instances have been allocated at offset for this draw (happens in parallel with other chunks)
					int offset = DrawCommand.AddInstances(ref drawCommands, splitMask, count);
					// remember offset
					splitCounts.outputInstanceOffset[splitMask] = offset;
				}
				
				bool anyVisible = false;
				// count instances again to 
				for (int i=0; i<chunk.Count; i++) {
					var splitMask = vis.EntityVisible[i];
					if (splitMask > 0) {
						// add in actual offset for output instance ids (Could be skipped if outputInstanceOffset is passed to WriteDrawInstanceIndicesJob)
						vis.InstanceOffset[i] += splitCounts.outputInstanceOffset[splitMask];
						
						anyVisible = true;
					}
				}

				if (anyVisible)
					chunkVisibilty.AddNoResize(vis);
			}
			else {
				Assert.IsTrue(CullingData.Splits.Length == 1);

				var splitPlanes = CullingData.SplitPlanePackets.AsNativeArray();
			
				int visibleCount = 0;
				for (int i=0; i<chunk.Count; i++) {
					AABB aabb = new AABB { Center = transforms[i].Position, Extents = 1 }; // hack

					bool isVisible = FrustumPlanes.Intersect2NoPartial(splitPlanes, aabb) != FrustumPlanes.IntersectResult.Out;
					vis.EntityVisible[i] = isVisible ? (byte)1 : (byte)0;
					if (isVisible) {
						vis.InstanceOffset[i] = visibleCount++;
					}
				}
				
				if (visibleCount > 0) {
					int offset = DrawCommand.AddInstances(ref drawCommands, 1, visibleCount);
				
					// count instances again to 
					for (int i=0; i<chunk.Count; i++) {
						bool isVisible = vis.EntityVisible[i] > 0;
						if (isVisible) {
							vis.InstanceOffset[i] += offset;
						}
					}

					chunkVisibilty.AddNoResize(vis);
				}
			}
		}
	}
#else
	// Per-Chunk Culling Only
	[BurstCompile]
	unsafe partial struct CullEntityInstancesJob : IJobChunk {

		//[ReadOnly] public NativeArray<int> ChunkBaseEntityIndices;
		[ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransformHandle;
		[ReadOnly] public SharedComponentTypeHandle<Asset> AssetHandle;
		[ReadOnly] public ComponentTypeHandle<ChunkBounds> ChunkBounds;

		[ReadOnly] public CullingSplits CullingData;
		[ReadOnly] public BatchCullingViewType CullingViewType;

		public NativeList<ChunkVisiblity>.ParallelWriter chunkVisibilty;
		public DrawData drawData;
		[ReadOnly] public NativeHashMap<ArchetypeChunk, InstanceData.BatchChunk> ChunkBatches;

		unsafe struct SplitCounts {
			public fixed int visibleCount[16];
			public fixed int outputInstanceOffset[16];
		}

		// CullingSplits works like this:
		// For camera: SplitPlanePackets contain presumably the 6 planes of the camera frustrum against which an AABB can be tested
		//  however these are stored in a SIMD way that tests 4(?) Planes at once
		// For BatchCullingViewType.Light (Directional shadows in my case):
		//  CullingData.ReceiverPlanePackets:
		//     I think contain the entire shadowmap volume (no splits)
		//  CullingData.SplitAndReceiverPlanePackets(for split):
		//     I think contain shadowmap volume split up with additional planes in attempt to cull away shadow cascades that instance does not touch
		//  CullingData.ReceiverSphereCuller:
		//     Seems to be a way to further cull instances because bigger shadow map cascades actually overlap smaller ones?
		//     But this really confuses me

		[BurstCompile]
		public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 enabledMaskIn) {
			Assert.IsFalse(useEnabledMask);
		
			var transforms = chunk.GetNativeArray(ref LocalTransformHandle);
			var chunkBounds = chunk.GetChunkComponentData(ref ChunkBounds);
			
			var asset = chunk.GetSharedComponent(AssetHandle);
			if (!asset.Mesh.IsValid()) return;
		
			ChunkVisiblity vis;
			vis.chunk = chunk;
			vis.Batch = ChunkBatches[chunk];

			if (CullingViewType == BatchCullingViewType.Light) {
				ref var splits = ref CullingData.Splits;
				Assert.IsTrue(splits.Length <= 4);

				byte chunkSplitVisible = 0;

				for (int splitIndex = 0; splitIndex < splits.Length; ++splitIndex) {
					var split = splits[splitIndex];
					var splitPlanes = CullingData.CombinedSplitAndReceiverPlanePackets.GetSubNativeArray(
						split.CombinedPlanePacketOffset, split.CombinedPlanePacketCount);
					var splitMask = (byte)(1 << splitIndex);
					
					bool isVisible = FrustumPlanes.Intersect2NoPartial(splitPlanes, chunkBounds.bounds)
						!= FrustumPlanes.IntersectResult.Out;

					if (isVisible) {
						chunkSplitVisible |= splitMask;
					}
				}
				
				// TODO: optimize by using bitscan instructions to quickly skip culled entities,
				// this is only relevant after first culling pass or if many entities are disabled beforehand (like invisible lod level)

				// I don't understand what SphereTesting actually is for, but this mimics Entity.Graphics
				if (CullingData.SphereTestEnabled) {
					// Instances are culled either through CombinedSplitAndReceiverPlanePackets or later through SphereTest
					if (chunkSplitVisible > 0) {
						chunkSplitVisible &= (byte)CullingData.ReceiverSphereCuller.Cull(chunkBounds.bounds);
					}
				}
				
				// Use the same SplitMask for the entire chunk, since likely only some chunks will cross split boundaries
				// Entities Graphics similarily only does culling for individual entities in some cases, and if the chunk is garantueed to be partially in/out
				if (chunkSplitVisible > 0) {
					// Draw-local chunk instances start offset
					var settings = new DrawSettings {
						BatchID = vis.Batch.batchID,
						SplitMask = chunkSplitVisible
					};
					settings.ComputeHashCode(); // ??

					int offset = drawData.AddInstances(settings, chunk.Count);
					
					for (int i=0; i<chunk.Count; i++) {
						vis.EntityVisible[i] = chunkSplitVisible;
						// Draw-relative per-Instance offset
						vis.InstanceOffset[i] = offset + i;
					}

					chunkVisibilty.AddNoResize(vis);
				}
			}
			else {
				Assert.IsTrue(CullingData.Splits.Length == 1);

				var splitPlanes = CullingData.SplitPlanePackets.AsNativeArray();
			
				bool isVisible = FrustumPlanes.Intersect2NoPartial(splitPlanes, chunkBounds.bounds)
					!= FrustumPlanes.IntersectResult.Out;

				if (isVisible) {
					var settings = new DrawSettings {
						BatchID = vis.Batch.batchID,
						SplitMask = 1
					};
					settings.ComputeHashCode(); // ??

					int baseInstanceIdx = drawData.AddInstances(settings, chunk.Count);
					
					for (int i=0; i<chunk.Count; i++) {
						vis.EntityVisible[i] = 1;
						vis.InstanceOffset[i] = baseInstanceIdx + i;
					}

					chunkVisibilty.AddNoResize(vis);
				}
			}
		}
	}
#endif

	[BurstCompile]
	unsafe partial struct AllocDrawCommandsJob : IJob {
	
		[ReadOnly] public BatchMeshID meshID;
		[ReadOnly] public BatchMaterialID materialID;

		public NativeArray<BatchCullingOutputDrawCommands> cullingOutputCommands;
		public DrawData drawData;

		[BurstCompile]
		public void Execute () {
			NativeList<BatchDrawCommand> commandList = new(64, Allocator.TempJob);
			int visibleInstanceCount = 0;
			
			// Calc total visible instances and allocate subrange of final instance index buffer for each command
			// Also called a prefix sum which is hard to parallelize
			for (var i=0; i<drawData.commands.Length; i++) {
				var cmd = drawData.commands[i];
				if (cmd.visibleInstances > 0) {
					var batchCmd = new BatchDrawCommand {
						// Start in instance index buffer
						visibleOffset = (uint)visibleInstanceCount,
						visibleCount = (uint)cmd.visibleInstances,

						batchID = cmd.DrawSettings.BatchID,
						materialID = materialID,
						meshID = meshID,

						submeshIndex = 0,
						splitVisibilityMask = cmd.DrawSettings.SplitMask,
						flags = 0,
						sortingPosition = 0,
					};

					visibleInstanceCount += cmd.visibleInstances;
					commandList.Add(batchCmd);
				}
			}

			// Allocate commands and single instance index buffer
			// These are auto-freed by the batch renderer
			var cmds   = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>()*commandList.Length, UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
			var ranges = (BatchDrawRange  *)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>(), UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
			var visibleInstances = (int*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<int>() * visibleInstanceCount, UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
			
			// Not sure if NativeLists internal buffer is safe to be passed to BatchCullingOutputDrawCommands, do a malloc+copy!
			UnsafeUtility.MemCpy(cmds, commandList.GetUnsafePtr(), commandList.Length * sizeof(BatchDrawCommand));
			
			BatchDrawCommand* cmdOut = cmds;
			for (var i=0; i<drawData.commands.Length; i++) {
				var cmd = drawData.commands[i];
				if (cmd.visibleInstances > 0) {
					cmd.cmd = cmdOut++; // TODO: get real ptr for WriteDrawInstanceIndicesJob, though only visibleOffset is actually needed!
					drawData.commands[i] = cmd;
				}
			}

			cullingOutputCommands[0] = new BatchCullingOutputDrawCommands {
				drawCommands     = cmds,
				drawRanges       = ranges,
				visibleInstances = visibleInstances,
				drawCommandPickingInstanceIDs = null,

				drawCommandCount = commandList.Length,
				drawRangeCount = 1,
				visibleInstanceCount = visibleInstanceCount,

				instanceSortingPositions = null,
				instanceSortingPositionFloatCount = 0,
			};

			ranges[0] = new BatchDrawRange {
				drawCommandsType = BatchDrawCommandType.Direct,
				drawCommandsBegin = 0,
				drawCommandsCount = (uint)commandList.Length,
				filterSettings = new BatchFilterSettings {
					renderingLayerMask = 0xffffffff,
					receiveShadows = true,
					shadowCastingMode = ShadowCastingMode.On // Should I not get this from the material? How?
				},
			};

			commandList.Dispose();
		}
	}

	[BurstCompile]
	unsafe partial struct WriteDrawInstanceIndicesJob : IJobParallelForDefer {
	
		[ReadOnly] public NativeArray<ChunkVisiblity> chunkVisibilty;
		[ReadOnly] public DrawData drawData;
	
		[ReadOnly] public NativeArray<BatchCullingOutputDrawCommands> cullingOutputCommands;
		
		[BurstCompile]
		public void Execute (int index) {
			var vis = chunkVisibilty[index];
			int* outputInstances = cullingOutputCommands[0].visibleInstances;
			
			var drawSettings = new DrawSettings {
				BatchID = vis.Batch.batchID,
				SplitMask = 0
			};
			int drawVisibleOffset = 0;

			for (int i=0; i<vis.chunk.Count; i++) {
				// Instance indices are relative to the batch graphics buffer
				int InstanceIdx = vis.Batch.InstanceOffset + i;
			
				byte splitMask = vis.EntityVisible[i];
				if (splitMask > 0) {
					// Optimize sequential equal DrawSettings, by avoiding expensive hashmap lookup
					// Currently entire chunk has equal DrawSettings(!), but later mesh (via LOD at least) will be part of DrawSettings
					if (splitMask != drawSettings.SplitMask) {
						drawSettings = new DrawSettings { BatchID = drawSettings.BatchID, SplitMask = splitMask };
						drawSettings.ComputeHashCode();

						var cmd = drawData.commands[drawData.cmdLookup[drawSettings]];
						drawVisibleOffset = (int)cmd.cmd->visibleOffset;
					}

					// Finally write instance index
					int outputIdx = vis.InstanceOffset[i] + drawVisibleOffset;
					outputInstances[outputIdx] = InstanceIdx;
				}
			}
		}
	}



#if UNITY_EDITOR
	// Fun Visualization of Culling of Game view camera in Scene view, not safe at all!
	[System.Serializable]
	public class VisCulling {
		// Copy NativeArray data since it is only valid this frame
		static BatchCullingContext CopyCullingData (in BatchCullingContext ctx) {
			// Gnarly and not safe at all!
			var planes = new NativeArray<Plane>(ctx.cullingPlanes.ToArray(), Allocator.TempJob);
			var splits = new NativeArray<CullingSplit>(ctx.cullingSplits.ToArray(), Allocator.TempJob);
			IntPtr occlusionBuffer = IntPtr.Zero;
			
			var viewID = (ulong)ctx.viewID.GetType().GetField("handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(ctx.viewID);

			var ctors = ctx.GetType().GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			return (BatchCullingContext)ctors[0].Invoke(new object[] { planes, splits, ctx.lodParameters, ctx.localToWorldMatrix,
					ctx.viewType, ctx.projectionType, ctx.cullingFlags, viewID, ctx.cullingLayerMask, ctx.sceneCullingMask,
					(byte)ctx.splitExclusionMask, ctx.receiverPlaneOffset, ctx.receiverPlaneCount, occlusionBuffer });
		}

		List<BatchCullingContext> culling = new();
		public BatchCullingContext? prevCameraCulling { get; private set; }

		public bool VisualizePlanes = true;
		public bool VisualizeCullingInScene = true;

		public int SceneCamIndex = 0;
		public BatchCullingViewType ShowType = BatchCullingViewType.Light;
		public int ShowSplit = 0;

		public void Draw () {
			foreach (var ctx in culling) {
				DebugCulling(ctx);
			}
			
			DisposeData();
		}
		public void DisposeData () {
			// Needs to be disposed
			foreach (var ctx in culling) {
				ctx.cullingPlanes.Dispose();
				ctx.cullingSplits.Dispose();
			}
			culling.Clear();
			counter = 0;
			// Has been disposed already
			prevCameraCulling = null;
		}

		int counter = 0;

		public void OnCulling (ref BatchCullingContext ctx) {
			int idx = counter++;
			if (idx/2 == SceneCamIndex) {
				// Don't show scene camera culling
				if (  VisualizeCullingInScene &&
					  (ctx.viewType == ShowType || ctx.viewType == BatchCullingViewType.Camera) &&
					  prevCameraCulling.HasValue)
					ctx = prevCameraCulling.Value;
			}
			else {
				var data = CopyCullingData(ctx);

				culling.Add(data);

				if (ctx.viewType == ShowType)
					prevCameraCulling = data;
			}
		}

		static void DebugDrawPlane (float3 cam_pos, Plane plane, float width, int count, Color col) {
			float3 normal = normalize(plane.normal);

			float3 up = float3(0,1,0);
			float3 forw = cross(up, normal);
	
			up = normalize(cross(normal, forw));
			forw = normalize(forw);

			float3 pos = plane.ClosestPointOnPlane(cam_pos);

			for (int i=0; i<=count; i++) {
				float3 x = forw * lerp(-width, width, i / (float)count);
				Debug.DrawLine(
					pos - up * width + x,
					pos + up * width + x,
					col);
			}
			for (int i=0; i<=count; i++) {
				float3 x = up * lerp(-width, width, i / (float)count);
				Debug.DrawLine(
					pos - forw * width + x,
					pos + forw * width + x,
					col);
			}
		}
	
		void DebugCulling (in BatchCullingContext ctx) {
			if (VisualizePlanes && ctx.viewType == ShowType) {
		
				for (int s = 0; s < ctx.cullingSplits.Length; s++) {
					if (s != ShowSplit) continue;
					var split = ctx.cullingSplits[s];

					for (int i = split.cullingPlaneOffset; i < split.cullingPlaneOffset + split.cullingPlaneCount; i++) {
						DebugDrawPlane(ctx.lodParameters.cameraPosition, ctx.cullingPlanes[i], 10, 10, Color.grey);
					}
				}
			}
		}
	}
#endif

	internal unsafe struct CullingSplits {
		public UnsafeList<Plane> BackfacingReceiverPlanes;
		public UnsafeList<FrustumPlanes.PlanePacket4> SplitPlanePackets;
		public UnsafeList<FrustumPlanes.PlanePacket4> ReceiverPlanePackets;
		public UnsafeList<FrustumPlanes.PlanePacket4> CombinedSplitAndReceiverPlanePackets;
		public UnsafeList<CullingSplitData> Splits;
		public ReceiverSphereCuller ReceiverSphereCuller;
		public bool SphereTestEnabled;

		public static CullingSplits Create (BatchCullingContext* cullingContext, ShadowProjection shadowProjection, AllocatorManager.AllocatorHandle allocator) {
			CullingSplits cullingSplits = default;

			var createJob = new CreateJob {
				cullingContext = cullingContext,
				shadowProjection = shadowProjection,
				allocator = allocator,
				Splits = &cullingSplits
			};
			createJob.Run();

			return cullingSplits;
		}

		[BurstCompile]
		private struct CreateJob : IJob {
			[NativeDisableUnsafePtrRestriction]
			[ReadOnly] public BatchCullingContext* cullingContext;
			[ReadOnly] public ShadowProjection shadowProjection;
			[ReadOnly] public AllocatorManager.AllocatorHandle allocator;

			[NativeDisableUnsafePtrRestriction]
			public CullingSplits* Splits;

			public void Execute () {
				*Splits = new CullingSplits(ref *cullingContext, shadowProjection, allocator);
			}
		}

		private CullingSplits (ref BatchCullingContext cullingContext,
			ShadowProjection shadowProjection,
			AllocatorManager.AllocatorHandle allocator) {
			BackfacingReceiverPlanes = default;
			SplitPlanePackets = default;
			ReceiverPlanePackets = default;
			CombinedSplitAndReceiverPlanePackets = default;
			Splits = default;
			ReceiverSphereCuller = default;
			SphereTestEnabled = false;

			// Initialize receiver planes first, so they are ready to be combined in
			// InitializeSplits
			InitializeReceiverPlanes(ref cullingContext, allocator);
			InitializeSplits(ref cullingContext, allocator);
			InitializeSphereTest(ref cullingContext, shadowProjection);
		}

		private void InitializeReceiverPlanes (ref BatchCullingContext cullingContext, AllocatorManager.AllocatorHandle allocator) {
#if DISABLE_HYBRID_RECEIVER_CULLING
            bool disableReceiverCulling = true;
#else
			bool disableReceiverCulling = false;
#endif
			// Receiver culling is only used for shadow maps
			if ((cullingContext.viewType != BatchCullingViewType.Light) ||
				(cullingContext.receiverPlaneCount == 0) ||
				disableReceiverCulling) {
				// Make an empty array so job system doesn't complain.
				ReceiverPlanePackets = new UnsafeList<FrustumPlanes.PlanePacket4>(0, allocator);
				return;
			}

			bool isOrthographic = cullingContext.projectionType == BatchCullingProjectionType.Orthographic;
			int numPlanes = 0;

			var planes = cullingContext.cullingPlanes.GetSubArray(
				cullingContext.receiverPlaneOffset,
				cullingContext.receiverPlaneCount);
			BackfacingReceiverPlanes = new UnsafeList<Plane>(planes.Length, allocator);
			BackfacingReceiverPlanes.Resize(planes.Length);

			float3 lightDir = ((float4)cullingContext.localToWorldMatrix.GetColumn(2)).xyz;
			Vector3 lightPos = cullingContext.localToWorldMatrix.GetPosition();

			for (int i = 0; i < planes.Length; ++i) {
				var p = planes[i];
				float3 n = p.normal;

				const float kEpsilon = (float)1e-12;

				// Compare with epsilon so that perpendicular planes are not counted
				// as back facing
				bool isBackfacing = isOrthographic
					? math.dot(n, lightDir) < -kEpsilon
					: p.GetSide(lightPos);

				if (isBackfacing) {
					BackfacingReceiverPlanes[numPlanes] = p;
					++numPlanes;
				}
			}

			ReceiverPlanePackets = CullingExtensions.BuildSOAPlanePackets(
				BackfacingReceiverPlanes.GetSubNativeArray(0, numPlanes),
				allocator);
			BackfacingReceiverPlanes.Resize(numPlanes);
		}

#if DEBUG_VALIDATE_EXTRA_SPLITS
        private static int s_DebugExtraSplitsCounter = 0;
#endif

		private void InitializeSplits (ref BatchCullingContext cullingContext, AllocatorManager.AllocatorHandle allocator) {
			var cullingPlanes = cullingContext.cullingPlanes;
			var cullingSplits = cullingContext.cullingSplits;

			int numSplits = cullingSplits.Length;

#if DEBUG_VALIDATE_EXTRA_SPLITS
            // If extra splits validation is enabled, pad the split number so it's between 5 and 8 by copying existing
            // splits, to ensure that the code functions correctly with higher split counts.
            if (numSplits > 1 && numSplits < 5)
            {
                numSplits = 5 + s_DebugExtraSplitsCounter;
                s_DebugExtraSplitsCounter = (s_DebugExtraSplitsCounter + 1) % 4;
            }
#endif

			Assert.IsTrue(numSplits > 0, "No culling splits provided, expected at least 1");
			Assert.IsTrue(numSplits <= 8, "Split count too high, only up to 8 splits supported");

			int planePacketCount = 0;
			int combinedPlanePacketCount = 0;
			for (int i = 0; i < numSplits; ++i) {
				int splitIndex = i;
#if DEBUG_VALIDATE_EXTRA_SPLITS
                splitIndex %= cullingSplits.Length;
#endif

				planePacketCount += (cullingSplits[splitIndex].cullingPlaneCount + 3) / 4;
				combinedPlanePacketCount +=
					((cullingSplits[splitIndex].cullingPlaneCount + BackfacingReceiverPlanes.Length) + 3) / 4;
			}

			SplitPlanePackets = new UnsafeList<FrustumPlanes.PlanePacket4>(planePacketCount, allocator);
			CombinedSplitAndReceiverPlanePackets = new UnsafeList<FrustumPlanes.PlanePacket4>(combinedPlanePacketCount, allocator);
			Splits = new UnsafeList<CullingSplitData>(numSplits, allocator);

			var combinedPlanes = new UnsafeList<Plane>(combinedPlanePacketCount * 4, allocator);

			int planeIndex = 0;
			int combinedPlaneIndex = 0;

			for (int i = 0; i < numSplits; ++i) {
				int splitIndex = i;
#if DEBUG_VALIDATE_EXTRA_SPLITS
                splitIndex %= cullingSplits.Length;
#endif

				var s = cullingSplits[splitIndex];
				float3 p = s.sphereCenter;
				float r = s.sphereRadius;

				if (s.sphereRadius <= 0)
					r = 0;

				var splitCullingPlanes = cullingPlanes.GetSubArray(s.cullingPlaneOffset, s.cullingPlaneCount);

				var planePackets = CullingExtensions.BuildSOAPlanePackets(
					splitCullingPlanes,
					allocator);

				foreach (var pp in planePackets)
					SplitPlanePackets.Add(pp);

				combinedPlanes.Resize(splitCullingPlanes.Length + BackfacingReceiverPlanes.Length);

				// Make combined packets that have both the split planes and the receiver planes so
				// they can be tested simultaneously
				UnsafeUtility.MemCpy(
					combinedPlanes.Ptr,
					splitCullingPlanes.GetUnsafeReadOnlyPtr(),
					splitCullingPlanes.Length * UnsafeUtility.SizeOf<Plane>());
				UnsafeUtility.MemCpy(
					combinedPlanes.Ptr + splitCullingPlanes.Length,
					BackfacingReceiverPlanes.Ptr,
					BackfacingReceiverPlanes.Length * UnsafeUtility.SizeOf<Plane>());

				var combined = CullingExtensions.BuildSOAPlanePackets(
					combinedPlanes.AsNativeArray(),
					allocator);

				foreach (var pp in combined)
					CombinedSplitAndReceiverPlanePackets.Add(pp);

				Splits.Add(new CullingSplitData {
					CullingSphereCenter = p,
					CullingSphereRadius = r,
					ShadowCascadeBlendCullingFactor = s.cascadeBlendCullingFactor,
					PlanePacketOffset = planeIndex,
					PlanePacketCount = planePackets.Length,
					CombinedPlanePacketOffset = combinedPlaneIndex,
					CombinedPlanePacketCount = combined.Length,
				});

				planeIndex += planePackets.Length;
				combinedPlaneIndex += combined.Length;
			}
		}

		private void InitializeSphereTest (ref BatchCullingContext cullingContext, ShadowProjection shadowProjection) {
			// Receiver sphere testing is only enabled if the cascade projection is stable
			bool projectionIsStable = shadowProjection == ShadowProjection.StableFit;
			bool allSplitsHaveValidReceiverSpheres = true;
			for (int i = 0; i < Splits.Length; ++i) {
				// This should also catch NaNs, which return false
				// for every comparison.
				if (!(Splits[i].CullingSphereRadius > 0)) {
					allSplitsHaveValidReceiverSpheres = false;
					break;
				}
			}

			if (projectionIsStable && allSplitsHaveValidReceiverSpheres) {
				ReceiverSphereCuller = new ReceiverSphereCuller(cullingContext, this);
				SphereTestEnabled = true;
			}
		}
	}

	internal unsafe struct CullingSplitData {
		public float3 CullingSphereCenter;
		public float CullingSphereRadius;
		public float ShadowCascadeBlendCullingFactor;
		public int PlanePacketOffset;
		public int PlanePacketCount;
		public int CombinedPlanePacketOffset;
		public int CombinedPlanePacketCount;
	}

	[BurstCompile]
	internal unsafe struct IncludeExcludeListFilter {
#if !DISABLE_INCLUDE_EXCLUDE_LIST_FILTERING
		public NativeParallelHashSet<int> IncludeEntityIndices;
		public NativeParallelHashSet<int> ExcludeEntityIndices;
		[MarshalAs(UnmanagedType.U1)]
		public bool IsIncludeEnabled;
		[MarshalAs(UnmanagedType.U1)]
		public bool IsExcludeEnabled;

		public bool IsEnabled => IsIncludeEnabled || IsExcludeEnabled;
		public bool IsIncludeEmpty => IncludeEntityIndices.IsEmpty;
		public bool IsExcludeEmpty => ExcludeEntityIndices.IsEmpty;

		public IncludeExcludeListFilter (
			EntityManager entityManager,
			NativeArray<int> includeEntityIndices,
			NativeArray<int> excludeEntityIndices,
			Allocator allocator) {
			IncludeEntityIndices = default;
			ExcludeEntityIndices = default;

			// Null NativeArray means that the list shoudln't be used for filtering
			IsIncludeEnabled = includeEntityIndices.IsCreated;
			IsExcludeEnabled = excludeEntityIndices.IsCreated;

			if (IsIncludeEnabled) {
				IncludeEntityIndices = new NativeParallelHashSet<int>(includeEntityIndices.Length, allocator);
				for (int i = 0; i < includeEntityIndices.Length; ++i)
					IncludeEntityIndices.Add(includeEntityIndices[i]);
			}
			else {
				// NativeParallelHashSet must be non-null even if empty to be passed to jobs. Otherwise errors happen.
				IncludeEntityIndices = new NativeParallelHashSet<int>(0, allocator);
			}

			if (IsExcludeEnabled) {
				ExcludeEntityIndices = new NativeParallelHashSet<int>(excludeEntityIndices.Length, allocator);
				for (int i = 0; i < excludeEntityIndices.Length; ++i)
					ExcludeEntityIndices.Add(excludeEntityIndices[i]);
			}
			else {
				// NativeParallelHashSet must be non-null even if empty to be passed to jobs. Otherwise errors happen.
				ExcludeEntityIndices = new NativeParallelHashSet<int>(0, allocator);
			}
		}

		public void Dispose () {
			if (IncludeEntityIndices.IsCreated)
				IncludeEntityIndices.Dispose();

			if (ExcludeEntityIndices.IsCreated)
				ExcludeEntityIndices.Dispose();
		}

		public JobHandle Dispose (JobHandle dependencies) {
			JobHandle disposeInclude = IncludeEntityIndices.IsCreated ? IncludeEntityIndices.Dispose(dependencies) : default;
			JobHandle disposeExclude = ExcludeEntityIndices.IsCreated ? ExcludeEntityIndices.Dispose(dependencies) : default;
			return JobHandle.CombineDependencies(disposeInclude, disposeExclude);
		}

		public bool EntityPassesFilter (int entityIndex) {
			if (IsIncludeEnabled) {
				if (!IncludeEntityIndices.Contains(entityIndex))
					return false;
			}

			if (IsExcludeEnabled) {
				if (ExcludeEntityIndices.Contains(entityIndex))
					return false;
			}

			return true;
		}
#else
        public bool IsIncludeEnabled => false;
        public bool IsExcludeEnabled => false;
        public bool IsEnabled => false;
        public bool IsIncludeEmpty => true;
        public bool IsExcludeEmpty => true;
        public bool EntityPassesFilter(int entityIndex) => true;
        public void Dispose() { }
        public JobHandle Dispose(JobHandle dependencies) => new JobHandle();
#endif
	}

	internal struct ReceiverSphereCuller {
		float4 ReceiverSphereCenterX4;
		float4 ReceiverSphereCenterY4;
		float4 ReceiverSphereCenterZ4;
		float4 LSReceiverSphereCenterX4;
		float4 LSReceiverSphereCenterY4;
		float4 LSReceiverSphereCenterZ4;
		float4 ReceiverSphereRadius4;
		float4 CoreSphereRadius4;
		UnsafeList<Plane> ShadowFrustumPlanes;

		float3 LightAxisX;
		float3 LightAxisY;
		float3 LightAxisZ;
		int NumSplits;

		public ReceiverSphereCuller (in BatchCullingContext cullingContext, in CullingSplits splits) {
			int numSplits = splits.Splits.Length;

			Assert.IsTrue(numSplits <= 4, "More than 4 culling splits is not supported for sphere testing");
			Assert.IsTrue(numSplits > 0, "No valid culling splits for sphere testing");

			if (numSplits > 4)
				numSplits = 4;

			// Initialize with values that will always fail the sphere test
			ReceiverSphereCenterX4 = new float4(float.PositiveInfinity);
			ReceiverSphereCenterY4 = new float4(float.PositiveInfinity);
			ReceiverSphereCenterZ4 = new float4(float.PositiveInfinity);
			LSReceiverSphereCenterX4 = new float4(float.PositiveInfinity);
			LSReceiverSphereCenterY4 = new float4(float.PositiveInfinity);
			LSReceiverSphereCenterZ4 = new float4(float.PositiveInfinity);
			ReceiverSphereRadius4 = 0;
			CoreSphereRadius4 = 0;

			LightAxisX = new float4(cullingContext.localToWorldMatrix.GetColumn(0)).xyz;
			LightAxisY = new float4(cullingContext.localToWorldMatrix.GetColumn(1)).xyz;
			LightAxisZ = new float4(cullingContext.localToWorldMatrix.GetColumn(2)).xyz;
			NumSplits = numSplits;

			ShadowFrustumPlanes = GetUnsafeListView(cullingContext.cullingPlanes,
				cullingContext.receiverPlaneOffset,
				cullingContext.receiverPlaneCount);

			for (int i = 0; i < numSplits; ++i) {
				int elementIndex = i & 3;
				ref CullingSplitData split = ref splits.Splits.ElementAt(i);
				float3 lsReceiverSphereCenter = TransformToLightSpace(split.CullingSphereCenter, LightAxisX, LightAxisY, LightAxisZ);

				ReceiverSphereCenterX4[elementIndex] = split.CullingSphereCenter.x;
				ReceiverSphereCenterY4[elementIndex] = split.CullingSphereCenter.y;
				ReceiverSphereCenterZ4[elementIndex] = split.CullingSphereCenter.z;

				LSReceiverSphereCenterX4[elementIndex] = lsReceiverSphereCenter.x;
				LSReceiverSphereCenterY4[elementIndex] = lsReceiverSphereCenter.y;
				LSReceiverSphereCenterZ4[elementIndex] = lsReceiverSphereCenter.z;

				ReceiverSphereRadius4[elementIndex] = split.CullingSphereRadius;
				CoreSphereRadius4[elementIndex] = split.CullingSphereRadius * split.ShadowCascadeBlendCullingFactor;
			}
		}

		public int Cull (AABB aabb) {
			int visibleSplitMask = CullSIMD(aabb);

#if DEBUG_VALIDATE_VECTORIZED_CULLING
            int referenceSplitMask = CullNonSIMD(aabb);

            // Use Debug.Log instead of Debug.Assert so that Burst does not remove it
            if (visibleSplitMask != referenceSplitMask)
                Debug.Log($"Vectorized culling test ({visibleSplitMask:x2}) disagrees with reference test ({referenceSplitMask:x2})");
#endif

			return visibleSplitMask;
		}

		int CullSIMD (AABB aabb) {
			float4 casterRadius4 = new float4(math.length(aabb.Extents));
			float4 combinedRadius4 = casterRadius4 + ReceiverSphereRadius4;
			float4 combinedRadiusSq4 = combinedRadius4 * combinedRadius4;

			float3 lsCasterCenter = TransformToLightSpace(aabb.Center, LightAxisX, LightAxisY, LightAxisZ);
			float4 lsCasterCenterX4 = lsCasterCenter.xxxx;
			float4 lsCasterCenterY4 = lsCasterCenter.yyyy;
			float4 lsCasterCenterZ4 = lsCasterCenter.zzzz;

			float4 lsCasterToReceiverSphereX4 = lsCasterCenterX4 - LSReceiverSphereCenterX4;
			float4 lsCasterToReceiverSphereY4 = lsCasterCenterY4 - LSReceiverSphereCenterY4;
			float4 lsCasterToReceiverSphereSqX4 = lsCasterToReceiverSphereX4 * lsCasterToReceiverSphereX4;
			float4 lsCasterToReceiverSphereSqY4 = lsCasterToReceiverSphereY4 * lsCasterToReceiverSphereY4;

			float4 lsCasterToReceiverSphereDistanceSq4 = lsCasterToReceiverSphereSqX4 + lsCasterToReceiverSphereSqY4;
			bool4 doCirclesOverlap4 = lsCasterToReceiverSphereDistanceSq4 <= combinedRadiusSq4;

			float4 lsZMaxAccountingForCasterRadius4 = LSReceiverSphereCenterZ4 + math.sqrt(combinedRadiusSq4 - lsCasterToReceiverSphereSqX4 - lsCasterToReceiverSphereSqY4);
			bool4 isBehindCascade4 = lsCasterCenterZ4 <= lsZMaxAccountingForCasterRadius4;

			int isFullyCoveredByCascadeMask = 0b1111;

#if !DISABLE_SHADOW_CULLING_CAPSULE_TEST
			float3 shadowCapsuleBegin;
			float3 shadowCapsuleEnd;
			float shadowCapsuleRadius;
			ComputeShadowCapsule(LightAxisZ, aabb.Center, casterRadius4.x, ShadowFrustumPlanes,
				out shadowCapsuleBegin, out shadowCapsuleEnd, out shadowCapsuleRadius);

			bool4 isFullyCoveredByCascade4 = IsCapsuleInsideSphereSIMD(shadowCapsuleBegin, shadowCapsuleEnd, shadowCapsuleRadius,
				ReceiverSphereCenterX4, ReceiverSphereCenterY4, ReceiverSphereCenterZ4, CoreSphereRadius4);

			if (math.any(isFullyCoveredByCascade4)) {
				// The goal here is to find the first non-zero bit in the mask, then set all the bits after it to 0 and all the ones before it to 1.

				// So for example 1100 should become 0111. The transformation logic looks like this:
				// Find first non-zero bit with tzcnt and build a mask -> 0100
				// Left shift by one -> 1000
				// Subtract 1 -> 0111

				int boolMask = math.bitmask(isFullyCoveredByCascade4);
				isFullyCoveredByCascadeMask = 1 << math.tzcnt(boolMask);
				isFullyCoveredByCascadeMask = isFullyCoveredByCascadeMask << 1;
				isFullyCoveredByCascadeMask = isFullyCoveredByCascadeMask - 1;
			}
#endif

			return math.bitmask(doCirclesOverlap4 & isBehindCascade4) & isFullyCoveredByCascadeMask;
		}

		// Keep non-SIMD version around for debugging and validation purposes.
		int CullNonSIMD (AABB aabb) {
			// This test has been ported from the corresponding test done by Unity's built in shadow culling.

			float casterRadius = math.length(aabb.Extents);

			float3 lsCasterCenter = TransformToLightSpace(aabb.Center, LightAxisX, LightAxisY, LightAxisZ);
			float2 lsCasterCenterXY = new float2(lsCasterCenter.x, lsCasterCenter.y);

#if !DISABLE_SHADOW_CULLING_CAPSULE_TEST
			float3 shadowCapsuleBegin;
			float3 shadowCapsuleEnd;
			float shadowCapsuleRadius;
			ComputeShadowCapsule(LightAxisZ, aabb.Center, casterRadius, ShadowFrustumPlanes,
				out shadowCapsuleBegin, out shadowCapsuleEnd, out shadowCapsuleRadius);
#endif

			int visibleSplitMask = 0;

			for (int i = 0; i < NumSplits; i++) {
				float receiverSphereRadius = ReceiverSphereRadius4[i];
				float3 lsReceiverSphereCenter = new float3(LSReceiverSphereCenterX4[i], LSReceiverSphereCenterY4[i], LSReceiverSphereCenterZ4[i]);
				float2 lsReceiverSphereCenterXY = new float2(lsReceiverSphereCenter.x, lsReceiverSphereCenter.y);

				// A spherical caster casts a cylindrical shadow volume. In XY in light space this ends up being a circle/circle intersection test.
				// Thus we first check if the caster bounding circle is at least partially inside the cascade circle.
				float lsCasterToReceiverSphereDistanceSq = math.lengthsq(lsCasterCenterXY - lsReceiverSphereCenterXY);
				float combinedRadius = casterRadius + receiverSphereRadius;
				float combinedRadiusSq = combinedRadius * combinedRadius;

				// If the 2D circles intersect, then the caster is potentially visible in the cascade.
				// If they don't intersect, then there is no way for the caster to cast a shadow that is
				// visible inside the circle.
				// Casters that intersect the circle but are behind the receiver sphere also don't cast shadows.
				// We don't consider that here, since those casters should be culled out by the receiver
				// plane culling.
				if (lsCasterToReceiverSphereDistanceSq <= combinedRadiusSq) {
					float2 lsCasterToReceiverSphereXY = lsCasterCenterXY - lsReceiverSphereCenterXY;
					float2 lsCasterToReceiverSphereSqXY = lsCasterToReceiverSphereXY * lsCasterToReceiverSphereXY;

					// If in light space the shadow caster is behind the current cascade sphere then it can't cast a shadow on it and we can skip it.
					// sphere equation is (x - x0)^2 + (y - y0)^2 + (z - z0)^2 = R^2 and we are looking for the farthest away z position
					// thus zMaxInLightSpace = z0 + Sqrt(R^2 - (x - x0)^2 - (y - y0)^2 )). R being Cascade + caster radius.
					float lsZMaxAccountingForCasterRadius = lsReceiverSphereCenter.z + math.sqrt(combinedRadiusSq - lsCasterToReceiverSphereSqXY.x - lsCasterToReceiverSphereSqXY.y);
					if (lsCasterCenter.z > lsZMaxAccountingForCasterRadius) {
						// This is equivalent (but cheaper) than : if (!IntersectCapsuleSphere(shadowVolume, cascades[cascadeIndex].outerSphere))
						// As the shadow volume is defined as a capsule, while shadows receivers are defined by a sphere (the cascade split).
						// So if they do not intersect there is no need to render that shadow caster for the current cascade.
						continue;
					}

					visibleSplitMask |= 1 << i;

#if !DISABLE_SHADOW_CULLING_CAPSULE_TEST
					float3 receiverSphereCenter = new float3(ReceiverSphereCenterX4[i], ReceiverSphereCenterY4[i], ReceiverSphereCenterZ4[i]);
					float coreSphereRadius = CoreSphereRadius4[i];

					// Next step is to detect if the shadow volume is fully covered by the cascade. If so we can avoid rendering all other cascades
					// as we know that in the case of cascade overlap, the smallest cascade index will always prevail. This help as cascade overlap is usually huge.
					if (IsCapsuleInsideSphere(shadowCapsuleBegin, shadowCapsuleEnd, shadowCapsuleRadius, receiverSphereCenter, coreSphereRadius)) {
						// Ideally we should test against the union of all cascades up to this one, however in a lot of cases (cascade configuration + light orientation)
						// the overlap of current and previous cascades is a super set of the union of these cascades. Thus testing only the previous cascade does
						// not create too much overestimation and the math is simpler.
						break;
					}
#endif
				}
			}

			return visibleSplitMask;
		}

		static void ComputeShadowCapsule (float3 lightDirection, float3 casterPosition, float casterRadius, UnsafeList<Plane> shadowFrustumPlanes,
			out float3 shadowCapsuleBegin, out float3 shadowCapsuleEnd, out float shadowCapsuleRadius) {
			float shadowCapsuleLength = GetShadowVolumeLengthFromCasterAndFrustumAndLightDir(lightDirection,
				casterPosition,
				casterRadius,
				shadowFrustumPlanes);

			shadowCapsuleBegin = casterPosition;
			shadowCapsuleEnd = casterPosition + shadowCapsuleLength * lightDirection;
			shadowCapsuleRadius = casterRadius;
		}

		static float GetShadowVolumeLengthFromCasterAndFrustumAndLightDir (float3 lightDir, float3 casterPosition, float casterRadius, UnsafeList<Plane> planes) {
			// The idea here is to find the capsule that goes from the caster and cover all possible shadow receiver in the frustum.
			// First we find the distance from the caster center to the frustum
			var casterRay = new Ray(casterPosition, lightDir);
			int planeIndex;
			float distFromCasterToFrustumInLightDirection = RayDistanceToFrustumOriented(casterRay, planes, out planeIndex);
			if (planeIndex == -1) {
				// Shadow caster center is outside of frustum and ray do not intersect it.
				// Shadow volume is thus the caster bounding sphere.
				return 0;
			}

			// Then we need to account for the radius of the capsule.
			// The distance returned might actually be too large in the case of a caster outside of the frustum
			// however detecting this would require to run another RayDistanceToFrustum and the case is rare enough
			// so its not a problem (these caster will just be less likely to be culled away).
			Assert.IsTrue(planeIndex >= 0 && planeIndex < planes.Length);

			float distFromCasterToPlane = math.abs(planes[planeIndex].GetDistanceToPoint(casterPosition));
			float sinAlpha = distFromCasterToPlane / (distFromCasterToFrustumInLightDirection + 0.0001f);
			float tanAlpha = sinAlpha / (math.sqrt(1.0f - (sinAlpha * sinAlpha)));
			distFromCasterToFrustumInLightDirection += casterRadius / (tanAlpha + 0.0001f);

			return distFromCasterToFrustumInLightDirection;
		}

		// Returns the shortest distance to the front facing plane from the ray.
		// Return -1 if no plane intersect this ray.
		// planeNumber will contain the index of the plane found or -1.
		static float RayDistanceToFrustumOriented (Ray ray, UnsafeList<Plane> planes, out int planeNumber) {
			planeNumber = -1;
			float maxDistance = float.PositiveInfinity;
			for (int i = 0; i < planes.Length; ++i) {
				float distance;
				if (IntersectRayPlaneOriented(ray, planes[i], out distance) && distance < maxDistance) {
					maxDistance = distance;
					planeNumber = i;
				}
			}

			return planeNumber != -1 ? maxDistance : -1.0f;
		}

		static bool IntersectRayPlaneOriented (Ray ray, Plane plane, out float distance) {
			distance = 0f;

			float vdot = math.dot(ray.direction, plane.normal);
			float ndot = -math.dot(ray.origin, plane.normal) - plane.distance;

			// No collision if the ray it the plane from behind
			if (vdot > 0)
				return false;

			// is line parallel to the plane? if so, even if the line is
			// at the plane it is not considered as intersection because
			// it would be impossible to determine the point of intersection
			if (Mathf.Approximately(vdot, 0.0F))
				return false;

			// the resulting intersection is behind the origin of the ray
			// if the result is negative ( enter < 0 )
			distance = ndot / vdot;

			return distance > 0.0F;
		}

		static bool IsInsideSphere (BoundingSphere sphere, BoundingSphere containingSphere) {
			if (sphere.radius >= containingSphere.radius)
				return false;

			float squaredDistance = math.lengthsq(containingSphere.position - sphere.position);
			float radiusDelta = containingSphere.radius - sphere.radius;
			float squaredRadiusDelta = radiusDelta * radiusDelta;

			return squaredDistance < squaredRadiusDelta;
		}

		static bool4 IsInsideSphereSIMD (float4 sphereCenterX, float4 sphereCenterY, float4 sphereCenterZ, float4 sphereRadius,
			float4 containingSphereCenterX, float4 containingSphereCenterY, float4 containingSphereCenterZ, float4 containingSphereRadius) {
			float4 dx = containingSphereCenterX - sphereCenterX;
			float4 dy = containingSphereCenterY - sphereCenterY;
			float4 dz = containingSphereCenterZ - sphereCenterZ;

			float4 squaredDistance = dx * dx + dy * dy + dz * dz;
			float4 radiusDelta = containingSphereRadius - sphereRadius;
			float4 squaredRadiusDelta = radiusDelta * radiusDelta;

			bool4 canSphereFit = sphereRadius < containingSphereRadius;
			bool4 distanceTest = squaredDistance < squaredRadiusDelta;

			return canSphereFit & distanceTest;
		}

		static bool IsCapsuleInsideSphere (float3 capsuleBegin, float3 capsuleEnd, float capsuleRadius, float3 sphereCenter, float sphereRadius) {
			var sphere = new BoundingSphere(sphereCenter, sphereRadius);
			var beginPoint = new BoundingSphere(capsuleBegin, capsuleRadius);
			var endPoint = new BoundingSphere(capsuleEnd, capsuleRadius);

			return IsInsideSphere(beginPoint, sphere) && IsInsideSphere(endPoint, sphere);
		}

		static bool4 IsCapsuleInsideSphereSIMD (float3 capsuleBegin, float3 capsuleEnd, float capsuleRadius,
			float4 sphereCenterX, float4 sphereCenterY, float4 sphereCenterZ, float4 sphereRadius) {
			float4 beginSphereX = capsuleBegin.xxxx;
			float4 beginSphereY = capsuleBegin.yyyy;
			float4 beginSphereZ = capsuleBegin.zzzz;

			float4 endSphereX = capsuleEnd.xxxx;
			float4 endSphereY = capsuleEnd.yyyy;
			float4 endSphereZ = capsuleEnd.zzzz;

			float4 capsuleRadius4 = new float4(capsuleRadius);

			bool4 isInsideBeginSphere = IsInsideSphereSIMD(beginSphereX, beginSphereY, beginSphereZ, capsuleRadius4,
				sphereCenterX, sphereCenterY, sphereCenterZ, sphereRadius);

			bool4 isInsideEndSphere = IsInsideSphereSIMD(endSphereX, endSphereY, endSphereZ, capsuleRadius4,
				sphereCenterX, sphereCenterY, sphereCenterZ, sphereRadius);

			return isInsideBeginSphere & isInsideEndSphere;
		}

		static float3 TransformToLightSpace (float3 positionWS, float3 lightAxisX, float3 lightAxisY, float3 lightAxisZ) => new float3(
			math.dot(positionWS, lightAxisX),
			math.dot(positionWS, lightAxisY),
			math.dot(positionWS, lightAxisZ));

		static unsafe UnsafeList<Plane> GetUnsafeListView (NativeArray<Plane> array, int start, int length) {
			NativeArray<Plane> subArray = array.GetSubArray(start, length);
			return new UnsafeList<Plane>((Plane*)subArray.GetUnsafeReadOnlyPtr(), length);
		}
	}
}
