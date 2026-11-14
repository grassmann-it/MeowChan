"""Static Motion subscription regression checks; no .NET, bus, desktop or OS reads.

These guard the Notification<T> migration's source contract. Compilation and
runtime validation belong to the parent's centralized heavy gate, not this suite.
"""
from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]


class OsMotionSignalSubscriptionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (ROOT / 'Desktop/Motion/OsMotionSignal.cs').read_text(encoding='utf-8')
        cls.source = re.sub(r'//[^\n]*', '', source)
        cls.watch = cls.source.split('private static async Task WatchPortalAsync(', 1)[1].split(
            'private const string PortalNamespace', 1)[0]
        cls.subscription = cls.watch.split('await connection.AddMatchAsync<', 1)[1].split(
            '.AsTask().WaitAsync(timeout.Token);', 1)[0]

    def test_notification_overload_keeps_explicit_context_flags_and_state(self):
        self.assertRegex(self.subscription, r'\bnotification\s*=>\s*\{')
        self.assertRegex(self.subscription, r'\},\s*emitOnCapturedContext:\s*false,\s*'
                         r'flags:\s*ObserverFlags\.None,\s*state:\s*null\)\s*$')
        self.assertNotIn('notification.Exception', self.subscription)
        self.assertNotIn('#pragma', self.source)

    def test_only_successful_target_boolean_notifications_reach_the_ui(self):
        # HasValue must short-circuit before Value (Value throws for completion/default notifications).
        # Namespace/key and nullable-bool checks still gate the *only* dispatch.
        self.assertRegex(self.subscription, r'if\s*\(notification\.HasValue\s*&&\s*'
                         r'notification\.Value\s+is\s*\{\s*Namespace:\s*PortalNamespace,\s*'
                         r'Key:\s*PortalKey,\s*Value:\s*\{\s*\}\s*now\s*\}\)\s*\{\s*'
                         r'Dispatcher\.UIThread\.Post\(\(\)\s*=>\s*changed\(PortalSays\(now\)\)\);\s*\}')
        self.assertEqual(1, self.subscription.count('notification.Value'))
        self.assertEqual(1, self.subscription.count('Dispatcher.UIThread.Post'))
        self.assertIn('PortalNamespace = "org.gnome.desktop.interface", PortalKey = "enable-animations"', self.source)

    def test_match_rule_and_message_reader_are_unchanged(self):
        for field, value in (
            ('Type', 'MessageType.Signal'),
            ('Sender', '"org.freedesktop.portal.Desktop"'),
            ('Path', '"/org/freedesktop/portal/desktop"'),
            ('Interface', '"org.freedesktop.portal.Settings"'),
            ('Member', '"SettingChanged"'),
        ):
            self.assertRegex(self.watch, rf'\b{field}\s*=\s*{re.escape(value)}')
        self.assertIn('(string Namespace, string Key, bool? Value)>(rule, '
                      'static (message, _) => ReadSettingChanged(message)', self.subscription)
        self.assertIn('bool? value = reader.ReadSignatureAsString() == "b" ? reader.ReadBool() : null;', self.source)

    def test_timeout_retention_and_failure_cleanup_are_preserved(self):
        self.assertIn('using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));', self.watch)
        self.assertEqual(3, self.watch.count('WaitAsync(timeout.Token)'))
        self.assertIn('await connection.ConnectAsync().AsTask().WaitAsync(timeout.Token);', self.watch)
        self.assertRegex(self.watch, r'bool\? initial = await connection\.CallMethodAsync\(ReadOne\(connection\),'
                         r'\s*static \(message, _\) => ReadBoolVariant\(message\), null\)\.WaitAsync\(timeout\.Token\);')
        self.assertRegex(self.watch, r'bool kept = false;\s*try\s*\{')
        self.assertRegex(self.watch, r'\}, emitOnCapturedContext: false, flags: ObserverFlags\.None, state: null\)'
                         r'\.AsTask\(\)\.WaitAsync\(timeout\.Token\);\s*'
                         r'_portal = connection;\s*kept = true;\s*\}\s*finally\s*\{\s*'
                         r'if \(!kept\) connection\.Dispose\(\);\s*\}')
        self.assertIn('catch (OperationCanceledException) { RecordUnavailableUnlessDetected("settings portal", '
                      '"no answer from the settings portal within 3 s"); }', self.watch)
        self.assertIn('catch (Exception exception) { RecordUnavailableUnlessDetected("settings portal", '
                      'exception.GetType().Name + ": " + exception.Message); }', self.watch)


if __name__ == '__main__':
    unittest.main()
