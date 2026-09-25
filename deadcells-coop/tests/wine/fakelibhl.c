/* Stands in for libhl.dll: like the real one it imports winmm.dll at load time. */
#include <windows.h>
#include <mmsystem.h>

__declspec(dllexport) unsigned fake_hl_time(void) { return timeGetTime(); }
