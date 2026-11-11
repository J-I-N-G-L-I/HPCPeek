"""Dev-only loopback SSH servers. Never connects to AIRE; all credentials are synthetic."""
import argparse
import json
import logging
import select
import socket
import threading
import time
from pathlib import Path

import paramiko

logging.getLogger("paramiko").setLevel(logging.CRITICAL)
parser = argparse.ArgumentParser()
parser.add_argument("--state", required=True)
parser.add_argument("--stop", required=True)
args = parser.parse_args()
state_path, stop_path = Path(args.state), Path(args.stop)
key = paramiko.RSAKey.generate(2048)
lock = threading.Lock()
state = {"paramikoVersion": paramiko.__version__, "ports": {}, "counters": {}}
listeners = {}


def save():
    # Called with lock. Replace the file so readers never observe partial JSON.
    temporary = state_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(state, indent=2), encoding="utf-8")
    temporary.replace(state_path)


def increment(name, field):
    with lock:
        state["counters"][name][field] += 1
        save()


class Server(paramiko.ServerInterface):
    def __init__(self, name):
        self.name = name
        self.round = 0
        self.password_accepted = False
        self.destinations = {}

    def get_allowed_auths(self, username):
        if self.name == "password_then_duo":
            return "keyboard-interactive" if self.password_accepted else "password,keyboard-interactive"
        return "password" if self.name == "password" else "keyboard-interactive"

    def check_auth_password(self, username, password):
        increment(self.name, "authStarts")
        increment(self.name, "authRounds")
        if self.name == "password" and username == "fixture-user" and password == "fake-password":
            return paramiko.AUTH_SUCCESSFUL
        if self.name == "password_then_duo" and username == "fixture-user" and password == "fake-password":
            self.password_accepted = True
            return paramiko.AUTH_PARTIALLY_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_auth_interactive(self, username, submethods):
        increment(self.name, "authStarts")
        if username != "fixture-user":
            return paramiko.AUTH_FAILED
        if self.name == "password_then_duo":
            if not self.password_accepted:
                return paramiko.AUTH_FAILED
            self.round = 2
            return paramiko.InteractiveQuery("Synthetic Duo", "7 = Duo push; 9 = SMS", ("Choose a method:", True))
        self.round = 1
        return paramiko.InteractiveQuery("Synthetic login", "Fixture password", ("Password:", False))

    def check_auth_interactive_response(self, responses):
        increment(self.name, "authRounds")
        if self.round == 1 and responses == ["fake-password"]:
            self.round = 2
            return paramiko.InteractiveQuery("Synthetic Duo", "7 = Duo push; 9 = SMS", ("Choose a method:", True))
        if self.round == 2 and responses == ["7"]:
            self.round = 3
            return paramiko.InteractiveQuery("Synthetic approval", "Approve in Duo Mobile (simulated)")
        if self.round == 3 and responses == []:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_direct_tcpip_request(self, channel_id, origin, destination):
        targets = [("localhost", state["ports"][name]) for name in ("target", "password_then_duo")]
        if self.name != "gateway" or destination not in targets:
            return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
        self.destinations[channel_id] = destination
        increment(self.name, "forwards")
        return paramiko.OPEN_SUCCEEDED

    def check_channel_exec_request(self, channel, command):
        # Commands are counted, not recorded. Fixture never runs a shell.
        increment(self.name, "commands")

        def reply():
            time.sleep(0.02)
            try:
                channel.sendall(("mock:" + self.name + "\n").encode())
                channel.send_exit_status(0)
                channel.shutdown_write()
                channel.close()
            except (OSError, EOFError):
                pass

        threading.Thread(target=reply, daemon=True).start()
        return True


def relay(channel, destination):
    with socket.create_connection(destination, timeout=10) as downstream:
        try:
            while not stop_path.exists():
                ready, _, _ = select.select([channel, downstream], [], [], 0.5)
                for source in ready:
                    data = source.recv(65536)
                    if not data:
                        return
                    (downstream if source is channel else channel).sendall(data)
        except (OSError, EOFError):
            pass
        finally:
            channel.close()


def serve_connection(name, sock):
    transport = paramiko.Transport(sock)
    transport.add_server_key(key)
    transport.auth_timeout = 30
    server = Server(name)
    channels = []
    try:
        transport.start_server(server=server)
        while transport.is_active() and not stop_path.exists():
            channel = transport.accept(0.2)
            if channel is not None:
                channels.append(channel)
                if channel.chanid in server.destinations:
                    threading.Thread(target=relay, args=(channel, server.destinations[channel.chanid]), daemon=True).start()
    except (paramiko.SSHException, OSError, EOFError):
        pass
    finally:
        transport.close()


def accept_connections(name, listener):
    while not stop_path.exists():
        try:
            sock, _ = listener.accept()
            threading.Thread(target=serve_connection, args=(name, sock), daemon=True).start()
        except socket.timeout:
            continue
        except OSError:
            if stop_path.exists():
                return
            raise


for name in ("target", "gateway", "password", "password_then_duo"):
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(8)
    listener.settimeout(0.5)
    listeners[name] = listener
    state["ports"][name] = listener.getsockname()[1]
    state["counters"][name] = {"authStarts": 0, "authRounds": 0, "commands": 0, "forwards": 0}
with lock:
    save()
for name, listener in listeners.items():
    threading.Thread(target=accept_connections, args=(name, listener), daemon=True).start()
while not stop_path.exists():
    time.sleep(0.1)
for listener in listeners.values():
    listener.close()
