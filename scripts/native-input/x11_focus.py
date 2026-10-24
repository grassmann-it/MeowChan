"""Private-Xvfb-only actual keyboard recipient inspection and exact PID/title activation."""
import ctypes as C
import ctypes.util
import os
from private_guard import require_private_session

U=C.c_ulong
class ClientData(C.Union):
    _fields_=[('b',C.c_char*20),('s',C.c_short*10),('l',C.c_long*5)]
class ClientMessage(C.Structure):
    _fields_=[('type',C.c_int),('serial',U),('send_event',C.c_int),('display',C.c_void_p),('window',U),('message_type',U),('format',C.c_int),('data',ClientData)]
class XEvent(C.Union):
    _fields_=[('xclient',ClientMessage),('pad',C.c_long*24)]

class XFocus:
    def __init__(self):
        require_private_session()
        self.x=C.CDLL(ctypes.util.find_library('X11'))
        signatures={
            'XOpenDisplay':([C.c_char_p],C.c_void_p),'XDefaultRootWindow':([C.c_void_p],U),
            'XInternAtom':([C.c_void_p,C.c_char_p,C.c_int],U),
            'XGetWindowProperty':([C.c_void_p,U,U,C.c_long,C.c_long,C.c_int,U,C.POINTER(U),C.POINTER(C.c_int),C.POINTER(U),C.POINTER(U),C.POINTER(C.POINTER(C.c_ubyte))],C.c_int),
            'XGetInputFocus':([C.c_void_p,C.POINTER(U),C.POINTER(C.c_int)],C.c_int),
            'XQueryTree':([C.c_void_p,U,C.POINTER(U),C.POINTER(U),C.POINTER(C.POINTER(U)),C.POINTER(C.c_uint)],C.c_int),
            'XSendEvent':([C.c_void_p,U,C.c_int,C.c_long,C.POINTER(XEvent)],C.c_int),
            'XFree':([C.c_void_p],C.c_int),'XFlush':([C.c_void_p],C.c_int),'XCloseDisplay':([C.c_void_p],C.c_int)}
        for name,(args,restype) in signatures.items():
            f=getattr(self.x,name);f.argtypes=args;f.restype=restype
        self.d=self.x.XOpenDisplay(None)
        if not self.d:raise RuntimeError('No private X display')
        self.root=int(self.x.XDefaultRootWindow(self.d))
    def close(self):
        if self.d:self.x.XCloseDisplay(self.d);self.d=None
    def atom(self,name):return self.x.XInternAtom(self.d,name.encode(),0)
    def prop(self,window,name):
        actual=U();fmt=C.c_int();count=U();remaining=U();data=C.POINTER(C.c_ubyte)()
        status=self.x.XGetWindowProperty(self.d,window,self.atom(name),0,4096,0,0,C.byref(actual),C.byref(fmt),C.byref(count),C.byref(remaining),C.byref(data))
        try:
            if status or not data:return None
            if fmt.value==32:return [int(C.cast(data,C.POINTER(U))[i]) for i in range(count.value)]
            if fmt.value==8:return C.string_at(data,count.value).decode('utf-8','replace')
            return None
        finally:
            if data:self.x.XFree(data)
    def chain(self,window):
        chain=[]
        while window>1 and window not in chain and len(chain)<32:
            chain.append(window)
            if window==self.root:break
            root=U();parent=U();children=C.POINTER(U)();count=C.c_uint()
            okay=self.x.XQueryTree(self.d,window,C.byref(root),C.byref(parent),C.byref(children),C.byref(count))
            if children:self.x.XFree(children)
            if not okay:break
            window=int(parent.value)
        return chain
    def state(self):
        focused=U();revert=C.c_int();self.x.XGetInputFocus(self.d,C.byref(focused),C.byref(revert))
        clients=[]
        for window in self.prop(self.root,'_NET_CLIENT_LIST') or []:
            clients.append({'xid':window,'pid':(self.prop(window,'_NET_WM_PID') or [None])[0],
                            'title':self.prop(window,'_NET_WM_NAME') or self.prop(window,'WM_NAME')})
        return {'input_focus':int(focused.value),'focus_ancestors':self.chain(int(focused.value)),
                'active_window':self.prop(self.root,'_NET_ACTIVE_WINDOW'),'clients':clients}
    def request_activation(self,window):
        event=XEvent();event.xclient.type=33;event.xclient.display=self.d;event.xclient.window=window
        event.xclient.message_type=self.atom('_NET_ACTIVE_WINDOW');event.xclient.format=32
        event.xclient.data.l[0]=2
        okay=self.x.XSendEvent(self.d,self.root,0,(1<<19)|(1<<20),C.byref(event));self.x.XFlush(self.d)
        return bool(okay)

def ensure_target(pid,title,pump):
    x=XFocus()
    try:
        before=x.state();targets=[v for v in before['clients'] if v['pid']==pid and v['title']==title]
        result={'pid':pid,'target_title':title,'before':before,'matches':targets,'activation_requested':False}
        if len(targets)!=1:
            result['reason']='Expected exactly one X11 client with checkpoint PID and accessible frame title';result['ready']=False;return result
        target=targets[0]['xid'];after=before
        if target not in before['focus_ancestors']:
            result['activation_requested']=x.request_activation(target)
            for _ in range(8):
                pump(.25);after=x.state()
                if target in after['focus_ancestors']:break
        result['after']=after;result['ready']=target in after['focus_ancestors']
        return result
    finally:x.close()
