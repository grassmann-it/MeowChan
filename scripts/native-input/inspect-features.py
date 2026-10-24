"""Native AT-SPI observer/actor. Only run inside the private session wrapper."""
import ctypes,ctypes.util,json,os,sys,time
from pathlib import Path
from x11_focus import ensure_target
from private_guard import require_private_session
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi,GLib
out=Path(sys.argv[1]); pid=int(sys.argv[2]); exchange=out/'exchange'; events=(out/'native-events.jsonl').open('w'); findings=[]
state_names={int(getattr(Atspi.StateType,name)):name.lower() for name in dir(Atspi.StateType) if name.isupper() and isinstance(getattr(Atspi.StateType,name),int)}
require_private_session()
Atspi.set_timeout(1500,1500)
def safe(fn,default=None):
    try:return fn()
    except Exception:return default

def info(node):
    if node is None:return None
    relations=[]
    for relation in safe(node.get_relation_set,[]) or []:
        relations.append({'type':str(relation.get_relation_type()),'targets':[safe(lambda:relation.get_target(i).get_name()) for i in range(relation.get_n_targets())]})
    return {'name':safe(node.get_name),'role':safe(node.get_role_name),'id':safe(node.get_accessible_id),'description':safe(node.get_description),'help':safe(node.get_help_text),'states':[state_names.get(int(s),str(s)) for s in safe(lambda:node.get_state_set().get_states(),[])],'interfaces':safe(node.get_interfaces,[]),'attributes':safe(node.get_attributes,{}),'relations':relations}

def event(e,*_):
    if safe(e.source.get_process_id)!=pid:return
    record={'t':time.time(),'type':e.type,'detail1':e.detail1,'detail2':e.detail2,'source':info(e.source)}
    data=e.any_data
    if isinstance(data,(str,int,bool,float)):record['data']=data
    events.write(json.dumps(record,ensure_ascii=False)+'\n');events.flush()
listener=Atspi.EventListener.new(event)
for kind in ['object:state-changed:focused','object:property-change:accessible-name','object:announcement','window:activate']:
    try:listener.register(kind)
    except Exception as ex:events.write(json.dumps({'registration_error':kind,'error':str(ex)})+'\n')
def pump(seconds):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        while GLib.MainContext.default().pending():GLib.MainContext.default().iteration(False)
        time.sleep(.02)
def tree():
    desktop=Atspi.get_desktop(0); nodes=[]
    def visit(node,path,depth):
        if len(nodes)>1500 or depth>35:return
        value=info(node);value['path']=path;nodes.append((node,value))
        for i in range(safe(node.get_child_count,0)):
            child=safe(lambda:node.get_child_at_index(i))
            if child is not None:visit(child,path+[i],depth+1)
    for i in range(desktop.get_child_count()):
        app=desktop.get_child_at_index(i)
        if safe(app.get_process_id)==pid:visit(app,[i],0)
    return nodes

def snapshot(stage,suffix):
    nodes=tree();(out/(stage+'-'+suffix+'.json')).write_text(json.dumps([v for _,v in nodes],indent=2,ensure_ascii=False)+'\n');return nodes

def require(name,okay,detail=None):
    findings.append({'check':name,'passed':bool(okay),'detail':detail});print(name,okay,flush=True)
def select(nodes,identity,names=()):
    return next((n for n,v in nodes if v['id']==identity),None) or next((n for n,v in nodes if v['name'] in names and v['role'] not in ('label','static','text')),None)
def focus(node,stage):
    ok=safe(lambda:node.get_component_iface().grab_focus(),False) if node else False
    pump(.6);require(stage+' focus request',ok,info(node));return ok

def invoke(node,stage):
    count=safe(lambda:node.get_action_iface().get_n_actions(),0) if node else 0
    okay=safe(lambda:node.get_action_iface().do_action(0),False) if count else False
    require(stage+' native action',okay,info(node));pump(.8)

def record_focus(stage,node,hold=1.2):
    # A repeated focus request need not emit an Orca utterance: first leave by a real Tab.
    if node is not None and 'focused' in info(node)['states']:
        generated=native_key();pump(.25)
        require(stage+' leave before refocus',generated and 'focused' not in info(node)['states'])
    start=time.time(); requested=safe(lambda:node.get_component_iface().grab_focus(),False) if node else False
    pump(hold)
    value=info(node)
    result={'stage':stage,'start':start,'end':time.time(),'requested':requested,'target':value,
            'focused_nodes':[v for _,v in tree() if 'focused' in v['states']]}
    (out/(stage+'-focus.json')).write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n')
    require(stage+' actual focus',value is not None and 'focused' in value['states'],result)
    return result

def visible(nodes,identity,names=()):
    return select([(n,v) for n,v in nodes if 'showing' in v['states']],identity,names)

def text_value(node):
    return safe(lambda:Atspi.Text.get_text(node,0,-1))

def native_key(keysym=0xff09):
    if os.environ.get('MEOWCHAN_DESKTOP_SMOKE_XVFB')!='1':
        raise RuntimeError('XTest keyboard probe requires the private Xvfb fixture')
    x11=ctypes.CDLL(ctypes.util.find_library('X11'));xtst=ctypes.CDLL(ctypes.util.find_library('Xtst'))
    x11.XOpenDisplay.argtypes=[ctypes.c_char_p];x11.XOpenDisplay.restype=ctypes.c_void_p
    x11.XKeysymToKeycode.argtypes=[ctypes.c_void_p,ctypes.c_ulong];x11.XKeysymToKeycode.restype=ctypes.c_uint
    x11.XFlush.argtypes=[ctypes.c_void_p];x11.XCloseDisplay.argtypes=[ctypes.c_void_p]
    xtst.XTestFakeKeyEvent.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_int,ctypes.c_ulong]
    display=x11.XOpenDisplay(None)
    if not display:return False
    try:
        key=x11.XKeysymToKeycode(display,keysym)
        pressed=xtst.XTestFakeKeyEvent(display,key,1,0);released=xtst.XTestFakeKeyEvent(display,key,0,0)
        x11.XFlush(display)
        return bool(key and pressed and released)
    finally:x11.XCloseDisplay(display)


