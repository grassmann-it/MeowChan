"""Native AT-SPI observer/actor. Only run inside the private session wrapper."""
import ctypes,ctypes.util,datetime,json,os,re,sys,time
from pathlib import Path
from x11_focus import ensure_target
from private_guard import require_private_session
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi,GLib
out=Path(sys.argv[1]); pid=int(sys.argv[2]); exchange=out/'exchange'; events=(out/'native-events.jsonl').open('w'); findings=[]
focus_events=[]
stage_deadline=0
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
    if e.type=='object:state-changed:focused':focus_events.append(record)
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

def same_target(value,target):
    return bool(value and target and value['name']==target['name'] and value['role']==target['role']
                and (value['id']==target['id'] if target.get('id') else True))

def log_time(line,reference):
    match=re.match(r'(\d\d):(\d\d):(\d\d\.\d+) - ',line)
    if not match:return None
    date=datetime.datetime.fromtimestamp(reference)
    clock=int(match[1])*3600+int(match[2])*60+float(match[3])
    stamp=date.replace(hour=0,minute=0,second=0,microsecond=0).timestamp()+clock
    if stamp-reference>43200:stamp-=86400
    elif reference-stamp>43200:stamp+=86400
    return stamp

def record_focus(stage,node):
    # A repeated focus request need not emit an Orca utterance: first leave by a real Tab.
    left=True
    if node is not None and 'focused' in info(node)['states']:
        generated=native_key();pump(.25)
        left=generated and 'focused' not in info(node)['states']
        require(stage+' leave before refocus',left)
    target=info(node)
    event_start=len(focus_events)
    with (out/'orca-debug.log').open(errors='replace') as speech:
        speech.seek(0,2) # Completed utterances from before this request can never satisfy it.
        start=time.time(); requested=safe(lambda:node.get_component_iface().grab_focus(),False) if node and left else False
        target_event=None;orca_focus=None;utterance=None;focus_lost=False;orca_focus_lost=False
        pending_lines=[];processed=0;event_cursor=event_start
        # This is a completion barrier inside the existing checkpoint, never a retry or a new timeout.
        while requested and time.monotonic()<stage_deadline:
            pump(.04)
            new_events=focus_events[event_cursor:];event_cursor=len(focus_events)
            for item in new_events:
                if item['t']<start:continue
                if item['detail1']==1:
                    if same_target(item['source'],target):
                        if target_event is None:target_event=item
                    elif target_event is not None:focus_lost=True
                elif item['detail1']==0 and target_event is not None and same_target(item['source'],target):
                    focus_lost=True
            if focus_lost:break
            while True:
                offset=speech.tell();line=speech.readline()
                if not line:break
                if not line.endswith('\n'):
                    speech.seek(offset);break
                stamp=log_time(line,start)
                if stamp is not None and stamp>=start:pending_lines.append((stamp,line))
            # The two DBus consumers can receive the same event in either order.
            # Keep post-request Orca records until the exact native target event is observed.
            if target_event:
                for stamp,line in pending_lines[processed:]:
                    target_description='to ['+target['role']+': '+repr(target['name'])+' ('
                    if 'FOCUS MANAGER: Changing locus of focus from ' in line:
                        if target_description in line:
                            orca_focus={'t':stamp,'line':line.rstrip()}
                        elif orca_focus:
                            orca_focus_lost=True;break
                    if orca_focus and stamp>=orca_focus['t'] and ' - SPEECH OUTPUT: '+repr(target['name']) in line:
                        utterance={'t':stamp,'line':line.rstrip()};break
                processed=len(pending_lines)
            if utterance or orca_focus_lost:break
    value=info(node)
    current_focus=[v for _,v in tree() if 'focused' in v['states']]
    pump(.02)
    value=info(node)
    subsequent=[item for item in focus_events[event_start:] if target_event and item['t']>target_event['t']
                and ((item['detail1']==1 and not same_target(item['source'],target))
                     or (item['detail1']==0 and same_target(item['source'],target)))]
    matched=bool(target_event and orca_focus and utterance and not focus_lost and not orca_focus_lost and not subsequent
                 and value and 'focused' in value['states'] and same_target(value,target))
    result={'stage':stage,'start':start,'end':time.time(),'requested':requested,'target':value,
            'focused_nodes':current_focus,'nativeFocusEvent':target_event,'orcaFocusTransition':orca_focus,
            'orcaUtterance':utterance,'subsequentFocusChanges':subsequent,'orcaFocusLost':orca_focus_lost,'completionMatched':matched,
            'scope':'New exact target native focus event, following Orca locus transition and exact utterance; no later focus transition before completion.'}
    (out/(stage+'-focus.json')).write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n')
    require(stage+' actual focus',value is not None and 'focused' in value['states'],result)
    require(stage+' event-bound Orca completion',matched,result)
    return result

