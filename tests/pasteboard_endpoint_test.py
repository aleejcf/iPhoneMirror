"""Recover a stale pasteboard endpoint without touching HID or issuing a paste twice."""
import asyncio
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import usb_touch_bridge as bridge

NAME = bridge.PasteboardService.SERVICE_NAME

class PasteboardEndpointTests(unittest.IsolatedAsyncioTestCase):
    def session(self):
        session = bridge.TouchSession(SimpleNamespace(emit=AsyncMock()), 120)
        session.rsd = SimpleNamespace(service=SimpleNamespace(address=('device', 123)),
            open_connection=AsyncMock(), udid='same-device',
            peer_info={'Services': {NAME: {'Port': '100'}, 'hid': {'Port': '200'}}})
        return session

    def discovery(self, identity='same-device', services=None):
        return SimpleNamespace(connect=AsyncMock(), send_device_handshake=AsyncMock(),
            receive_response=AsyncMock(return_value={'Properties': {'UniqueDeviceID': identity},
                'Services': services if services is not None else {NAME: {'Port': '101'}}}),
            __aexit__=AsyncMock())

    def connection_type(self, discovery):
        constructor = Mock(return_value=discovery)
        # Keep the patched symbol a type: production cleanup uses isinstance.
        connection = type('DiscoveryConnection', (), {
            '__new__': lambda cls, *args, **kwargs: constructor(*args, **kwargs)})
        return connection, constructor

    async def test_connection_failure_rediscovery_then_one_set_and_one_paste(self):
        session = self.session()
        old_rsd = session.rsd
        failed = SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()), __aexit__=AsyncMock())
        live = SimpleNamespace(__aenter__=AsyncMock(), __aexit__=AsyncMock(), set_text=AsyncMock())
        discovery = self.discovery()
        connection_type, connection = self.connection_type(discovery)
        with patch.object(bridge, 'PasteboardService', side_effect=[failed, live]) as factory, \
             patch.object(bridge, 'RemoteXPCConnection', connection_type):
            factory.SERVICE_NAME = NAME
            session._send_keyboard_report = AsyncMock()
            await session._apply_paste_text('中文')
        self.assertIs(session.rsd, old_rsd)
        self.assertEqual({'Port': '200'}, session.rsd.peer_info['Services']['hid'])
        self.assertEqual({'Port': '101'}, session.rsd.peer_info['Services'][NAME])
        connection.assert_called_once_with(('device', 123), open_connection=old_rsd.open_connection)
        live.set_text.assert_awaited_once_with('中文')
        self.assertEqual([[0xE3], [0xE3, 0x19], [0xE3], []],
                         [call.args[0] for call in session._send_keyboard_report.await_args_list])
        failed.__aexit__.assert_awaited_once()
        discovery.__aexit__.assert_awaited_once()
        self.assertFalse(session._pasteboard_refresh_required)

    async def test_foreign_identity_or_missing_service_never_updates_catalogue(self):
        for identity, services in [('another-device', None), ('same-device', {})]:
            with self.subTest(identity=identity, services=services):
                session = self.session()
                session._pasteboard_refresh_required = True
                discovery = self.discovery(identity, services)
                connection_type, _ = self.connection_type(discovery)
                with patch.object(bridge, 'RemoteXPCConnection', connection_type):
                    with self.assertRaises(RuntimeError):
                        await session._refresh_pasteboard_endpoint()
                self.assertEqual({'Port': '100'}, session.rsd.peer_info['Services'][NAME])
                self.assertTrue(session._pasteboard_refresh_required)
                discovery.__aexit__.assert_awaited_once()

    async def test_cancelled_rediscovery_closes_only_its_own_connection(self):
        session = self.session()
        discovery = self.discovery()
        discovery.connect.side_effect = asyncio.CancelledError()
        connection_type, _ = self.connection_type(discovery)
        with patch.object(bridge, 'RemoteXPCConnection', connection_type):
            with self.assertRaises(asyncio.CancelledError):
                await session._refresh_pasteboard_endpoint()
        discovery.__aexit__.assert_awaited_once()
        self.assertEqual({'Port': '100'}, session.rsd.peer_info['Services'][NAME])

    async def test_unavailable_service_after_refresh_never_sends_paste_chord(self):
        session = self.session()
        discovery = self.discovery(services={NAME: {'Port': '100'}})
        connection_type, _ = self.connection_type(discovery)
        instances = []
        def service(_rsd):
            instance = SimpleNamespace(__aenter__=AsyncMock(side_effect=TimeoutError()),
                __aexit__=AsyncMock(), set_text=AsyncMock())
            instances.append(instance)
            return instance
        session._send_keyboard_report = AsyncMock()
        with patch.object(bridge, 'PasteboardService', side_effect=service) as factory, \
             patch.object(bridge, 'RemoteXPCConnection', connection_type):
            factory.SERVICE_NAME = NAME
            with self.assertRaises(TimeoutError):
                await session._apply_paste_text('中文')
        session._send_keyboard_report.assert_not_awaited()
        self.assertEqual(2, len(instances))
        for instance in instances:
            instance.set_text.assert_not_awaited()
            instance.__aexit__.assert_awaited_once()
        self.assertTrue(session._pasteboard_refresh_required)

if __name__ == '__main__':
    unittest.main()
