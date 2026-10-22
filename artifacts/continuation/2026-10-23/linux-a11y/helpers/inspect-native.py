"""Native AT-SPI observer/actor. Only run inside the private session wrapper."""
import json,os,sys,time
from pathlib import Path
import gi
gi.require_version('Atspi','2.0')
from gi.repository import Atspi,GLib
out=Path(sys.argv[1]); pid=int(sys.argv[2]); exchange=out/'exchange'; events=(out/'native-events.jsonl').open('w'); findings=[]
numeric_probes=(out/'numeric-focus-probes.jsonl').open('w')
spinner_probes=(out/'spinner-button-probes.jsonl').open('w')
spinner_only=os.environ.get('MEOWCHAN_ACCESSIBILITY_SPINNER_ONLY')=='1'
state_names={int(getattr(Atspi.StateType,name)):name.lower() for name in dir(Atspi.StateType) if name.isupper() and isinstance(getattr(Atspi.StateType,name),int)}
if not os.environ.get('AT_SPI_BUS_ADDRESS','').startswith('unix:path=/tmp/meowchan-a11y-'):
    raise RuntimeError('Refusing a non-private accessibility bus')
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

def record_focus(stage,node,hold=2):
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

def navigate_plugin(stage):
    invoke(select(tree(),'ToolsNavigationButton'),stage+' tools')
    nodes=snapshot(stage+'-tools','open')
    item=visible(nodes,'PluginsMenuItem',['Plugins'])
    invoke(item,stage+' plugins')
    nodes=snapshot(stage+'-plugins','open')
    for identity in ['PluginCategory','PluginsRefresh','PluginsFolder']:
        node=select(nodes,identity)
        require(stage+' '+identity+' meaningful name',node is not None and bool(info(node)['name']) and info(node)['name'] not in ('Button','ComboBox'),info(node))
        record_focus(stage+'-'+identity,node)
    invoke(select(tree(),'DownloadsNavigationButton'),stage+' downloads')

def inspect_about(stage):
    invoke(select(tree(),'BrandAboutButton'),stage+' about')
    nodes=snapshot(stage+'-about','open')
    for identity in ['CheckUpdatesButton','CloseButton']:
        node=select(nodes,identity)
        require(stage+' '+identity+' meaningful name',node is not None and bool(info(node)['name']),info(node))
        if node and 'enabled' in info(node)['states']:record_focus(stage+'-'+identity,node)
    # Never call update/browser/clipboard actions: focus only.
    invoke(select(tree(),'CloseButton'),stage+' close about')

def inspect_spinner(stage):
    nodes=tree();outer=next(((n,v) for n,v in nodes if v['id']=='VideoVolumeInput'),None)
    if not outer:require(stage+' volume exposed',False);return
    descendants=[(n,v) for n,v in nodes if v['path'][:len(outer[1]['path'])]==outer[1]['path']]
    increase=select(descendants,'PART_IncreaseButton');decrease=select(descendants,'PART_DecreaseButton')
    record_focus(stage+'-increase-first',increase)
    record_focus(stage+'-decrease-bridge',decrease)
    record_focus(stage+'-increase-repeat',increase,5)
    snapshot(stage+'-spinner','after')

seen=set();deadline=time.monotonic()+230
while time.monotonic()<deadline:
    pump(.1)
    if not Path('/proc/'+str(pid)).exists():break
    try:stage=json.loads((exchange/'stage.json').read_text())['id']
    except (OSError,ValueError,KeyError):continue
    if stage in seen:continue
    seen.add(stage);pump(.7)
    if stage.startswith('main-'):
        snapshot(stage,'before');navigate_plugin(stage);inspect_about(stage)
    elif stage.startswith('settings-'):inspect_spinner(stage)
    elif stage.startswith('favorite-ready-'):
        invoke(select(tree(),'ArchiveMediaFavorite',['Add to favorites · F','Als Favorit markieren · F']),stage+' required fixture transition')
    elif stage.startswith('board-ready-'):
        invoke(select(tree(),'BoardAdmissionClearTextRules',["Clear this board's text rules",'Textregeln dieses Boards entfernen']),stage+' required fixture transition')
    (exchange/(stage+'.done')).touch()
    if stage=='board-error-de':break
pump(1)
(out/'native-findings.json').write_text(json.dumps({'scope':'Targeted plugin overview, About/update control names/focus; Increase first/repeat focus only. Other fixture checkpoints acknowledged without repeating prior matrices.','stages':sorted(seen),'checks':findings,'passed':bool(findings) and all(f['passed'] for f in findings)},indent=2,ensure_ascii=False)+'\n')
events.close();numeric_probes.close();spinner_probes.close()
