using UnityEngine;
using System.Collections;

[DefaultExecutionOrder(99)]
public class Fps : MonoBehaviour {
	public float update_frequency = 10;
	float update_period => 1.0f / update_frequency;

	TimedAverage display_fps = new TimedAverage();

	void Update () {
		GUI.depth = 2;
		
		display_fps.push(Time.unscaledDeltaTime);
		display_fps.update(update_period);

		DebugHUD.Show($"FPS: {1f/display_fps.cur_result.mean, 6:###0.0} (min: {1f/display_fps.cur_result.max, 6:###0.0})");
	}
}