def visible(nodes,identity,names=()):
    return select([(n,v) for n,v in nodes if 'showing' in v['states']],identity,names)

def text_value(node):
    return safe(lambda:Atspi.Text.get_text(node,0,-1))

def native_key(keysym=0xff09,modifiers=()):
    require_private_session()
    if os.environ.get('MEOWCHAN_DESKTOP_SMOKE_XVFB')!='1':
        raise RuntimeError('XTest keyboard probe requires the private Xvfb fixture')
    x11=ctypes.CDLL(ctypes.util.find_library('X11'));xtst=ctypes.CDLL(ctypes.util.find_library('Xtst'))
    x11.XOpenDisplay.argtypes=[ctypes.c_char_p];x11.XOpenDisplay.restype=ctypes.c_void_p
    x11.XKeysymToKeycode.argtypes=[ctypes.c_void_p,ctypes.c_ulong];x11.XKeysymToKeycode.restype=ctypes.c_uint
    x11.XFlush.argtypes=[ctypes.c_void_p];x11.XCloseDisplay.argtypes=[ctypes.c_void_p]
    xtst.XTestFakeKeyEvent.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_int,ctypes.c_ulong]
    display=x11.XOpenDisplay(None)
    if not display:return False
    held=[]
    okay=True
    try:
        codes=[x11.XKeysymToKeycode(display,symbol) for symbol in (*modifiers,keysym)]
        if not all(codes):return False
        for code in codes:
            pressed=xtst.XTestFakeKeyEvent(display,code,1,0)
            if pressed:held.append(code)
            okay=bool(pressed) and okay
    finally:
        for code in reversed(held):okay=bool(xtst.XTestFakeKeyEvent(display,code,0,0)) and okay
        x11.XFlush(display)
        x11.XCloseDisplay(display)
    return okay


def send_native(stage,node,keysym,modifiers=()):
    # Revalidate private ownership and the exact PID/frame recipient before each
    # real input, including modifiers; never fall back to an active user window.
    require_private_session()
    if node is None or safe(node.get_process_id)!=pid:
        require(stage+' owned native target',False,info(node));return False
    frame=node
    while frame is not None and safe(frame.get_role_name) not in ('frame','dialog','window'):
        frame=safe(frame.get_parent)
    activation=ensure_target(pid,safe(frame.get_name) if frame else None,pump)
    ready=activation['ready'] and 'focused' in info(node)['states']
    generated=native_key(keysym,modifiers) if ready else False
    value={'stage':stage,'utc':time.time(),'keysym':hex(keysym),'modifiers':[hex(value) for value in modifiers],
           'target':info(node),'recipient':activation,'generated':generated}
    with (out/'native-actions.jsonl').open('a') as log:log.write(json.dumps(value,ensure_ascii=False)+'\n')
    require(stage+' exact recipient and native key',ready and generated,value)
    return ready and generated


def wait_native(predicate):
    # All actor work shares the existing checkpoint deadline; no retry/timeout extension.
    while time.monotonic()<stage_deadline:
        if predicate():return True
        pump(.04)
    return False


def selected_action(actions_name):
    nodes=tree()
    owners=[(node,value) for node,value in nodes if value['name']==actions_name and value['role']=='list']
    if len(owners)!=1:return None
    owner,owner_value=owners[0]
    rows=[(node,value) for node,value in nodes if value['role']=='list item' and 'selected' in value['states']
          and value['path'][:len(owner_value['path'])]==owner_value['path']]
    if len(rows)!=1:return None
    item,item_value=rows[0]
    return {'owner':owner_value,'item':item_value}


def same_selection(before,after):
    return bool(before and after and all(before[key][field]==after[key][field]
        for key in ('owner','item') for field in ('path','name','role','id')))


