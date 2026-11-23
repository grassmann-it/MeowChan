#!/usr/bin/python3
"""Private DBus StatusNotifier fixture. Run only inside the smoke runner's bus."""
import subprocess
import sys
import dbus
import dbus.service
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib

DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
watcher = 'org.kde.StatusNotifierWatcher'
bus.request_name(watcher)
bus.request_name('org.meowchan.TraySmoke')


class Host(dbus.service.Object):
    def __init__(self):
        super().__init__(bus, '/StatusNotifierWatcher')
        self.items = []
        self.hosted = True
        self.saved_items = []

    @dbus.service.method(watcher, in_signature='s', out_signature='')
    def RegisterStatusNotifierItem(self, item):
        # KDE exposes service + object path, while Avalonia registers its service name.
        registered = item if '/' in item else item + '/StatusNotifierItem'
        if registered not in self.items:
            self.items.append(registered)
        self.StatusNotifierItemRegistered(registered)

    @dbus.service.signal(watcher, signature='s')
    def StatusNotifierItemRegistered(self, item):
        pass

    @dbus.service.method('org.freedesktop.DBus.Properties', in_signature='ss', out_signature='v')
    def Get(self, interface, property):
        if property == 'IsStatusNotifierHostRegistered':
            return dbus.Boolean(self.hosted)
        if property == 'RegisteredStatusNotifierItems':
            return dbus.Array(self.items, signature='s')
        if property == 'ProtocolVersion':
            return dbus.Int32(0)
        raise dbus.exceptions.DBusException('Unknown property')

    def item_proxy(self):
        name, _, path = self.items[0].partition('/')
        return bus.get_object(name, '/' + path if path else '/StatusNotifierItem', introspect=False)

    def visible_menu_entries(self):
        proxy = self.item_proxy()
        menu_path = proxy.Get('org.kde.StatusNotifierItem', 'Menu', dbus_interface='org.freedesktop.DBus.Properties')
        menu = dbus.Interface(bus.get_object(proxy.bus_name, menu_path, introspect=False), 'com.canonical.dbusmenu')
        menu.AboutToShow(dbus.Int32(0))
        _, layout = menu.GetLayout(dbus.Int32(0), dbus.Int32(1), dbus.Array([], signature='s'))
        entries = [(item_id, '' if props.get('type') == 'separator' else str(props['label']))
                   for item_id, props, _ in layout[2] if props.get('visible', True)]
        return menu, entries

    @dbus.service.method('org.meowchan.TraySmoke', in_signature='', out_signature='as')
    def MenuEntries(self):
        _, entries = self.visible_menu_entries()
        return [label for _, label in entries]

    @dbus.service.method('org.meowchan.TraySmoke', in_signature='s', out_signature='')
    def Control(self, command):
        if command == 'hide-own-item':
            self.saved_items = self.items
            self.items = ['org.kde.StatusNotifierItem-999999999-0/StatusNotifierItem']
        elif command == 'restore-own-item':
            self.items = self.saved_items
        elif command == 'lose-host':
            self.hosted = False
        elif command == 'drop-watcher':
            bus.release_name(watcher)
            self.items = []
        elif command == 'restart-watcher':
            self.hosted = True
            bus.request_name(watcher)
        elif command == 'activate':
            self.item_proxy().Activate(dbus.Int32(0), dbus.Int32(0), dbus_interface='org.kde.StatusNotifierItem')
        elif command == 'open-menu-item' or command.startswith('click-menu-item:'):
            menu, entries = self.visible_menu_entries()
            item_id = entries[0][0] if command == 'open-menu-item' else next(
                item_id for item_id, label in entries if label == command.removeprefix('click-menu-item:'))
            # With introspection disabled, dbus-python infers each wire type.
            # Event's data argument is a variant even when its payload is zero.
            menu.Event(dbus.Int32(item_id), 'clicked', dbus.Int32(0, variant_level=1), dbus.UInt32(0))
        else:
            raise dbus.exceptions.DBusException('Unknown command')


host = Host()
child = subprocess.Popen(sys.argv[sys.argv.index('--') + 1:])
loop = GLib.MainLoop()


def check_child():
    if child.poll() is not None:
        loop.quit()
        return False
    return True


GLib.timeout_add(50, check_child)
try:
    loop.run()
finally:
    if child.poll() is None:
        child.terminate()
    child.wait()
sys.exit(child.returncode)
