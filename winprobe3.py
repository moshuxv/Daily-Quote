import ctypes
from ctypes import wintypes
import subprocess

user32 = ctypes.WinDLL('user32', use_last_error=True)
EnumWindows = user32.EnumWindows
EnumChildWindows = user32.EnumChildWindows
GetWindowThreadProcessId = user32.GetWindowThreadProcessId
IsWindowVisible = user32.IsWindowVisible
GetClassName = user32.GetClassNameW
GetWindowText = user32.GetWindowTextW
GetWindowRect = user32.GetWindowRect
GetParent = user32.GetParent
GetWindowLongPtrW = user32.GetWindowLongPtrW

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

def cls_of(hwnd):
    b = ctypes.create_unicode_buffer(256)
    GetClassName(hwnd, b, 256)
    return b.value

def info(hwnd):
    txt = ctypes.create_unicode_buffer(256)
    GetWindowText(hwnd, txt, 256)
    r = RECT(); GetWindowRect(hwnd, ctypes.byref(r))
    vis = bool(IsWindowVisible(hwnd))
    par = GetParent(hwnd)
    ex = GetWindowLongPtrW(hwnd, -20) & 0xFFFFFFFF
    style = user32.GetWindowLongW(hwnd, -16) & 0xFFFFFFFF
    return (f"hwnd=0x{hwnd:X} cls={cls_of(hwnd)} title=[{txt.value}] "
            f"rect=({r.left},{r.top},{r.right},{r.bottom}) vis={vis} "
            f"parent=0x{par:X} style=0x{style:X} ex=0x{ex:X}")

# 全系统枚举所有顶层窗口，找 WorkerW / SHELLDLL_DefView / Progman 及类桌面窗口
tops = []
@WNDENUMPROC
def cb_top(hwnd, lparam):
    tops.append(hwnd)
    return True
EnumWindows(cb_top, 0)

desktop_like = []
for h in tops:
    c = cls_of(h)
    if c in ("WorkerW", "Progman", "SHELLDLL_DefView", "DummyDWMListenerWindow"):
        desktop_like.append(h)
        print("DESKTOP-LIKE TOP:", info(h))

# 对每个 desktop-like 窗口枚举子窗口（一层），找我们的进程窗口
def our_pid():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq 每日一句.exe", "/FO", "CSV"],
                         capture_output=True, text=True, encoding='gbk', errors='replace').stdout
    for line in out.splitlines():
        p = line.strip('"').split('","')
        if len(p) >= 2 and p[0].strip().endswith('.exe'):
            try: return int(p[1])
            except: pass
    return None

pid = our_pid()
print(f"TARGET_PID={pid}")
if pid:
    for h in desktop_like:
        subs = []
        @WNDENUMPROC
        def cb_sub(shwnd, lp):
            subs.append(shwnd); return True
        EnumChildWindows(h, cb_sub, 0)
        for s in subs:
            q = wintypes.DWORD()
            GetWindowThreadProcessId(s, ctypes.byref(q))
            if q.value == pid:
                print(f"  OUR-WIDGET under 0x{h:X}:", info(s))
