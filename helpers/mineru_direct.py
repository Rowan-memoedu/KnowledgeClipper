"""Recover HTTPS connection setup through the installed runner's direct route.

The hook is local to one MinerU child process. It retries only connect/TLS setup,
before HTTP headers or request bodies are sent; API submissions are never replayed
after a send/read failure. The original HTTPSConnection still checks TLS and SNI.
"""
from __future__ import annotations

import http.client
import ipaddress
import json
import os
import shutil
import subprocess
import time
from urllib.parse import urlencode


def _physical_routes(provider, hostname):
    curl = shutil.which("curl.exe") or shutil.which("curl")
    if not curl:
        raise OSError("Physical direct connection requires curl")
    routes = []
    for source in provider.physical_ipv4_addresses():
        try:
            result = subprocess.run(
                [curl, "-q", "--noproxy", "*", "--interface", source,
                 "--connect-timeout", "10", "--max-time", "30", "--fail", "--silent", "--show-error",
                 "--resolve", "dns.alidns.com:443:223.5.5.5",
                 "https://dns.alidns.com/resolve?" + urlencode({"name": hostname, "type": "A"})],
                capture_output=True, text=True, timeout=40, check=True,
                env=provider.direct_subprocess_env(),
            )
            answers = json.loads(result.stdout).get("Answer", [])
        except (OSError, subprocess.SubprocessError, ValueError):
            continue
        for answer in answers:
            if answer.get("type") != 1:
                continue
            try:
                address = ipaddress.IPv4Address(answer["data"])
            except (ValueError, KeyError):
                continue
            if address.is_global and (source, str(address)) not in routes:
                routes.append((source, str(address)))
    if not routes:
        raise OSError("HTTPS DNS returned no physical direct route")
    return routes


def install_physical_direct(provider):
    """Return an undo callback; shared runner files/configuration are not modified."""
    if os.name != "nt" or not all(callable(getattr(provider, name, None)) for name in
                                  ("physical_ipv4_addresses", "direct_subprocess_env")):
        return lambda: None
    original = http.client.HTTPSConnection.connect
    routes = {}

    def close_socket(connection):
        if connection.sock is not None:
            connection.sock.close()
            connection.sock = None

    def connect_route(connection, route):
        source, address = route
        create = connection._create_connection
        initial_source = connection.source_address
        try:
            # HTTPConnection owns socket setup; HTTPSConnection owns TLS/SNI.
            # Only the TCP destination and source address change.
            connection.source_address = (source, 0)
            connection._create_connection = lambda target, timeout, source_address: create(
                (address, target[1]), timeout, source_address)
            return original(connection)
        finally:
            connection._create_connection = create
            connection.source_address = initial_source

    def connect(connection):
        host = connection.host
        eligible = connection.port == 443 and not connection._tunnel_host
        cached = routes.get(host) if eligible else None
        if cached and cached[1] > time.monotonic():
            try:
                return connect_route(connection, cached[0])
            except OSError:
                close_socket(connection)
                routes.pop(host, None)
        try:
            return original(connection)
        except OSError as initial_error:
            close_socket(connection)
            if not eligible:
                raise
            try:
                candidates = _physical_routes(provider, host)
            except (OSError, subprocess.SubprocessError, ValueError) as route_error:
                raise OSError("Physical direct HTTPS discovery failed: " + type(route_error).__name__) from initial_error
            for route in candidates[:3]:
                try:
                    result = connect_route(connection, route)
                    routes[host] = (route, time.monotonic() + 60)
                    print("HTTPS connected through physical adapter (direct): " + host, flush=True)
                    return result
                except OSError:
                    close_socket(connection)
            raise OSError("Physical direct HTTPS connection failed for " + host) from initial_error

    http.client.HTTPSConnection.connect = connect
    return lambda: setattr(http.client.HTTPSConnection, "connect", original)
