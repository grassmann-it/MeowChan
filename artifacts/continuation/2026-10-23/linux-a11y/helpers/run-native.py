"""Run only in a new Xvfb/Openbox/private AT-SPI session; no native-desktop fallback."""
from pathlib import Path
import argparse,datetime,hashlib,importlib.util,json,os,shutil,subprocess,sys,tempfile
parser=argparse.ArgumentParser();parser.add_argument('--assembly',required=True);parser.add_argument('--output',required=True);parser.add_argument('--numeric-only',action='store_true');parser.add_argument('--spinner-buttons',action='store_true');args=parser.parse_args()
root=Path(__file__).resolve().parents[1];repo=Path('/home/tomka/Desktop/oChan');tools=Path('/home/tomka/Desktop/oChan-ui-workers/boards/artifacts/ui-implementation/boards/accessibility-tools/root')
assembly=Path(args.assembly).resolve(strict=True);out=Path(args.output).resolve();out.mkdir();(out/'exchange').mkdir()
(out/'assembly-manifest.json').write_text(json.dumps({'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'files':[{'file':str(file),'sha256':hashlib.sha256(file.read_bytes()).hexdigest(),'mtime_utc':datetime.datetime.fromtimestamp(file.stat().st_mtime,datetime.timezone.utc).isoformat()} for file in [assembly,assembly.with_name('MeowChan.dll'),assembly.with_name('libmeowchan.dll')]]},indent=2)+'\n')
spec=importlib.util.spec_from_file_location('runner',repo/'scripts/run-desktop-smoke.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
with tempfile.TemporaryDirectory(prefix='meowchan-a11y-') as temporary:
    work=Path(temporary)
    base={k:v for k,v in os.environ.items() if not k.startswith(('GTK','GIO','GDK','GI_','LD_','GST','PYTHON','SNAP','QT_','XDG_DATA_DIRS','PULSE','PIPEWIRE','SPEECHD','BRLAPI'))}
    env=m.prepare_environment(base,True,work)
    env.update({'PYTHONPATH':str(tools/'usr/lib/python3.14/site-packages'),'LD_LIBRARY_PATH':str(tools/'usr/lib'),'GI_TYPELIB_PATH':str(tools/'usr/lib/girepository-1.0'),'GSETTINGS_SCHEMA_DIR':str(tools/'usr/share/glib-2.0/schemas'),'GSETTINGS_BACKEND':'memory','XDG_DATA_DIRS':str(tools/'usr/share')+':/usr/share','PULSE_SERVER':'unix:'+str(work/'no-personal-audio'),'PIPEWIRE_REMOTE':'meowchan-isolated-no-audio','SPEECHD_ADDRESS':'unix_socket:'+str(work/'no-speech-server'),'SPEECHD_CMD':'/bin/false','NO_AT_BRIDGE':'0','MEOWCHAN_ACCESSIBILITY_EXCHANGE':str(out/'exchange'),'AVALONIA_GLOBAL_SCALE_FACTOR':'1'})
    env['MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY']='0'
    env['MEOWCHAN_ACCESSIBILITY_SPINNER_ONLY']='1' if args.spinner_buttons else '0'
    custom=work/'data/orca';custom.mkdir(parents=True)
    (custom/'orca-customizations.py').write_text("from orca import gsettings_registry\ngsettings_registry.get_registry().set_runtime_value('sound', 'enabled', False)\ngsettings_registry.get_registry().set_runtime_value('braille', 'enabled', False)\n")
    command=['dbus-run-session','--config-file='+str(work/'bus.conf'),'--','xvfb-run','--auto-servernum','--server-args=-screen 0 3200x2000x24 -nolisten tcp',sys.executable,str(repo/'scripts/with-isolated-window-manager.py'),'--',sys.executable,str(root/'helpers/native-session.py'),str(out),str(tools),str(assembly)]
    with (out/'session.log').open('w') as log:
        process=subprocess.Popen(command,cwd=work,env=env,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        try:status=process.wait(timeout=260)
        finally:m.stop_owned_processes(process)
print(out,status)
