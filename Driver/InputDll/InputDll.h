#ifndef INPUT_DLL_H
#define INPUT_DLL_H

#ifdef __cplusplus
extern "C" {
#endif

// Keyboard functions
__declspec(dllexport) void SendKeyDown(unsigned char vkCode);
__declspec(dllexport) void SendKeyUp(unsigned char vkCode);

__declspec(dllexport) void SendKeyDownHid(unsigned char hidUsage);
__declspec(dllexport) void SendKeyUpHid(unsigned char hidUsage);

__declspec(dllexport) void SendKeyRepeatHid(unsigned char hidUsage);

__declspec(dllexport) void SendModifierKeyDown(unsigned char modifierBit);
__declspec(dllexport) void SendModifierKeyUp(unsigned char modifierBit);

__declspec(dllexport) void SendMediaKey(unsigned char action);

// Mouse functions
__declspec(dllexport) void MouseMoveRelative(int dx, int dy);

__declspec(dllexport) void MouseButtonDown(unsigned char button);
__declspec(dllexport) void MouseButtonDownUp(unsigned char button);

__declspec(dllexport) void MouseScrollVertical(int delta);
__declspec(dllexport) void MouseScrollHorizontal(int delta);

#ifdef __cplusplus
}
#endif

#endif