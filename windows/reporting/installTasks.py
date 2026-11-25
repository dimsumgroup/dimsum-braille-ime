import hashlib
import os
from pathlib import Path

from logHandler import log
from NVDAState import WritePaths


def onInstall():
    """Record the profile used by this copy of NVDA for a later Dimsum removal."""
    try:
        profile = str(Path(WritePaths.configDir).resolve())
        destination = Path(os.environ["LOCALAPPDATA"]) / "DimsumInput" / "nvda-addon-profiles"
        destination.mkdir(parents=True, exist_ok=True)
        identity = hashlib.sha256(profile.casefold().encode("utf-8")).hexdigest()
        (destination / f"{identity}.path").write_text(profile, encoding="utf-8")
    except (OSError, KeyError, TypeError):
        log.warning("Could not record the Dimsum add-on profile", exc_info=True)
