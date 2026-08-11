import ctypes, ctypes.wintypes as wt

user32 = ctypes.windll.user32

buf = ctypes.create_unicode_buffer(256)
cls = ctypes.create_unicode_buffer(256)


def enum(h, l):
    user32.GetClassNameW(h, cls, 256)
    c = cls.value
    if c.startswith("HwndWrapper"):
        user32.GetWindowTextW(h, buf, 256)
        parent = user32.GetParent(h)
        pc = ctypes.create_unicode_buffer(256)
        if parent:
            user32.GetClassNameW(parent, pc, 256)
        else:
            pc.value = "(none/top-level)"
        r = wt.RECT()
        user32.GetWindowRect(h, ctypes.byref(r))
        vis = user32.IsWindowVisible(h)
        print(f"WPF hwnd={h} title=[{buf.value}] parent_cls={pc.value} vis={vis} rect=({r.left},{r.top},{r.right},{r.bottom})")
    return True


EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
user32.EnumWindows(EnumWindowsProc(enum), 0)
print("--- scan done ---")
