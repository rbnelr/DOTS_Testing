using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera))]
public class Flycam : MonoBehaviour {
	
	//public float3 position => GetComponent<Camera>().transform.position;
	//public Quaternion rotation => GetComponent<Camera>().transform.rotation;

	// CCW  0=north (+Z)  90=east(+X)  (compass)
	[Range(-180, 180)]
	public float azimuth = 0;
	// 0=looking at horizon  90=looking up  (flight HUDs)
	[Range(-90, 90)]
	public float elevation = 0;
	// 0=normal  90=camera rolled towards right
	[Range(-180, 180)]
	public float roll = 0;

	public float base_speed = 10;

	float max_speed = 1000000.0f;
	float speedup_factor = 2;

	float cur_speed = 0;
	string CurrentSpeed => $"{cur_speed}";
	
	public bool planar_move = false;
	
#region FOV
	// smoothed fov while gracefully handling public API and inspector

	// get: real fov
	// set: set real fov without smoothing
	public float fov {
		get => GetComponent<Camera>().fieldOfView;
		set {
			GetComponent<Camera>().fieldOfView = value;
			fov_target = value;
			fov_velocity = 0;
		}
	}
	void hard_set_fov () { fov = fov_target; }

	// serialize fov target (since we can't serialize/inspect properties)
	// on inspector modification don't smooth
	[SerializeField, InspectorName("FOV"), Range(0.1f, 179.0f)]
	public float fov_target = 70;
	
	public float fov_min = 0.1f;
	public float fov_max = 179;
	public float fov_smoothing = 0.1f;
	float fov_velocity;
	float default_fov;
#endregion
	
	private void Awake () {
		fov = fov_target; // don't smooth fov after deserialize
		default_fov = fov; // store fov to allow reset to default
	}

#region controls
	[Header("Controls")]
	public float look_mouse_sensitivity = 1;
	public float look_gamepad_sensitivity = 1;

	public float roll_speed = 45;
	
	// lock and make cursor invisible
	public bool lock_cursor = false;

	static float2 get_WASD () { // unnormalized
		float2 dir = 0;
		dir.y += Keyboard.current.sKey.isPressed ? -1.0f : 0.0f;
		dir.y += Keyboard.current.wKey.isPressed ? +1.0f : 0.0f;
		dir.x += Keyboard.current.aKey.isPressed ? -1.0f : 0.0f;
		dir.x += Keyboard.current.dKey.isPressed ? +1.0f : 0.0f;
		return dir;
	}
	static float get_QE () {
		float val = 0;
		val += Keyboard.current.qKey.isPressed ? -1.0f : 0.0f;
		val += Keyboard.current.eKey.isPressed ? +1.0f : 0.0f;
		return val;
	}
	
	bool manual_look => Mouse.current.middleButton.isPressed;
	bool change_fov => Keyboard.current.fKey.isPressed;
	float scroll_delta => Mouse.current.scroll.ReadValue().y;

	float2 get_mouse_look_delta () {
		float2 look = Mouse.current.delta.ReadValue();
		
		// Apply sensitivity and scale by FOV
		// scaling by FOV might not be wanted in all situations (180 flick in a shooter would need new muscle memory with other fov)
		// but usually muscle memory for flicks are supposedly based on offsets on screen, which do scale with FOV, so FOV scaled sens seems to be better
		// look_sensitivity is basically  screen heights per 100 mouse dots, where dots are moved mouse distance in inch * mouse dpi
		look *= 0.001f * look_mouse_sensitivity * fov;
		
		if (lock_cursor || manual_look) {
			return look;
		}
		return 0;
	}
	
	static float2 get_left_stick () => Gamepad.current?.leftStick.ReadValue() ?? float2(0);
	static float2 get_right_stick () => Gamepad.current?.rightStick.ReadValue() ?? float2(0);
	static float get_gamepad_up_down () {
		float value = 0;
		value += Gamepad.current?.leftShoulder.ReadValue() ?? 0;
		value -= Gamepad.current?.leftTrigger.ReadValue() ?? 0;
		return value;
	}

