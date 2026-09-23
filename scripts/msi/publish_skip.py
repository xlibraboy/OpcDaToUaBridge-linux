"""Runtime state that must never ship inside the installer or the portable zip.

The bridge creates these files (and the pki/ and displays/ directories) on the
target machine at first run. Shipping a host's copy would overwrite live config
on upgrade, so both distributions filter them out. Shared by harvest.py (MSI)
and make-zip.py (portable) so the two cannot drift apart.
"""

SKIP_DIRECTORIES = {"pki", "displays"}

SKIP_FILES = {
    "users.json", "mappings.json", "sources.json", "links.json",
    "mqtt.json", "influx.json", "instance.lock",
}


def is_runtime_state(relative) -> bool:
    """True when a path relative to a publish root is machine-specific state."""
    return (
        any(part in SKIP_DIRECTORIES for part in relative.parts)
        or relative.name in SKIP_FILES
    )
