using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public class ShowConsoleWindow : MonoBehaviour {
	public bool OnlyInDebugBuilds = true;

	bool ShowConsole => !Application.isEditor && (OnlyInDebugBuilds ? Debug.isDebugBuild : true);

#if UNITY_STANDALONE_WIN
	void Awake () {
		Application.logMessageReceived += HandleLog;

		if (ShowConsole)
			ConsoleWindowUtil.Show();
	}

	void HandleLog (string logString, string stackTrace, LogType type) {
		Console.WriteLine(logString);
	}

	void OnDestroy () {
		if (ShowConsole)
			ConsoleWindowUtil.Hide();
	}
#endif
}

#if UNITY_STANDALONE_WIN
public class ConsoleWindowUtil {
	[DllImport("kernel32.dll")]
	static extern IntPtr GetConsoleWindow ();

	[DllImport("user32.dll")]
	static extern bool ShowWindow (IntPtr hWnd, int nCmdShow);

	// Import necessary DLL functions
	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	static extern bool AllocConsole ();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);
	
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool SetStdHandle (int nStdHandle, IntPtr hHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern IntPtr GetStdHandle (int nStdHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern IntPtr CreateFile (
		string lpFileName,
		uint dwDesiredAccess,
		uint dwShareMode,
		IntPtr lpSecurityAttributes,
		uint dwCreationDisposition,
		uint dwFlagsAndAttributes,
		IntPtr hTemplateFile
	);
	
	const int SW_HIDE = 0;
	const int SW_SHOW = 5;
	const uint GENERIC_READ = 0x80000000;
	const uint GENERIC_WRITE = 0x40000000;
	const uint FILE_SHARE_READ = 0x00000001;
	const uint FILE_SHARE_WRITE = 0x00000002;
	const uint OPEN_EXISTING = 3;
	const int STD_OUTPUT_HANDLE = -11;
	const int STD_INPUT_HANDLE = -10;
	const int STD_ERROR_HANDLE = -12;

	public static bool EnsureConsoleWindow (bool AttachToParentConsole) {
		// Open existing console)
		if (ShowWindow(GetConsoleWindow(), SW_SHOW))
			return true;
		
		// or attach to parent process (if launched from cmd ; but if build and run from unity we don't get a console!
		if (AttachToParentConsole && AttachConsole(-1))
			return true;
		
		// or create console (if launched via double click)
		return AllocConsole();
	}

	public static void Show (bool AttachToParentConsole=false) {
		EnsureConsoleWindow(AttachToParentConsole);

		// Attach console messages because these are broken for some reason
		IntPtr consoleOutputHandle = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (consoleOutputHandle != IntPtr.Zero) {
			SetStdHandle(STD_OUTPUT_HANDLE, consoleOutputHandle);
			Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
		}

		IntPtr consoleInputHandle = CreateFile("CONIN$", GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (consoleInputHandle != IntPtr.Zero) {
			SetStdHandle(STD_INPUT_HANDLE, consoleInputHandle);
		}

		IntPtr consoleErrorHandle = CreateFile("CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (consoleErrorHandle != IntPtr.Zero) {
			SetStdHandle(STD_ERROR_HANDLE, consoleErrorHandle);
			Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
		}
	}
	public static void Hide () {
		ShowWindow(GetConsoleWindow(), SW_HIDE);
	}
}
#endif
