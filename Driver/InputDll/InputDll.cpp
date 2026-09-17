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
        case 0x1E: return 0x31; case 0x1F: return 0x32; case 0x20: return 0x33; case 0x21: return 0x34;
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
        // Navigation cluster
        case 0x46: return 0x2C; case 0x47: return 0x46; case 0x48: return 0x13; case 0x49: return 0x2D;
        case 0x4A: return 0x24; case 0x4B: return 0x21; case 0x4C: return 0x2E; case 0x4D: return 0x23;
        case 0x4E: return 0x22; case 0x4F: return 0x27; case 0x50: return 0x25; case 0x51: return 0x28;
        case 0x52: return 0x26;
        // Keypad
        case 0x53: return 0x90; case 0x54: return 0x6F; case 0x55: return 0x6A; case 0x56: return 0x6D;
        case 0x57: return 0x6B; case 0x58: return 0x0D; case 0x59: return 0x61; case 0x5A: return 0x62;
        case 0x5B: return 0x63; case 0x5C: return 0x64; case 0x5D: return 0x65; case 0x5E: return 0x66;
        case 0x5F: return 0x67; case 0x60: return 0x68; case 0x61: return 0x69; case 0x62: return 0x60;
        case 0x63: return 0x6E;
        // Non-US keys, application and function keys
        case 0x64: return 0xE2; case 0x65: return 0x5D; case 0x67: return 0x92;
        case 0x68: return 0x7C; case 0x69: return 0x7D; case 0x6A: return 0x7E; case 0x6B: return 0x7F;
        case 0x6C: return 0x80; case 0x6D: return 0x81; case 0x6E: return 0x82; case 0x6F: return 0x83;
        case 0x70: return 0x84; case 0x71: return 0x85; case 0x72: return 0x86; case 0x73: return 0x87;
        // Media
        case 0x7F: return 0xAD; case 0x80: return 0xAF; case 0x81: return 0xAE;
        // Modifier keys (LCtrl, LShift, LAlt, LWin, RCtrl, RShift, RAlt, RWin)
        case 0xE0: return 0xA2; case 0xE1: return 0xA0; case 0xE2: return 0xA4; case 0xE3: return 0x5B;
        case 0xE4: return 0xA3; case 0xE5: return 0xA1; case 0xE6: return 0xA5; case 0xE7: return 0x5C;
        default: return 0;
    }
}

