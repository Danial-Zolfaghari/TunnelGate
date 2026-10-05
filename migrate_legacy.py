#!/usr/bin/env python3
"""One-time TunnelGate 2.x -> TunnelGate Native migration helper.

This script is NOT part of the Native runtime. It imports the old Python backend
only to decrypt the existing SQLite vault, then writes a temporary plaintext JSON
that TunnelGate Native can import. Delete the JSON immediately after importing it.
"""
from __future__ import annotations

import argparse
import getpass
import json
import os
import sys
from pathlib import Path


def tunnel(t: dict) -> dict:
    return {
        "uid": str(t.get("uid") or ""),
        "name": str(t.get("name") or ""),
        "mode": str(t.get("mode") or "reverse"),
        "remoteHost": str(t.get("remote_host") or ""),
        "remoteSshPort": int(t.get("remote_ssh_port") or 22),
        "remoteUser": str(t.get("remote_user") or ""),
        "password": str(t.get("password") or ""),
        "savePassword": bool(t.get("save_password", False)),
        "keyFile": str(t.get("key_file") or ""),
        "remoteBind": str(t.get("remote_bind") or "127.0.0.1"),
        "remotePort": int(t.get("remote_port") or 0),
        "localHost": str(t.get("local_host") or "127.0.0.1"),
        "localPort": int(t.get("local_port") or 0),
        "enabled": bool(t.get("enabled", True)),
        "autoRestart": bool(t.get("auto_restart", False)),
        "autoRestartDays": int(t.get("auto_restart_days") or 0),
        "autoRestartHours": int(t.get("auto_restart_hours") or 0),
        "autoRestartMinutes": int(t.get("auto_restart_minutes") or 60),
    }


def settings(s: dict) -> dict:
    return {
        "language": str(s.get("language") or "en"),
        "reconnect": bool(s.get("reconnect", True)),
        "reconnectDelay": int(s.get("reconnect_delay") or 5),
        "keepAlive": int(s.get("keepalive") or 30),
        "compression": bool(s.get("compression", False)),
        "autoStoreHostKey": bool(s.get("auto_store_hostkey", False)),
        "tunnels": [tunnel(x) for x in (s.get("tunnels") or [])],
    }


def convert(v: dict) -> dict:
    return {
        "language": str(v.get("language") or "en"),
        "activeProfileUid": str(v.get("active_profile_uid") or ""),
        "categories": [
            {
                "uid": str(c.get("uid") or ""),
                "name": str(c.get("name") or "Default"),
                "enabled": bool(c.get("enabled", True)),
                "sortOrder": int(c.get("sort_order") or 0),
            }
            for c in (v.get("categories") or [])
        ],
        "profiles": [
            {
                "uid": str(p.get("uid") or ""),
                "categoryUid": str(p.get("category_uid") or ""),
                "name": str(p.get("name") or "Default"),
                "enabled": bool(p.get("enabled", True)),
                "settings": settings(p.get("settings") or {}),
            }
            for p in (v.get("profiles") or [])
        ],
        "autoStartTunnels": bool(v.get("auto_start_tunnels", True)),
        "launchAtStartup": bool(v.get("launch_at_startup", True)),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Export a TunnelGate 2.x vault for one-time Native import")
    parser.add_argument("--backend", required=True, help="Path to the OLD TunnelGate backend directory containing rt_core.py")
    parser.add_argument("--data-dir", required=True, help="Exact old RT_DATA_DIR containing vault.db")
    parser.add_argument("--output", default="TunnelGate-migration.tglegacy.json")
    args = parser.parse_args()

    backend = Path(args.backend).resolve()
    data_dir = Path(args.data_dir).resolve()
    if not (backend / "rt_core.py").exists():
        raise SystemExit(f"rt_core.py not found in: {backend}")
    if not (data_dir / "vault.db").exists():
        raise SystemExit(f"vault.db not found in: {data_dir}")

    os.environ["RT_DATA_DIR"] = str(data_dir)
    sys.path.insert(0, str(backend))
    from rt_core import VaultStore, vault_to_dict  # type: ignore

    store = VaultStore(app_dir=str(data_dir))
    if not store.has_vault():
        raise SystemExit("Old vault is empty / not initialized.")

    unlocked = False
    try:
        unlocked = store.try_restore_session()
    except Exception:
        unlocked = False
    if not unlocked:
        password = getpass.getpass("Old TunnelGate master password: ")
        if not store.unlock(password):
            raise SystemExit("Wrong password or the data-dir does not match the original vault path.")

    raw = vault_to_dict(store.load_vault(from_db=True))
    out = Path(args.output).resolve()
    out.write_text(json.dumps(convert(raw), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Exported: {out}")
    print("WARNING: this migration JSON is plaintext and may contain saved SSH passwords.")
    print("Import it in TunnelGate Native, then delete it immediately.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
