using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

[BurstCompile]
public class NativeTimer {

	[DllImport("NativeTimer")]
	private static extern long _QueryPerformanceFrequency ();

	[DllImport("NativeTimer")]
	private static extern long _QueryPerformanceCounter ();

	[DllImport("NativeTimer")]
	private static extern long _rdtsc ();


	//public static long GetTimestamp () {
	//	return _QueryPerformanceCounter();
	//}

	public static void test () {
		
		var freq = _QueryPerformanceFrequency();
		var qpc0 = _QueryPerformanceCounter();
		var ts0 = _rdtsc();

		var a = _QueryPerformanceCounter();
		var b = _QueryPerformanceCounter();
		var c = _QueryPerformanceCounter();
		var d = _QueryPerformanceCounter();

		const int count = 1000;
		for (int i=0; i<count-4; ++i) {
			var e = _QueryPerformanceCounter();
		}

		var ts1 = _rdtsc();
		var qpc1 = _QueryPerformanceCounter();

		var qpc_time = (float)(qpc1 - qpc0) / count / freq * 1000*1000*1000; // in nanoseconds
		var ts_time = (ts1 - ts0) / count;

		Debug.Log($"{b - a} {c - b} {d - c}");
		Debug.Log($"Est. QueryPerformanceCounter overhead: QPC: {qpc_time} ns rdtsc: {ts_time} cycles");
	}
}

/*
// Inspired by ThreadLocalAllocator from https://github.com/needle-mirror/com.unity.entities.graphics/blob/master/Unity.Entities.Graphics/DrawCommandGeneration.cs
// TODO: why use FieldOffset(16)?
[StructLayout(LayoutKind.Explicit, Size = JobsUtility.CacheLineSize)]
public unsafe struct PerThreadJobTiming {
	//public bool Started; // Somehow avoid calling QPC for every chunk with branching? How do we know we are in the last iteration for the thread?
	public long TotalTime;
}

[BurstCompile]
unsafe partial struct UpdateSpatialGridJob : IJobChunk {
	

	public NativeArray<PerThreadJobTiming> ThreadTimings;

	[NativeSetThreadIndex] public int ThreadIndex;

	public void Execute (in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask) {
		var _time_start = NativeTimer._QueryPerformanceCounter();

		// ...

		ThreadTimings[TotalTime] += NativeTimer._QueryPerformanceCounter() - _time_start;
	}
}

https://discord.com/channels/489222168727519232/1362116160455311471/1362153547088789796
https://gitlab.com/tertle/com.bovinelabs.core/-/blob/master/BovineLabs.Core/Jobs/IJobChunkWorkerBeginEnd.cs?ref_type=heads#L22

We can actually add a custom base job type which would allow me to add timing calls to any custom written jobs
(although not IJobEntity as they use codegen, and no externally written jobs like entities.graphics)
In fact any scheduled job runs exactly once per thread (internally grabbing chunks from a queue(?) and only finishing to the next job when all chunks are grabbed)
So by simply called QPC at the start and end of this function (via the callback) and storing the time difference in a per-thread buffer
then adding a method to sum these later on the main thread would be perfect
*/