// HID Usage Page 0x07 -> PS/2 Set-1 scan code. Bit 0xE000 marks an extended
// (E0-prefixed) key. 0 means "no scan code" -> fall back to the VK code.
static unsigned short GetScanFromHid(unsigned char hid) {
    switch (hid) {
        // A-Z
        case 0x04: return 0x001E; case 0x05: return 0x0030; case 0x06: return 0x002E; case 0x07: return 0x0020;
        case 0x08: return 0x0012; case 0x09: return 0x0021; case 0x0A: return 0x0022; case 0x0B: return 0x0023;
        case 0x0C: return 0x0017; case 0x0D: return 0x0024; case 0x0E: return 0x0025; case 0x0F: return 0x0026;
        case 0x10: return 0x0032; case 0x11: return 0x0031; case 0x12: return 0x0018; case 0x13: return 0x0019;
        case 0x14: return 0x0010; case 0x15: return 0x0013; case 0x16: return 0x001F; case 0x17: return 0x0014;
        case 0x18: return 0x0016; case 0x19: return 0x002F; case 0x1A: return 0x0011; case 0x1B: return 0x002D;
        case 0x1C: return 0x0015; case 0x1D: return 0x002C;
        // 1-0
        case 0x1E: return 0x0002; case 0x1F: return 0x0003; case 0x20: return 0x0004; case 0x21: return 0x0005;
        case 0x22: return 0x0006; case 0x23: return 0x0007; case 0x24: return 0x0008; case 0x25: return 0x0009;
        case 0x26: return 0x000A; case 0x27: return 0x000B;
        // Enter, Esc, Backspace, Tab, Space, - = [ ] \ # ; ' ` , . / Caps
        case 0x28: return 0x001C; case 0x29: return 0x0001; case 0x2A: return 0x000E; case 0x2B: return 0x000F;
        case 0x2C: return 0x0039; case 0x2D: return 0x000C; case 0x2E: return 0x000D; case 0x2F: return 0x001A;
        case 0x30: return 0x001B; case 0x31: return 0x002B; case 0x32: return 0x002B; case 0x33: return 0x0027;
        case 0x34: return 0x0028; case 0x35: return 0x0029; case 0x36: return 0x0033; case 0x37: return 0x0034;
        case 0x38: return 0x0035; case 0x39: return 0x003A;
        // F1-F12
        case 0x3A: return 0x003B; case 0x3B: return 0x003C; case 0x3C: return 0x003D; case 0x3D: return 0x003E;
        case 0x3E: return 0x003F; case 0x3F: return 0x0040; case 0x40: return 0x0041; case 0x41: return 0x0042;
        case 0x42: return 0x0043; case 0x43: return 0x0044; case 0x44: return 0x0057; case 0x45: return 0x0058;
        // PrintScreen, ScrollLock, Pause, Insert, Home, PageUp, Delete, End, PageDown
        case 0x46: return 0xE037; case 0x47: return 0x0046; case 0x48: return 0x0000; case 0x49: return 0xE052;
        case 0x4A: return 0xE047; case 0x4B: return 0xE049; case 0x4C: return 0xE053; case 0x4D: return 0xE04F;
        case 0x4E: return 0xE051;
        // Arrows (extended)
        case 0x4F: return 0xE04D; case 0x50: return 0xE04B; case 0x51: return 0xE050; case 0x52: return 0xE048;
        // Keypad
        case 0x53: return 0x0045; case 0x54: return 0xE035; case 0x55: return 0x0037; case 0x56: return 0x004A;
        case 0x57: return 0x004E; case 0x58: return 0xE01C; case 0x59: return 0x004F; case 0x5A: return 0x0050;
        case 0x5B: return 0x0051; case 0x5C: return 0x004B; case 0x5D: return 0x004C; case 0x5E: return 0x004D;
        case 0x5F: return 0x0047; case 0x60: return 0x0048; case 0x61: return 0x0049; case 0x62: return 0x0052;
        case 0x63: return 0x0053;
        // Non-US \, Application, Power, Keypad =
        case 0x64: return 0x0056; case 0x65: return 0xE05D; case 0x66: return 0xE05E; case 0x67: return 0x0059;
        // F13-F24
        case 0x68: return 0x0064; case 0x69: return 0x0065; case 0x6A: return 0x0066; case 0x6B: return 0x0067;
        case 0x6C: return 0x0068; case 0x6D: return 0x0069; case 0x6E: return 0x006A; case 0x6F: return 0x006B;
        case 0x70: return 0x006C; case 0x71: return 0x006D; case 0x72: return 0x006E; case 0x73: return 0x0076;
        // Media (Mute, Volume Up, Volume Down)
        case 0x7F: return 0xE020; case 0x80: return 0xE030; case 0x81: return 0xE02E;
        // Modifiers
        case 0xE0: return 0x001D; case 0xE1: return 0x002A; case 0xE2: return 0x0038; case 0xE3: return 0xE05B;
        case 0xE4: return 0xE01D; case 0xE5: return 0x0036; case 0xE6: return 0xE038; case 0xE7: return 0xE05C;
        default: return 0;
    }
}

static unsigned char GetVkFromModifier(unsigned char modBit) {
    switch (modBit) {
        case 0x01: return 0xA2; // LCtrl
        case 0x02: return 0xA0; // LShift
        case 0x04: return 0xA4; // LAlt
        case 0x08: return 0x5B; // LWin
        case 0x10: return 0xA3; // RCtrl
        case 0x20: return 0xA1; // RShift
        case 0x40: return 0xA5; // RAlt
        case 0x80: return 0x5C; // RWin
        default: return 0;
    }
}

