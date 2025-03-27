using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;

public class GO_Spawner : MonoBehaviour {
	
	public GameObject prefab;

	int Count = 0;
	
	[Range(0.0f, 1.0f)]
	public float Ratio = 0.1f;

	public int Max = 100000;
	public int2 Tiling = int2(50, 50);

	public float Spacing = 3;

	int SpawnCount => (int)ceil(Max * Ratio);

	public float LODBias = 2;

	void Spawn (int idx) {
		
		int x = idx % Tiling.x;
		idx        /= Tiling.x;

		int z = idx % Tiling.y;
		idx        /= Tiling.y;

		int y = idx;

		float3 pos = float3(x,1+y,z) * Spacing;

		Instantiate(prefab, pos, Quaternion.identity, transform);
	}

	void SpawnAll () {
		for (int i=0; i<SpawnCount; ++i) {
			Spawn(i);
		}
		Count = SpawnCount;
	}
	void DestroyAll () {
		foreach (Transform c in transform) {
			Destroy(c.gameObject);
		}
		Count = 0;
	}

	void Update () {
		if (Count != SpawnCount) {
			DestroyAll();
			SpawnAll();
		}

		QualitySettings.lodBias = 2.0f * LODBias;
	}

	void OnDisable () {
		DestroyAll();
	}
}
