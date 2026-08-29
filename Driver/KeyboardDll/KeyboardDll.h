#ifndef KEYBOARD_DLL_H
#define KEYBOARD_DLL_H

#ifdef __cplusplus
extern "C" {
#endif

__declspec(dllexport) void SendKeyDown(unsigned char vkCode);
__declspec(dllexport) void SendKeyUp(unsigned char vkCode);

__declspec(dllexport) void SendModifierKeyDown(unsigned char modifierBit);
__declspec(dllexport) void SendModifierKeyUp(unsigned char modifierBit);

__declspec(dllexport) void SendMediaKey(unsigned char action);

#ifdef __cplusplus
}
#endif

#endif