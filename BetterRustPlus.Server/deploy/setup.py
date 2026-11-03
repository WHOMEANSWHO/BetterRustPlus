#!/usr/bin/env python3
"""Default BetterRustPlus installer: native Linux/systemd, without Docker."""
from setup_native import main

if __name__ == "__main__":
    raise SystemExit(main())
