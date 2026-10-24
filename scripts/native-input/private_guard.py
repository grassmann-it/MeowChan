"""Validate the exact runner-owned session, independently of the configured temp parent."""
import os
from pathlib import Path
import re
import stat
from urllib.parse import unquote


def require_private_session(require_atspi=True):
    root_text = os.environ.get('MEOWCHAN_NATIVE_PRIVATE_ROOT', '')
    token = os.environ.get('MEOWCHAN_NATIVE_PRIVATE_TOKEN', '')
    root = Path(root_text)
    if not root.is_absolute() or root.is_symlink() or root.resolve(strict=True) != root:
        raise RuntimeError('Explicit canonical private session root required')
    if not root.name.startswith('meowchan-a11y-') or re.fullmatch('[0-9a-f]{64}', token) is None:
        raise RuntimeError('Runner-owned private session identity required')
    marker = root / '.native-input-owner'
    if marker.is_symlink() or marker.read_text() != token or marker.stat().st_uid != os.geteuid():
        raise RuntimeError('Private session owner marker mismatch')
    if root.stat().st_uid != os.geteuid() or stat.S_IMODE(root.stat().st_mode) != 0o700:
        raise RuntimeError('Private root ownership/0700 mode required')
    if os.environ.get('MEOWCHAN_DESKTOP_SMOKE_XVFB') != '1' or not os.environ.get('DISPLAY'):
        raise RuntimeError('Runner private Xvfb session required')
    for variable, leaf in [('XDG_RUNTIME_DIR', 'runtime'), ('HOME', 'home'), ('XDG_STATE_HOME', 'state')]:
        path = Path(os.environ.get(variable, ''))
        if path != root / leaf or path.is_symlink() or stat.S_IMODE(path.stat().st_mode) != 0o700:
            raise RuntimeError('Exact runner-owned 0700 ' + variable + ' required')
    variables = ['DBUS_SESSION_BUS_ADDRESS'] + (['AT_SPI_BUS_ADDRESS'] if require_atspi else [])
    for variable in variables:
        address = os.environ.get(variable, '')
        if ';' in address or not address.startswith('unix:path='):
            raise RuntimeError('Explicit private filesystem socket required: ' + variable)
        socket = Path(unquote(address.split('unix:path=', 1)[1].split(',', 1)[0]))
        if socket.is_symlink() or not socket.is_absolute() or not socket.resolve(strict=True).is_relative_to(root / 'runtime'):
            raise RuntimeError('Bus socket is outside the owned runtime: ' + variable)
        if not stat.S_ISSOCK(socket.stat().st_mode):
            raise RuntimeError('Bus path is not a socket: ' + variable)
    return root
