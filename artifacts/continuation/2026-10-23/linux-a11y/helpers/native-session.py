import ast,json,os,subprocess,sys,time
from pathlib import Path
out=Path(sys.argv[1]);tools=Path(sys.argv[2]);assembly=Path(sys.argv[3]);children=[];handles=[]
def start(args,name):
    log=(out/name).open('w');handles.append(log);p=subprocess.Popen(args,stdout=log,stderr=subprocess.STDOUT);children.append(p);return p
try:
    start(['/usr/lib/at-spi-bus-launcher','--launch-immediately','--a11y=1','--screen-reader=1'],'a11y.log');time.sleep(.5)
    reply=subprocess.check_output(['gdbus','call','--session','--dest','org.a11y.Bus','--object-path','/org/a11y/bus','--method','org.a11y.Bus.GetAddress'],text=True)
    os.environ['AT_SPI_BUS_ADDRESS']=ast.literal_eval(reply)[0]
    start(['/usr/lib/at-spi2-registryd'],'registry.log');time.sleep(.5)
    start([sys.executable,str(tools/'usr/bin/orca'),'--debug-file',str(out/'orca-debug.log')],'orca.log');time.sleep(1)
    app=start(['dotnet',str(assembly),'--accessibility-probe'],'app.log')
    inspector=start([sys.executable,str(Path(__file__).with_name('inspect-native.py')),str(out),str(app.pid)],'inspector.log')
    status=app.wait(timeout=240)
    inspector.wait(timeout=10)
    print('APP EXIT',status,'INSPECTOR EXIT',inspector.returncode,flush=True)
    (out/'session-results.json').write_text(json.dumps({'app_exit':status,'inspector_exit':inspector.returncode,'private_accessibility_bus':os.environ['AT_SPI_BUS_ADDRESS'],'audio':'No speech server; private unavailable socket; Orca SPEECH OUTPUT logs only','assembly':str(assembly)},indent=2)+'\n')
finally:
    for p in children[::-1]:
        if p.poll() is None:
            p.terminate()
            try:p.wait(timeout=3)
            except subprocess.TimeoutExpired:p.kill();p.wait(timeout=3)
    for handle in handles:handle.close()
