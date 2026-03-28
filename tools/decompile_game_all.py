#!/usr/bin/env python3
"""
Decompile every *.dll under GHPC_Data/Managed into artifacts/decompiled-all
(one subdirectory per assembly, ilspycmd -p).

Game root resolution matches tools/decompile-game.ps1:
  GHPC_GAME_DIR env, else <GHPCGameDir> from Directory.Build.props.

Requires: ilspycmd on PATH (dotnet tool install -g ilspycmd).
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional


def _repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def _game_dir_from_props(props_path: Path) -> Optional[str]:
    if not props_path.is_file():
        return None
    text = props_path.read_text(encoding="utf-8")
    m = re.search(r"<GHPCGameDir>([^<]+)</GHPCGameDir>", text)
    return m.group(1).strip() if m else None


def resolve_game_dir() -> Path:
    env = (os.environ.get("GHPC_GAME_DIR") or "").strip()
    if env:
        return Path(env)
    gd = _game_dir_from_props(_repo_root() / "Directory.Build.props")
    if gd:
        return Path(gd)
    print(
        "Set GHPC_GAME_DIR or define GHPCGameDir in Directory.Build.props",
        file=sys.stderr,
    )
    sys.exit(1)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Decompile all Managed/*.dll to artifacts/decompiled-all"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="List assemblies only; do not run ilspycmd",
    )
    parser.add_argument(
        "--max",
        type=int,
        default=0,
        metavar="N",
        help="Process only the first N DLLs (order by name), for smoke tests",
    )
    args = parser.parse_args()

    game = resolve_game_dir()
    managed = game / "GHPC_Data" / "Managed"
    if not managed.is_dir():
        print(f"Not found: {managed}", file=sys.stderr)
        sys.exit(1)

    out_root = _repo_root() / "artifacts" / "decompiled-all"
    ilspy = shutil.which("ilspycmd")
    if not ilspy and not args.dry_run:
        print(
            "ilspycmd not on PATH. Install: dotnet tool install -g ilspycmd",
            file=sys.stderr,
        )
        sys.exit(1)

    dlls = sorted(managed.glob("*.dll"))
    if args.max and args.max > 0:
        dlls = dlls[: args.max]

    if not dlls:
        print(f"No DLLs in {managed}", file=sys.stderr)
        sys.exit(1)

    print(f"Game:  {game}")
    print(f"Managed: {managed}")
    print(f"Output: {out_root}")
    print(f"Assemblies: {len(dlls)}")

    if args.dry_run:
        for dll in dlls:
            print(f"  would decompile -> {out_root / dll.stem}")
        return

    out_root.mkdir(parents=True, exist_ok=True)

    failed: list[str] = []
    for i, dll in enumerate(dlls, 1):
        target = out_root / dll.stem
        print(f"[{i}/{len(dlls)}] {dll.name} -> {target.name}/ ...")
        if target.exists():
            shutil.rmtree(target)
        target.mkdir(parents=True, exist_ok=True)
        r = subprocess.run(
            [ilspy, str(dll), "-o", str(target), "-p"],
            capture_output=True,
            text=True,
        )
        if r.returncode != 0:
            print(f"  FAILED ({r.returncode}): {dll.name}", file=sys.stderr)
            if r.stderr.strip():
                print(r.stderr, file=sys.stderr)
            failed.append(dll.name)

    if failed:
        print(f"Finished with {len(failed)} failure(s): {', '.join(failed)}", file=sys.stderr)
        sys.exit(1)
    print("Done.")


if __name__ == "__main__":
    main()
