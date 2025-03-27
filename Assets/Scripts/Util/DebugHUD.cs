using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using static UnityEngine.Rendering.DebugUI.MessageBox;

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
