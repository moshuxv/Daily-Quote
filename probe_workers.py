import ctypes, ctypes.wintypes as wt
user32 = ctypes.windll.user32
def cls(h):
    b=ctypes.create_unicode_buffer(256); user32.GetClassNameW(h,b,256); return b.value
def title(h):
    b=ctypes.create_unicode_buffer(256); user32.GetWindowTextW(h,b,256); return b.value
def rect(h):
    r=wt.RECT(); user32.GetWindowRect(h,ctypes.byref(r)); return (r.left,r.top,r.right,r.bottom)
def isvis(h): return user32.IsWindowVisible(h)
EWP=ctypes.WINFUNCTYPE(ctypes.c_bool,wt.HWND,wt.LPARAM)
out=[]
def enum(h,l):
    c=cls(h)
    if c=="WorkerW":
        out.append((h, isvis(h), cls(user32.GetParent(h)), rect(h), title(h)))
    return True
user32.EnumWindows(EWP(enum),0)
print("WorkerW count:",len(out))
for h,v,pc,r,t in out:
    print(f"hwnd={h} vis={v} parent={pc} rect={r} title=[{t}]")
# progman child check
progman=user32.FindWindowW("Progman",None)
print("Progman=",progman,"direct-child WorkerW via FindWindowEx:")
c=user32.FindWindowExW(progman,0,"WorkerW",None)
print("  progman->WorkerW child=",c,"vis=",isvis(c) if c else None)
