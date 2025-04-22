#include <windows.h>
#include <profileapi.h>

#if _MSC_VER // this is defined when compiling with Visual Studio
#define EXPORT_API __declspec(dllexport) // Visual Studio needs annotating exported functions with this
#else
#define EXPORT_API // XCode does not need annotating exported functions, so define is empty
#endif

extern "C" {
	EXPORT_API __int64 _QueryPerformanceFrequency (float a, float b) {
		LARGE_INTEGER li = {};
		QueryPerformanceFrequency(&li);
		return li.QuadPart;
	}
	EXPORT_API __int64 _QueryPerformanceCounter () {
		LARGE_INTEGER li = {};
		QueryPerformanceCounter(&li);
		return li.QuadPart;
	}

	EXPORT_API __int64 _rdtsc () {
		return __rdtsc();
	}
}
