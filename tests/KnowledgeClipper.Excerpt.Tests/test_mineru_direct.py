"""Offline regression for the observed TLS failure; no sockets or secrets."""
import http.client
import json
import ssl
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "helpers"))
import mineru_direct


class DirectConnectTests(unittest.TestCase):
    def setUp(self):
        self.provider = types.SimpleNamespace(physical_ipv4_addresses=lambda: ["192.0.2.10"], direct_subprocess_env=lambda: {"NO_PROXY": "*"})
        self.route = ("192.0.2.10", "93.184.216.34")

    def connection(self):
        connection = http.client.HTTPSConnection("public.example", timeout=5)
        connection._create_connection = Mock(return_value=Mock())
        return connection

    def test_normal_connect_never_discovers_another_route(self):
        with patch.object(http.client.HTTPSConnection, "connect", Mock()) as original, patch.object(mineru_direct, "_physical_routes") as discovery:
            undo = mineru_direct.install_physical_direct(self.provider)
            try:
                self.connection().connect()
                original.assert_called_once()
                discovery.assert_not_called()
            finally:
                undo()

    def test_tls_eof_uses_dynamic_destination_and_keeps_hostname_and_certificate_checks(self):
        calls = []

        def original(connection):
            calls.append(connection.source_address)
            if connection.source_address is None:
                raise ssl.SSLEOFError("observed TLS handshake EOF")
            self.assertEqual(connection.host, "public.example")
            self.assertTrue(connection._context.check_hostname)
            self.assertEqual(connection._context.verify_mode, ssl.CERT_REQUIRED)
            connection.sock = connection._create_connection((connection.host, 443), connection.timeout, connection.source_address)

        with patch.object(http.client.HTTPSConnection, "connect", original), patch.object(mineru_direct, "_physical_routes", return_value=[self.route]) as discovery:
            undo = mineru_direct.install_physical_direct(self.provider)
            try:
                connection = self.connection(); create = connection._create_connection
                connection.connect()
                self.assertEqual(calls, [None, (self.route[0], 0)])
                create.assert_called_once_with((self.route[1], 443), 5, (self.route[0], 0))
                self.assertIsNone(connection.source_address)
                self.assertIs(connection._create_connection, create)
                discovery.assert_called_once_with(self.provider, "public.example")
                second = self.connection(); second.connect()
                self.assertEqual(calls[-1], (self.route[0], 0))
                self.assertEqual(len(calls), 3, "a successful direct route is reused before the fake-IP route")
            finally:
                undo()

    def test_certificate_failure_is_never_disabled_or_accepted(self):
        with patch.object(http.client.HTTPSConnection, "connect", Mock(side_effect=ssl.SSLCertVerificationError("fixture certificate failure"))) as original, \
                patch.object(mineru_direct, "_physical_routes", return_value=[self.route]):
            undo = mineru_direct.install_physical_direct(self.provider)
            try:
                with self.assertRaises(OSError): self.connection().connect()
                self.assertEqual(original.call_count, 2)
            finally:
                undo()

    def test_proxy_tunnel_is_not_a_recovery_target(self):
        with patch.object(http.client.HTTPSConnection, "connect", Mock(side_effect=TimeoutError())), patch.object(mineru_direct, "_physical_routes") as discovery:
            undo = mineru_direct.install_physical_direct(self.provider)
            try:
                connection = self.connection(); connection._tunnel_host = "proxy.example"
                with self.assertRaises(TimeoutError): connection.connect()
                discovery.assert_not_called()
            finally:
                undo()

    def test_send_failure_does_not_replay_an_api_submission(self):
        with patch.object(http.client.HTTPSConnection, "connect", Mock()) as original, patch.object(mineru_direct, "_physical_routes") as discovery:
            undo = mineru_direct.install_physical_direct(self.provider)
            try:
                connection = self.connection()
                with patch.object(connection, "send", side_effect=ConnectionResetError("request send failed")) as send:
                    with self.assertRaises(ConnectionResetError): connection.request("POST", "/file-urls/batch", body=b"public fixture")
                    self.assertEqual(send.call_count, 1)
                discovery.assert_not_called()
                self.assertEqual(original.call_count, 0)
            finally:
                undo()

    def test_https_dns_is_physical_direct_and_excludes_fake_addresses(self):
        answer = {"Answer": [{"type": 1, "data": "198.18.1.152"}, {"type": 1, "data": "93.184.216.34"}, {"type": 5, "data": "alias.example"}]}
        with patch.object(mineru_direct.shutil, "which", return_value="curl.exe"), \
                patch.object(mineru_direct.subprocess, "run", return_value=types.SimpleNamespace(stdout=json.dumps(answer))) as run:
            self.assertEqual(mineru_direct._physical_routes(self.provider, "public.example"), [self.route])
            args, kwargs = run.call_args
            command = args[0]
            self.assertIn("--interface", command)
            self.assertIn("--noproxy", command)
            self.assertIn("name=public.example", command[-1])
            self.assertNotIn("--insecure", command)
            self.assertEqual(kwargs["env"], {"NO_PROXY": "*"})

    def test_offline_legacy_provider_without_adapter_interface_is_unchanged(self):
        original = http.client.HTTPSConnection.connect
        undo = mineru_direct.install_physical_direct(types.SimpleNamespace())
        self.assertIs(http.client.HTTPSConnection.connect, original)
        undo()


if __name__ == "__main__":
    unittest.main(verbosity=2)
