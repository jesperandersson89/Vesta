"""Persistent client identity management."""

import json
import secrets
import string
from pathlib import Path

VESTA_DIR = Path.home() / ".vesta"


def load_or_create_identity(prefix: str) -> str:
    """
    Load or create a persistent client identity.

    Stores a stable clientId in ~/.vesta/{prefix}-identity.json.
    Returns the clientId string.
    """
    VESTA_DIR.mkdir(exist_ok=True)
    path = VESTA_DIR / f"{prefix}-identity.json"

    if path.exists():
        data = json.loads(path.read_text(encoding="utf-8"))
        return data["clientId"]

    alphabet = string.ascii_letters + string.digits + "-_"
    client_id = "".join(secrets.choice(alphabet) for _ in range(22))
    path.write_text(
        json.dumps({"clientId": client_id}, indent=2),
        encoding="utf-8",
    )
    return client_id