// Modifier bit -> PS/2 Set-1 scan code (0xE000 marks extended).
static unsigned short GetScanFromModifier(unsigned char modBit) {
    switch (modBit) {
        case 0x01: return 0x001D; // LCtrl
        case 0x02: return 0x002A; // LShift
        case 0x04: return 0x0038; // LAlt
        case 0x08: return 0xE05B; // LWin
        case 0x10: return 0xE01D; // RCtrl
        case 0x20: return 0x0036; // RShift
        case 0x40: return 0xE038; // RAlt
        case 0x80: return 0xE05C; // RWin
        default: return 0;
    }
}

// Modifier bit -> HID usage, used as the physical key id for state tracking.
static unsigned char GetHidFromModifier(unsigned char modBit) {
    switch (modBit) {
        case 0x01: return 0xE0; case 0x02: return 0xE1; case 0x04: return 0xE2; case 0x08: return 0xE3;
        case 0x10: return 0xE4; case 0x20: return 0xE5; case 0x40: return 0xE6; case 0x80: return 0xE7;
        default: return 0;
    }
}

static BYTE keyDownStates[256] = {0};

// Inject a key event. `scan` is the PS/2 Set-1 scan code (0xE000 marks
// extended, 0 means none); otherwise the virtual-key code is used.
static void SendKeyEvent(unsigned short scan, unsigned char vkCode, bool keyUp) {
    INPUT input = {0};
    input.type = INPUT_KEYBOARD;
    input.ki.time = 0;
    input.ki.dwExtraInfo = 0;

    if (scan != 0) {
        input.ki.wVk = 0;
        input.ki.wScan = (WORD)(scan & 0xFF);
        input.ki.dwFlags = KEYEVENTF_SCANCODE;
        if (scan & 0xE000) input.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
    } else {
        input.ki.wVk = vkCode;
        input.ki.wScan = 0;
        input.ki.dwFlags = 0;
    }
    if (keyUp) input.ki.dwFlags |= KEYEVENTF_KEYUP;

    SendInput(1, &input, sizeof(INPUT));
}

// Emit a key event using its hardware scan code when available, falling back
// to the virtual-key code. `id` identifies the physical key for down-state
// tracking; `scan` is the PS/2 Set-1 scan code, with 0xE000 marking extended.
static void EmitKey(unsigned char id, unsigned short scan, unsigned char vkCode, bool keyUp) {
    if (id == 0) return;
    if (scan == 0 && (vkCode == 0 || vkCode == 0xFF)) return;

    if (keyUp) {
        if (!keyDownStates[id]) return;
        keyDownStates[id] = 0;
    } else {
        if (keyDownStates[id]) return;
        keyDownStates[id] = 1;
    }

    SendKeyEvent(scan, vkCode, keyUp);
}

void SendKeyDown(unsigned char vkCode) {
    EmitKey(vkCode, 0, vkCode, false);
}

void SendKeyUp(unsigned char vkCode) {
    EmitKey(vkCode, 0, vkCode, true);
}

extern "C" {

__declspec(dllexport) void SendKeyDownHid(unsigned char hidUsage) {
    EmitKey(hidUsage, GetScanFromHid(hidUsage), GetVkFromHid(hidUsage), false);
}

__declspec(dllexport) void SendKeyUpHid(unsigned char hidUsage) {
    EmitKey(hidUsage, GetScanFromHid(hidUsage), GetVkFromHid(hidUsage), true);
}

// Typematic auto-repeat: re-send the key-down of a key that is still held.
// Ignored unless the key is currently tracked as down.
__declspec(dllexport) void SendKeyRepeatHid(unsigned char hidUsage) {
    if (hidUsage == 0 || !keyDownStates[hidUsage]) return;
    SendKeyEvent(GetScanFromHid(hidUsage), GetVkFromHid(hidUsage), false);
}

__declspec(dllexport) void SendModifierKeyDown(unsigned char modifierBit) {
    EmitKey(GetHidFromModifier(modifierBit), GetScanFromModifier(modifierBit), GetVkFromModifier(modifierBit), false);
}

__declspec(dllexport) void SendModifierKeyUp(unsigned char modifierBit) {
    EmitKey(GetHidFromModifier(modifierBit), GetScanFromModifier(modifierBit), GetVkFromModifier(modifierBit), true);
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