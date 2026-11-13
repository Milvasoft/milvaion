#!/usr/bin/env python3
"""Create an api key on a local Milvaion for the CLI and Terraform acceptance tests.

Logs in as rootuser and creates a key that holds every ScheduledJobManagement and
WorkerManagement permission. Prints the key on stdout.

    MILVAION_URL=http://localhost:5000 MILVAION_ROOT_PASSWORD=... python3 tools/scripts/create-dev-api-key.py

Only for local development stacks: it uses the root password.
"""
import json
import os
import sys
import urllib.request

URL = os.environ.get("MILVAION_URL", "http://localhost:5000").rstrip("/") + "/api/v1"
PASSWORD = os.environ.get("MILVAION_ROOT_PASSWORD")
PREFIXES = ("ScheduledJobManagement.", "WorkerManagement.")


def call(method, path, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(URL + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req) as resp:
        env = json.load(resp)
    if not env.get("isSuccess"):
        sys.exit(f"{method} {path} failed: {env.get('messages')}")
    return env.get("data")


def walk(node, out):
    """Collect {id, name} permission entries from whatever shape the permissions endpoint returns."""
    if isinstance(node, dict):
        name = node.get("name") or node.get("permissionName")
        group = node.get("permissionGroup") or node.get("groupName")
        if "id" in node and isinstance(node.get("id"), int) and name:
            out.append((node["id"], f"{group}.{name}" if group and "." not in name else name))
        for v in node.values():
            walk(v, out)
    elif isinstance(node, list):
        for v in node:
            walk(v, out)


def main():
    if not PASSWORD:
        sys.exit("set MILVAION_ROOT_PASSWORD")
    login = call("POST", "/account/login", {"userName": "rootuser", "password": PASSWORD, "deviceId": "milvaion-cli-dev"})
    token = login["token"]["accessToken"] if isinstance(login.get("token"), dict) else login.get("accessToken")
    perms = []
    walk(call("PATCH", "/permissions", {}, token), perms)
    ids = sorted({pid for pid, name in perms if name.startswith(PREFIXES)})
    if not ids:
        sys.exit("no ScheduledJobManagement/WorkerManagement permissions found")
    created = call("POST", "/apikeys/apiKey",
                   {"name": "milvaion-cli dev", "description": "Created by tools/scripts/create-dev-api-key.py", "permissionIdList": ids},
                   token)
    print(created.get("key") or created.get("apiKey") or created.get("plainKey"))


if __name__ == "__main__":
    main()
