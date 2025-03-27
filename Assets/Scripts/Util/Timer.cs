using System;
using UnityEngine;

// Timer use like:
// Manual Start/Stop
// 1. var t = new Timer(); ... float duration = t.Stop();
// 2. var t = Timer.Start(); ... float duration = t.Stop();
// Scoped Start/Stop
// 3. using (Timer.Start(d => duration = d)) { ... }
// 4. using (Timer.Start(d => duration_total += d)) { ... }
public struct Timer : IDisposable {
	double start;
	Action<float> set_result;
	
	// Starts timer (Manual Stop)
	public static Timer Start () {
		return new Timer {
			start = Time.realtimeSinceStartupAsDouble,
			set_result = null
		};
	}

	// Starts timer, (auto Stop on out of scope, will call set_result)
	public static Timer Start (Action<float> set_result) {
		return new Timer {
			start = Time.realtimeSinceStartupAsDouble,
			set_result = set_result
		};
	}
	
	// manually stops timer, result will be written if set_result != null, afterwards set_result=null
	public float Stop () {
		float duration = (float)(Time.realtimeSinceStartupAsDouble - start);
		if (set_result != null) {
			set_result(duration);
			set_result = null;
		}
		return duration;
	}

	// Automatically called if used as  using (var t=ScopeTimer.Start(d => duration = d)) {
	public void Dispose () {
		if (set_result != null) {
			float duration = (float)(Time.realtimeSinceStartupAsDouble - start);
			set_result(duration);
			set_result = null;
		}
	}
}