def match(nodes,probe):
    candidates=[(n,v) for n,v in nodes if probe.get('id') and v['id']==probe['id']]
    if not candidates and probe.get('name'):
        candidates=[(n,v) for n,v in nodes if v['name']==probe['name'] and v['role'] not in ('label','static')]
    if len(candidates)!=1:return None
    return candidates[0][0]

def inspect_stage(stage):
    name=stage['id'];nodes=snapshot(name,'before')
    require(name+' native app tree',bool(nodes),{'pid':pid,'nodes':len(nodes)})
    initial=match(nodes,stage.get('initialFocus') or {})
    first=next((match(nodes,p) for p in stage['probes'] if p['focusable'] and p['enabled']),None)
    if first is not None:
        frame=first
        while frame is not None and safe(frame.get_role_name) not in ('frame','dialog','window'):
            frame=safe(frame.get_parent)
        activation=ensure_target(pid,safe(frame.get_name) if frame else None,pump)
        require(name+' initial X11 keyboard recipient',activation['ready'],activation)
        if not activation['ready']:return
    for i,probe in enumerate(stage['probes']):
        label=name+':'+str(probe.get('id') or probe.get('name') or i)
        node=match(nodes,probe);require(label+' uniquely exposed',node is not None,probe)
        if node is None:continue
        value=info(node)
        require(label+' nonempty native name',bool(value['name']),value)
        if probe.get('name'):require(label+' exact localized name',value['name']==probe['name'],value)
        require(label+' native role',bool(value['role']) and value['role'] not in ('invalid','unknown'),value)
        if probe.get('expectedHelp'):
            require(label+' native error/help',probe['expectedHelp'] in ((value['help'] or '')+'\n'+(value['description'] or '')),value)
        if probe.get('expectedText') is not None:
            require(label+' native raw text',text_value(node)==probe['expectedText'],{'expected':probe['expectedText'],'native':text_value(node)})
        if probe['requireInitialFocus']:
            require(label+' initial native focus','focused' in value['states'],value)
        if probe['focusable'] and probe['enabled']:
            record_focus(label.replace(':','_').replace('/','_'),node,1.2)
    # One real XTest Tab from the first enabled focusable probe, then restore.
    first=next((match(tree(),p) for p in stage['probes'] if p['focusable'] and p['enabled']),None)
    if first is not None:
        frame=first
        while frame is not None and safe(frame.get_role_name) not in ('frame','dialog','window'):
            frame=safe(frame.get_parent)
        activation=ensure_target(pid,safe(frame.get_name) if frame else None,pump)
        (out/(name+'-x11-keyboard-recipient.json')).write_text(json.dumps(activation,indent=2)+'\n')
        require(name+' X11 keyboard recipient ready',activation['ready'],activation)
        focus(first,name+' native Tab start')
        generated=native_key() if activation['ready'] else False
        pump(.35)
        require(name+' native Tab moves focus',generated and 'focused' not in info(first)['states'],
                {'x11_recipient_ready':activation['ready'],'key_generated':generated,'source':info(first),'focused':[v for _,v in tree() if 'focused' in v['states']]})
    if initial is not None:
        focus(initial,name+' restore initial focus')
    for probe in stage['probes']:
        if probe.get('expectedText') is not None:
            node=match(tree(),probe)
            require(name+' raw preserved after native focus/Tab',node is not None and text_value(node)==probe['expectedText'])
    snapshot(name,'after')

expected=set(json.loads(os.environ['MEOWCHAN_NATIVE_EXPECTED_FAMILIES']))
expected_stages=set(json.loads(os.environ['MEOWCHAN_NATIVE_EXPECTED_STAGES']))
seen=set();families=set();deadline=time.monotonic()+850
try:
    while time.monotonic()<deadline:
        pump(.1)
        if not Path('/proc/'+str(pid)).exists():break
        try:stage=json.loads((exchange/'stage.json').read_text())
        except (OSError,ValueError,KeyError):continue
        if stage['id'] in seen:continue
        if stage.get('pid')!=pid:raise RuntimeError('Checkpoint PID does not belong to launched app')
        seen.add(stage['id']);families.add(stage['family'])
        try:inspect_stage(stage)
        except Exception as error:require(stage['id']+' observer exception',False,repr(error))
        (exchange/(stage['id']+'.done')).touch()
    require('all required feature families observed',expected.issubset(families),{'expected':sorted(expected),'actual':sorted(families)})
    require('all required localized stages observed',expected_stages.issubset(seen),{'expected':sorted(expected_stages),'actual':sorted(seen)})
except Exception as error:require('observer fatal',False,repr(error))
finally:
    (out/'native-findings.json').write_text(json.dumps({'scope':'Real private Linux AT-SPI names/roles/help/raw text; native focus and XTest Tab; Orca utterances evaluated separately. No human/native IME/full workflow claim.',
       'stages':sorted(seen),'families':sorted(families),'checks':findings,'passed':bool(findings) and all(f['passed'] for f in findings)},indent=2,ensure_ascii=False)+'\n')
    events.close()
raise SystemExit(0 if findings and all(f['passed'] for f in findings) else 1)
