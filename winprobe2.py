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
GetAncestor = user32.GetAncestor
FindWindowW = user32.FindWindowW
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
    return (f"hwnd=0x{hwnd:X} cls={cls_of(hwnd)} title=[{txt.value}] "
            f"rect=({r.left},{r.top},{r.right},{r.bottom}) vis={vis} parent=0x{par:X} ex=0x{ex:X}")

# 1) 找 Progman 和 WorkerW
progman = FindWindowW("Progman", None)
print(f"Progman = 0x{progman:X}" if progman else "Progman NOT FOUND")

# 2) 枚举 Progman 的所有后代子窗口，找 WorkerW 和我们的 widget
found = []
@WNDENUMPROC
def cb_child(hwnd, lparam):
    found.append(hwnd)
    return True

found_pid = []
@WNDENUMPROC
def cb_top(hwnd, lparam):
    found_pid.append(hwnd)
    return True

if progman:
    EnumChildWindows(progman, cb_child, 0)
    print(f"Progman children count = {len(found)}")
    for h in found:
        c = cls_of(h)
        if c == "WorkerW" or "WorkerW" in c:
            print(f"  WorkerW: {info(h)}")

# 3) 枚举所有顶层窗口里属于我们进程的（含子窗口）——查 widget 是否在某 WorkerW 下
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
    hits = []
    @WNDENUMPROC
    def cb2(hwnd, lparam):
        q = wintypes.DWORD()
        GetWindowThreadProcessId(hwnd, ctypes.byref(q))
        if q.value == pid:
            hits.append(hwnd)
        return True
    EnumWindows(cb2, 0)
    print(f"顶层窗口数={len(hits)}")
    for h in hits:
        print(" TOP:", info(h))
        # 也枚举每个顶层窗口的子窗口
        subs = []
        @WNDENUMPROC
        def cb3(shwnd, lp):
            subs.append(shwnd); return True
        EnumChildWindows(h, cb3, 0)
        for s in subs:
            q = wintypes.DWORD()
            GetWindowThreadProcessId(s, ctypes.byref(q))
            if q.value == pid:
                print("   CHILD:", info(s))
