"""Exercise the smoke host against a native-menu peer on a private DBus, without a GUI."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class TrayHostProtocolTests(unittest.TestCase):
    def test_open_menu_event_uses_variant_payload_on_the_wire(self):
        python = '/usr/bin/python3'
        if not shutil.which('dbus-run-session') or not Path(python).exists():
            self.skipTest('Requires Linux dbus-run-session and system Python')
        probe = subprocess.run([python, '-c', 'import dbus, gi'], capture_output=True)
        if probe.returncode:
            self.skipTest('Requires system dbus-python and PyGObject')
        with tempfile.TemporaryDirectory(prefix='meowchan-tray-protocol-') as temporary:
            config = Path(temporary) / 'bus.conf'
            # No service directories: this test cannot activate personal desktop services.
            config.write_text('<busconfig><type>session</type><listen>unix:tmpdir=' + temporary + '</listen>'
                              '<policy context="default"><allow send_destination="*" eavesdrop="true"/>'
                              '<allow eavesdrop="true"/><allow own="*"/></policy></busconfig>')
            environment = dict(os.environ)
            for key in ('DBUS_SESSION_BUS_ADDRESS', 'DBUS_STARTER_ADDRESS', 'DBUS_STARTER_BUS_TYPE',
                        'DISPLAY', 'WAYLAND_DISPLAY', 'SESSION_MANAGER'):
                environment.pop(key, None)
            result = subprocess.run(['dbus-run-session', '--config-file=' + str(config), '--', python,
                                     str(ROOT / 'scripts/with-fake-tray-host.py'), '--', python,
                                     str(Path(__file__).resolve()), '--dbus-peer'], env=environment,
                                    text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=20)
            self.assertEqual(0, result.returncode, result.stdout)
            self.assertIn('MENU EVENT signature=isvu; id=7; payload variant=1', result.stdout)


def run_peer():
    import dbus
    import dbus.service
    from dbus.mainloop.glib import DBusGMainLoop
    from gi.repository import GLib
    DBusGMainLoop(set_as_default=True)
    bus = dbus.SessionBus()
    name = 'org.kde.StatusNotifierItem-' + str(os.getpid()) + '-0'
    bus.request_name(name)
    loop = GLib.MainLoop()
    signatures = []
    failures = []

    def inspect_message(connection, message):
        if message.get_interface() == 'com.canonical.dbusmenu' and message.get_member() == 'Event':
            signatures.append(str(message.get_signature()))
        return dbus.lowlevel.HANDLER_RESULT_NOT_YET_HANDLED
    bus.add_message_filter(inspect_message)

    class Item(dbus.service.Object):
        @dbus.service.method('org.freedesktop.DBus.Properties', in_signature='ss', out_signature='v')
        def Get(self, interface, property):
            if interface != 'org.kde.StatusNotifierItem' or property != 'Menu':
                raise ValueError('Unexpected status notifier property')
            return dbus.ObjectPath('/Menu')

    class Menu(dbus.service.Object):
        @dbus.service.method('com.canonical.dbusmenu', in_signature='iias', out_signature='u(ia{sv}av)')
        def GetLayout(self, parent, depth, properties):
            child = dbus.Struct((dbus.Int32(7), dbus.Dictionary({'label': 'Open'}, signature='sv'),
                                 dbus.Array([], signature='v')), signature='ia{sv}av', variant_level=1)
            return dbus.UInt32(1), (dbus.Int32(0), dbus.Dictionary({}, signature='sv'),
                                    dbus.Array([child], signature='v'))

        @dbus.service.method('com.canonical.dbusmenu', in_signature='isvu', out_signature='')
        def Event(self, item, event, payload, timestamp):
            signature = signatures[-1] if signatures else 'missing'
            if signature != 'isvu' or item != 7 or event != 'clicked' or payload.variant_level != 1:
                raise ValueError('Wrong native menu event: ' + signature)
            print('MENU EVENT signature=' + signature + '; id=' + str(item)
                  + '; payload variant=' + str(payload.variant_level), flush=True)

    item = Item(bus, '/StatusNotifierItem')
    menu = Menu(bus, '/Menu')
    watcher = bus.get_object('org.kde.StatusNotifierWatcher', '/StatusNotifierWatcher', introspect=False)

    def fail(error):
        failures.append(str(error))
        loop.quit()

    def registered():
        watcher.Control('open-menu-item', dbus_interface='org.meowchan.TraySmoke',
                        reply_handler=lambda: loop.quit(), error_handler=fail)

    watcher.RegisterStatusNotifierItem(name, dbus_interface='org.kde.StatusNotifierWatcher',
                                       reply_handler=registered, error_handler=fail)
    GLib.timeout_add_seconds(10, lambda: (fail('Protocol peer timeout'), False)[1])
    loop.run()
    if failures or signatures != ['isvu']:
        raise RuntimeError('; '.join(failures) + '; observed=' + repr(signatures))


if __name__ == '__main__':
    if '--dbus-peer' in sys.argv:
        run_peer()
    else:
        unittest.main()
