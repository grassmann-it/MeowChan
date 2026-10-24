import ast,json,os,platform,subprocess,sys,time
from pathlib import Path
out,bundle,assembly=map(Path,sys.argv[1:4]);suite=sys.argv[4];children=[];handles=[];status=1
from private_guard import require_private_session
private_root=require_private_session(require_atspi=False)
def start(args,name):
 log=(out/name).open('w');handles.append(log);p=subprocess.Popen(args,stdout=log,stderr=subprocess.STDOUT);children.append(p);return p
try:
 (out/'environment.json').write_text(json.dumps({'privateRoot':str(private_root),'ownerMarkerSha256':__import__('hashlib').sha256((private_root/'.native-input-owner').read_bytes()).hexdigest(),'platform':platform.platform(),'display':os.environ.get('DISPLAY'),'runtime':os.environ.get('XDG_RUNTIME_DIR'),'home':os.environ['HOME'],'state':os.environ['XDG_STATE_HOME'],'homeMode':oct(Path(os.environ['HOME']).stat().st_mode & 0o777),'stateMode':oct(Path(os.environ['XDG_STATE_HOME']).stat().st_mode & 0o777),'bundle':str(bundle),'suite':suite,'scale':os.environ['AVALONIA_GLOBAL_SCALE_FACTOR'],'audio':'disabled; no personal endpoint; Orca debug text only'},indent=2)+'\n')
 start(['/usr/lib/at-spi-bus-launcher','--launch-immediately','--a11y=1','--screen-reader=1'],'a11y.log');time.sleep(.5)
 reply=subprocess.check_output(['gdbus','call','--session','--dest','org.a11y.Bus','--object-path','/org/a11y/bus','--method','org.a11y.Bus.GetAddress'],text=True)
 os.environ['AT_SPI_BUS_ADDRESS']=ast.literal_eval(reply)[0]
 require_private_session()
 start(['/usr/lib/at-spi2-registryd'],'registry.log');time.sleep(.5)
 start([sys.executable,str(bundle/'usr/bin/orca'),'--debug-file',str(out/'orca-debug.log')],'orca.log');time.sleep(1)
 flags=['--plugins'] if suite=='plugins' else []
 app=start(['dotnet',str(assembly),*flags],'app.log')
 inspector=start([sys.executable,str(Path(__file__).with_name('inspect-features.py')),str(out),str(app.pid)],'inspector.log')
 appstatus=app.wait(timeout=880);inspector.wait(timeout=12)
 findings=json.loads((out/'native-findings.json').read_text())
 speech_status=subprocess.run([sys.executable,str(Path(__file__).with_name('summarize-speech.py')),str(out)],check=False).returncode
 speech=json.loads((out/'speech-summary.json').read_text())
 status=appstatus or inspector.returncode or speech_status or (0 if findings['passed'] and speech['passed'] else 1)
 (out/'session-results.json').write_text(json.dumps({'app_exit':appstatus,'inspector_exit':inspector.returncode,'assertions_passed':findings['passed'],'orca_utterances_passed':speech['passed'],'speech_exit':speech_status,'exit_status':status,'native_bus':os.environ['AT_SPI_BUS_ADDRESS'],'scope':'private Linux X11/AT-SPI; Orca utterances separate; no human/IME/cross-platform PASS'},indent=2)+'\n')
except Exception as error:
 (out/'session-error.json').write_text(json.dumps({'error':repr(error),'exit_status':1})+'\n');status=1
finally:
 for p in children[::-1]:
  if p.poll() is None:
   p.terminate()
   try:p.wait(timeout=3)
   except subprocess.TimeoutExpired:p.kill();p.wait(timeout=3)
 for handle in handles:handle.close()
raise SystemExit(status)
