import ctypes
from ctypes import wintypes
import subprocess, time

user32 = ctypes.WinDLL('user32', use_last_error=True)
EnumWindows = user32.EnumWindows
GetWindowThreadProcessId = user32.GetWindowThreadProcessId
IsWindowVisible = user32.IsWindowVisible
GetClassName = user32.GetClassNameW
GetWindowText = user32.GetWindowTextW
GetWindowRect = user32.GetWindowRect
GetParent = user32.GetParent
GetAncestor = user32.GetAncestor
GetWindowLongPtrW = user32.GetWindowLongPtrW

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

def find_pid():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq 拾句.exe", "/FO", "CSV"],
                         capture_output=True, text=True, encoding='gbk', errors='replace').stdout
    for line in out.splitlines():
        parts = line.strip('"').split('","')
        if len(parts) >= 2 and parts[0].strip().endswith('.exe'):
            try: return int(parts[1])
            except: pass
    return None

def main():
    pid = find_pid()
    if not pid:
        print("NO_PROCESS"); return
    print(f"TARGET_PID={pid}")
    windows = []
    @WNDENUMPROC
    def cb(hwnd, lparam):
        p = wintypes.DWORD()
        GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid:
            cls = ctypes.create_unicode_buffer(256)
            txt = ctypes.create_unicode_buffer(256)
            GetClassName(hwnd, cls, 256)
            GetWindowText(hwnd, txt, 256)
            r = RECT()
            GetWindowRect(hwnd, ctypes.byref(r))
            parent = GetParent(hwnd)
            anc = GetAncestor(hwnd, 2)  # GA_ROOT=2
            vis = bool(IsWindowVisible(hwnd))
            ex = GetWindowLongPtrW(hwnd, -20)
            windows.append((hwnd, cls.value, txt.value, r, vis, parent, anc, ex))
        return True
    EnumWindows(cb, 0)
    if not windows:
        print("NO_WINDOWS_FOUND"); return
    for hwnd, cls, txt, r, vis, parent, anc, ex in windows:
        print(f"hwnd=0x{hwnd:X} class={cls} title=[{txt}] "
              f"rect=({r.left},{r.top},{r.right},{r.bottom}) "
              f"visible={vis} parent=0x{parent:X} root=0x{anc:X} exstyle=0x{ex & 0xFFFFFFFF:X}")

main()
