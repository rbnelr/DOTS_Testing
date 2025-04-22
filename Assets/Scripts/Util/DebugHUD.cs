using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using static UnityEngine.Rendering.DebugUI.MessageBox;

#if true
[DefaultExecutionOrder(-900)]
public class DebugHUD : MonoBehaviour {
	static DebugHUD inst;

	void OnEnable () {
		inst = this;
	}

	List<string> lines = new List<string>();

	void Update () { // clear before other script Update() Show() lines
		lines.Clear();
	}

	public static void Show (string msg) {
		inst.lines.Add(msg);
	}
	void OnGUI () {
		var style = new GUIStyle();
		style.normal = new GUIStyleState();
		style.normal.textColor = Color.black;
		style.fontSize = 20;
		style.normal.background = create_texture(1,1, new Color(0, 0, 0, 0.3f));

		foreach (var line in lines) {
			GUILayout.Label(line, style);
		}
	}

	private Texture2D create_texture (int width, int height, Color color){
		Texture2D texture = new Texture2D(width, height);
		Color[] pixels = new Color[width * height];
		for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
		texture.SetPixels(pixels);
		texture.Apply();
		return texture;
	}
}
#else
[DefaultExecutionOrder(-900)]
[BurstCompile]
public class DebugHUD : MonoBehaviour {

	public static readonly SharedStatic<NativeList<FixedString128Bytes>> lines = SharedStatic<NativeList<FixedString128Bytes>>.GetOrCreate<DebugHUD, ListFieldKey>();
	private class ListFieldKey {}

	//static NativeList<FixedString128Bytes> lines;
	void Awake () {
		lines.Data = new NativeList<FixedString128Bytes>(Allocator.Persistent);
	}
	void OnEnable () {
		lines.Data.Dispose();
		lines.Data = new NativeList<FixedString128Bytes>(Allocator.Persistent);
	}

	public static void Show (in FixedString128Bytes msg) {
		lines.Data.Add(msg);
	}
	public static void Show (string msg) {
		lines.Data.Add(msg);
	}
	void OnGUI () {
		var style = new GUIStyle();
		style.normal = new GUIStyleState();
		style.normal.textColor = Color.black;
		style.fontSize = 20;
		style.normal.background = create_texture(1,1, new Color(0, 0, 0, 0.3f));

		foreach (var line in lines.Data) {
			GUILayout.Label(line.ToString(), style);
		}

		lines.Data.Clear();
	}

	private Texture2D create_texture (int width, int height, Color color){
		Texture2D texture = new Texture2D(width, height);
		Color[] pixels = new Color[width * height];
		for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
		texture.SetPixels(pixels);
		texture.Apply();
		return texture;
	}
}
#endif