def inspect_shortcut_record(stage):
    name=stage['id'];de=stage['language'].startswith('de')
    record_name='Weiteres Kürzel aufnehmen' if de else 'Record additional shortcut'
    capture_name='Tastenkürzel aufnehmen' if de else 'Record shortcut'
    actions_name='Aktionen und Tastenkürzel' if de else 'Actions and shortcuts'
    for activation,keysym in [('enter',0xff0d),('space',0x20)]:
        before=selected_action(actions_name)
        require(name+' '+activation+' unique selected action',before is not None,before)
        if before is None:return
        record=match(tree(),{'name':record_name})
        require(name+' '+activation+' unique actual Record button',record is not None,{'expected':record_name,'actual':info(record)})
        if record is None or not focus(record,name+' '+activation+' Record focus'):return
        visible_focus='focused' in info(record)['states'] and 'showing' in info(record)['states']
        require(name+' '+activation+' Record visible focus',visible_focus,info(record))
        if not visible_focus:return
        if not send_native(name+' '+activation+' Record',record,keysym):return
        capture=None
        def capturing():
            nonlocal capture
            capture=match(tree(),{'name':capture_name})
            return capture is not None and 'focused' in info(capture)['states'] and 'enabled' in info(capture)['states']
        started=wait_native(capturing)
        require(name+' '+activation+' real capture focus',started,info(capture))
        if not started:return
        if not send_native(name+' '+activation+' Escape',capture,0xff1b):return
        after=None
        def returned():
            nonlocal after
            after=selected_action(actions_name)
            return same_selection(before,after) and 'focused' in after['item']['states'] and 'showing' in after['item']['states']
        restored=wait_native(returned)
        result={'before':before,'after':after,'same_native_path_name_role_id':same_selection(before,after),
                'focused_and_showing_after':restored,'scope':'Native Enter/Space starts Record, native Escape returns to exact selected action; no focus request after Escape.'}
        (out/(name+'-'+activation+'-selected-action-return.json')).write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n')
        require(name+' '+activation+' Escape exact selected return',restored,result)
        if not restored:return


def inspect_shortcut_text(stage,initial):
    name=stage['id']
    require(name+' actual text actor target',initial is not None,info(initial))
    if initial is None or not focus(initial,name+' text setup focus'):return
    raw=text_value(initial)
    if not send_native(name+' text pause shortcut',initial,ord('p'),(0xffe3,0xffe1)):return
    pump(.2)
    require(name+' text shortcut preserves focus and raw text','focused' in info(initial)['states'] and text_value(initial)==raw,
            {'node':info(initial),'before':raw,'after':text_value(initial)})
    if not send_native(name+' forward Tab',initial,0xff09):return
    moved=wait_native(lambda:'focused' not in info(initial)['states'])
    focused=[node for node,value in tree() if 'focused' in value['states'] and value['role'] not in ('frame','dialog','window','application')]
    require(name+' Tab actual next target',moved and len(focused)==1,[info(node) for node in focused])
    if not moved or len(focused)!=1:return
    if not send_native(name+' reverse ShiftTab',focused[0],0xff09,(0xffe1,)):return
    restored=wait_native(lambda:'focused' in info(initial)['states'])
    require(name+' ShiftTab exact text return',restored and text_value(initial)==raw,info(initial))


def inspect_shortcut_normal(stage,initial):
    name=stage['id']
    require(name+' actual normal actor target',initial is not None,info(initial))
    if initial is None or not focus(initial,name+' normal setup focus'):return
    before=info(initial)
    if not send_native(name+' normal pause shortcut',initial,ord('p'),(0xffe3,0xffe1)):return
    changed=wait_native(lambda:info(initial)['name']!=before['name'] and 'enabled' in info(initial)['states'])
    require(name+' normal command changes native pause state',changed,{'before':before,'after':info(initial),
        'scope':'Native accessible Pause/Resume label transition; C# independently requires actual Host pause and persisted profile change.'})


def match(nodes,probe):
    candidates=[(n,v) for n,v in nodes if probe.get('id') and v['id']==probe['id']]
    if not candidates and probe.get('name'):
        candidates=[(n,v) for n,v in nodes if v['name']==probe['name'] and v['role'] not in ('label','static')]
    if len(candidates)!=1:return None
    return candidates[0][0]

def inspect_stage(stage):
    name=stage['id'];nodes=snapshot(name,'before')
    if stage['family'] in ('shortcuts','shortcut-text','shortcut-normal'):
        actual=stage.get('renderScaling')
        valid=isinstance(actual,(int,float)) and abs(actual-float(os.environ['AVALONIA_GLOBAL_SCALE_FACTOR']))<=0.025
        require(name+' actual native scale',valid,{'actual':actual,'requested':os.environ['AVALONIA_GLOBAL_SCALE_FACTOR']})
        if not valid:return
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
            result=record_focus(label.replace(':','_').replace('/','_'),node)
            if not result['completionMatched']:return
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
    if stage['family']=='shortcuts':
        inspect_shortcut_record(stage)
    elif stage['family']=='shortcut-text':
        inspect_shortcut_text(stage,initial)
    elif stage['family']=='shortcut-normal':
        inspect_shortcut_normal(stage,initial)
    elif initial is not None:
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
        # Leave ten seconds of the unchanged 55-second C# deadline for final snapshots/ack.
        stage_deadline=time.monotonic()+max(0,45-max(0,time.time()-(exchange/'stage.json').stat().st_mtime))
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
