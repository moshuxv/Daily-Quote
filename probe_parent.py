import sys, ctypes, ctypes.wintypes as wt

user32 = ctypes.windll.user32
pid = int(sys.argv[1])

buf = ctypes.create_unicode_buffer(256)
cls = ctypes.create_unicode_buffer(256)


def enum(h, l):
    p = wt.DWORD(0)
    user32.GetWindowThreadProcessId(h, ctypes.byref(p))
    if p.value == pid:
        user32.GetClassNameW(h, cls, 256)
        user32.GetWindowTextW(h, buf, 256)
        if user32.IsWindowVisible(h):
            parent = user32.GetParent(h)
            pc = ctypes.create_unicode_buffer(256)
            if parent:
                user32.GetClassNameW(parent, pc, 256)
            else:
                pc.value = "(none/top-level)"
            r = wt.RECT()
            user32.GetWindowRect(h, ctypes.byref(r))
            style = user32.GetWindowLongW(h, -16)
            exstyle = user32.GetWindowLongW(h, -20)
            print(f"hwnd={h} cls={cls.value} title=[{buf.value}] parent_cls={pc.value}")
            print(f"   rect=({r.left},{r.top},{r.right},{r.bottom}) style=0x{style:X} exstyle=0x{exstyle:X}")
    return True


EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
user32.EnumWindows(EnumWindowsProc(enum), 0)