	float2 get_look_delta () {
		float2 look = get_mouse_look_delta();
		if (look.x == 0 && look.y == 0)
			look = get_right_stick() * look_gamepad_sensitivity * 100 * Time.deltaTime;
		return look;
	}
	float2 get_move2d () {
		float2 move2d = normalizesafe(get_WASD());
		if (move2d.x == 0 && move2d.y == 0)
			move2d = get_left_stick();
		return move2d;
	}
	float3 get_move3d () {
		float3 move3d = float3(get_WASD(), get_QE());
		move3d = normalizesafe(move3d);

		if (lengthsq(move3d) == 0)
			move3d = float3(get_left_stick(), get_gamepad_up_down()); // stick is analog, up down via buttons is digital, how to normalize, if at all?

		return float3(move3d.x, move3d.z, move3d.y); // swap to unity Y-up
	}

	void update_lock_cursor () {
		bool toggle_lock = Keyboard.current.f2Key.wasPressedThisFrame;

		if (toggle_lock) {
			lock_cursor = !lock_cursor;
		}
		if (lock_cursor && !Application.isFocused)
			lock_cursor = false; // enforce unlock when alt-tab

		if (lock_cursor) {
			Cursor.visible = false;
			Cursor.lockState = CursorLockMode.Locked;
		}
		else {
			Cursor.visible = true;
			if (manual_look) {
				Cursor.lockState = CursorLockMode.Confined;
			}
			else {
				Cursor.lockState = CursorLockMode.None;
			}
		}
	}

	private void OnDisable () {
		if (lock_cursor) {
			lock_cursor = false;
			update_lock_cursor();
		}
	}
#endregion

	void LateUpdate () {
		update_lock_cursor();

		//float2 move2d = get_move2d();
		//float roll_dir = get_QE();

		float3 move3d = get_move3d();
		float roll_dir = 0;

		float2 look_delta = get_look_delta();

		{ //// speed or fov change with mousewheel
			if (!change_fov) { // scroll changes base speed
				float log = log2(base_speed);
				log += 0.1f * scroll_delta;
				base_speed = clamp(pow(2.0f, log), 0.001f, max_speed);
			}
			else { // F+scroll changes fov
				float log = log2(fov_target);
				log -= 0.1f * scroll_delta;
				fov_target = clamp(pow(2.0f, log), fov_min, fov_max);
				
				if (Keyboard.current.leftShiftKey.isPressed && scroll_delta != 0) // shift+F+scroll resets fov
					fov = default_fov;
			}

			// smooth fov to fov_target
			GetComponent<Camera>().fieldOfView = Mathf.SmoothDamp(fov, fov_target, ref fov_velocity, fov_smoothing, float.PositiveInfinity, Time.unscaledDeltaTime);
		}

		{ //// Camera rotation
			azimuth += look_delta.x;
			azimuth = (azimuth + 180.0f) % 360.0f - 180.0f;

			elevation += look_delta.y;
			elevation = clamp(elevation, -90.0f, +90.0f);

			roll += roll_dir * roll_speed * Time.unscaledDeltaTime;
			roll = (roll + 180.0f) % 360.0f - 180.0f;

			transform.rotation = Quaternion.Euler(-elevation, azimuth, -roll);
		}
		
		{ //// movement
			if (lengthsq(move3d) == 0.0f)
				cur_speed = base_speed; // no movement resets speed

			if (Keyboard.current.leftShiftKey.isPressed) {
				cur_speed += base_speed * speedup_factor * Time.unscaledDeltaTime;
			}

			cur_speed = clamp(cur_speed, base_speed, max_speed);

			float3 move_delta = cur_speed * move3d * Time.unscaledDeltaTime;

			if (planar_move) {
				transform.position += Quaternion.AngleAxis(azimuth, Vector3.up) * move_delta;
			}
			else {
				transform.position += transform.TransformDirection(move_delta);
			}
		}
	}
}
