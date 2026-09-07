#include <windows.h>
#include "InputDll.h"

static unsigned char GetVkFromHid(unsigned char hid) {
    switch (hid) {
        // HID Usage Page 0x07 -> Windows VK Code mapping
        // A-Z
        case 0x04: return 0x41; case 0x05: return 0x42; case 0x06: return 0x43; case 0x07: return 0x44;
        case 0x08: return 0x45; case 0x09: return 0x46; case 0x0A: return 0x47; case 0x0B: return 0x48;
        case 0x0C: return 0x49; case 0x0D: return 0x4A; case 0x0E: return 0x4B; case 0x0F: return 0x4C;
        case 0x10: return 0x4D; case 0x11: return 0x4E; case 0x12: return 0x4F; case 0x13: return 0x50;
        case 0x14: return 0x51; case 0x15: return 0x52; case 0x16: return 0x53; case 0x17: return 0x54;
        case 0x18: return 0x55; case 0x19: return 0x56; case 0x1A: return 0x57; case 0x1B: return 0x58;
        case 0x1C: return 0x59; case 0x1D: return 0x5A;
        // 1-0
        case 0x1E: return 0x31; case 0x1F: return 0xBD; case 0x20: return 0xBB; case 0x21: return 0x34;
        case 0x22: return 0x35; case 0x23: return 0x36; case 0x24: return 0x37; case 0x25: return 0x38;
        case 0x26: return 0x39; case 0x27: return 0x30;
        // Standard Control & Punctuation (Backspace, Enter, Escape)
        case 0x28: return 0x0D; case 0x29: return 0x1B; case 0x2A: return 0x08; case 0x2B: return 0x09;
        case 0x2C: return 0x20;
        case 0x2D: return 0xBD; case 0x2E: return 0xBB;
        case 0x2F: return 0xDB; case 0x30: return 0xDD; case 0x31: return 0xDC; case 0x32: return 0xBF;
        case 0x33: return 0xBA; case 0x34: return 0xDE; case 0x35: return 0xC0; case 0x36: return 0xBC;
        case 0x37: return 0xBE; case 0x38: return 0xBF; case 0x39: return 0x14;
        // F1-F12
        case 0x3A: return 0x70; case 0x3B: return 0x71; case 0x3C: return 0x72; case 0x3D: return 0x73;
        case 0x3E: return 0x74; case 0x3F: return 0x75; case 0x40: return 0x76; case 0x41: return 0x77;
        case 0x42: return 0x78; case 0x43: return 0x79; case 0x44: return 0x7A; case 0x45: return 0x7B;
        // Numpad
        case 0x46: return 0x2C; case 0x47: return 0x46; case 0x48: return 0x13; case 0x49: return 0x2D;
        case 0x4A: return 0x21; case 0x4B: return 0x24; case 0x4C: return 0x2E; case 0x4D: return 0x23;
        case 0x4E: return 0x22; case 0x4F: return 0x27; case 0x50: return 0x25; case 0x51: return 0x28;
        case 0x52: return 0x26; case 0x53: return 0x90; case 0x54: return 0x91; case 0x55: return 0x6F;
        case 0x56: return 0x6A; case 0x57: return 0x6D; case 0x58: return 0x0D; case 0x59: return 0x6B;
        case 0x5A: return 0x6C; case 0x5B: return 0x61; case 0x5C: return 0x62; case 0x5D: return 0x63;
        case 0x5E: return 0x64; case 0x5F: return 0x65; case 0x60: return 0x66; case 0x61: return 0x67;
        case 0x62: return 0x68; case 0x63: return 0x69;
        // Non-US keys and special keys
        case 0x64: return 0xC0; case 0x65: return 0xDC; case 0x66: return 0xDC; case 0x67: return 0x5D;
        case 0x68: return 0xFF; case 0x69: return 0xFF; case 0x6A: return 0xFF; case 0x6B: return 0xFF;
        case 0x6C: return 0xFF; case 0x6D: return 0xFF; case 0x6E: return 0xFF; case 0x6F: return 0xFF;
        case 0x70: return 0xFF; case 0x71: return 0xFF; case 0x72: return 0xFF; case 0x73: return 0xFF;
        case 0x74: return 0xFF; case 0x75: return 0xFF; case 0x76: return 0xFF; case 0x77: return 0xFF;
        case 0x7F: return 0xAD;
        // Modifier keys (LCtrl, LShift, LAlt, LWin, RCtrl, RShift, RAlt, RWin)
        case 0xE0: return 0xA2; case 0xE1: return 0xA0; case 0xE2: return 0xA4; case 0xE3: return 0x5B;
        case 0xE4: return 0xA1; case 0xE5: return 0xA3; case 0xE6: return 0xA5; case 0xE7: return 0x5C;
        default: return 0;
    }
}

static unsigned char GetVkFromModifier(unsigned char modBit) {
    switch (modBit) {
        case 0x01: return 0xA2; // LCtrl
        case 0x02: return 0xA0; // LShift
        case 0x04: return 0xA4; // LAlt
        case 0x08: return 0x5B; // LWin
        case 0x10: return 0xA1; // RCtrl
        case 0x20: return 0xA3; // RShift
        case 0x40: return 0xA5; // RAlt
        case 0x80: return 0x5C; // RWin
        default: return 0;
    }
}

static BYTE keyDownStates[256] = {0};

void SendKeyDown(unsigned char vkCode) {
    if (keyDownStates[vkCode]) return;
    keyDownStates[vkCode] = 1;
    INPUT input = {0};
    input.type = INPUT_KEYBOARD;
    input.ki.wVk = vkCode;
    input.ki.wScan = 0;
    input.ki.dwFlags = 0;
    input.ki.time = 0;
    input.ki.dwExtraInfo = 0;
    SendInput(1, &input, sizeof(INPUT));
}

void SendKeyUp(unsigned char vkCode) {
    if (!keyDownStates[vkCode]) return;
    keyDownStates[vkCode] = 0;
    INPUT input = {0};
    input.type = INPUT_KEYBOARD;
    input.ki.wVk = vkCode;
    input.ki.wScan = 0;
    input.ki.dwFlags = KEYEVENTF_KEYUP;
    input.ki.time = 0;
    input.ki.dwExtraInfo = 0;
    SendInput(1, &input, sizeof(INPUT));
}

extern "C" {

__declspec(dllexport) void SendKeyDownHid(unsigned char hidUsage) {
    unsigned char vk = GetVkFromHid(hidUsage);
    if (vk) SendKeyDown(vk);
}

__declspec(dllexport) void SendKeyUpHid(unsigned char hidUsage) {
    unsigned char vk = GetVkFromHid(hidUsage);
    if (vk) SendKeyUp(vk);
}

__declspec(dllexport) void SendModifierKeyDown(unsigned char modifierBit) {
    unsigned char vk = GetVkFromModifier(modifierBit);
    if (vk) SendKeyDown(vk);
}

__declspec(dllexport) void SendModifierKeyUp(unsigned char modifierBit) {
    unsigned char vk = GetVkFromModifier(modifierBit);
    if (vk) SendKeyUp(vk);
}

__declspec(dllexport) void SendMediaKey(unsigned char action) {
    unsigned char vk = 0;
    switch (action) {
        case 0x01: vk = 0x4D; break;
        case 0x02: vk = 0x4B; break;
        case 0x04: vk = 0xB0; break;
        case 0x08: vk = 0xB1; break;
        case 0x10: vk = 0xB3; break;
        case 0x20: vk = 0xAD; break;
        case 0x40: vk = 0xAE; break;
        case 0x80: vk = 0xAF; break;
    }
    if (vk) { SendKeyDown(vk); SendKeyUp(vk); }
}

// Mouse helper functions
static void SendMouseInput(DWORD flags, int dx, int dy, DWORD wheelData) {
    INPUT input = {0};
    input.type = INPUT_MOUSE;
    input.mi.dx = dx;
    input.mi.dy = dy;
    input.mi.mouseData = wheelData;
    input.mi.dwFlags = flags;
    input.mi.time = 0;
    input.mi.dwExtraInfo = 0;
    SendInput(1, &input, sizeof(INPUT));
}

__declspec(dllexport) void MouseMoveRelative(int dx, int dy) {
    SendMouseInput(0x0001, dx, dy, 0);
}

__declspec(dllexport) void MouseButtonDown(unsigned char button) {
    DWORD flags = 0;
    switch (button) {
        case 0: flags = 0x0002; break;  // LEFTDOWN
        case 1: flags = 0x0008; break;  // RIGHTDOWN
        case 2: flags = 0x0020; break;  // MIDDLEDOWN
    }
    if (flags) SendMouseInput(flags, 0, 0, 0);
}

__declspec(dllexport) void MouseButtonDownUp(unsigned char button) {
    DWORD downFlags = 0, upFlags = 0;
    switch (button) {
        case 0: downFlags = 0x0002; upFlags = 0x0004; break;
        case 1: downFlags = 0x0008; upFlags = 0x0010; break;
        case 2: downFlags = 0x0020; upFlags = 0x0040; break;
    }
    if (downFlags) {
        INPUT input = {0};
        input.type = INPUT_MOUSE;
        input.mi.dwFlags = downFlags;
        SendInput(1, &input, sizeof(INPUT));
        input.mi.dwFlags = upFlags;
        SendInput(1, &input, sizeof(INPUT));
    }
}

__declspec(dllexport) void MouseButtonUp(unsigned char button) {
    DWORD flags = 0;
    switch (button) {
        case 0: flags = 0x0004; break;   // LEFTUP
        case 1: flags = 0x0010; break;   // RIGHTUP
        case 2: flags = 0x0040; break;   // MIDDLEUP
    }
    if (flags) SendMouseInput(flags, 0, 0, 0);
}

__declspec(dllexport) void MouseScrollVertical(int delta) {
    SendMouseInput(0x0800, 0, 0, (DWORD)delta);
}

__declspec(dllexport) void MouseScrollHorizontal(int delta) {
    SendMouseInput(0x1000, 0, 0, (DWORD)delta);
}

}