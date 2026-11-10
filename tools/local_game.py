"""Build and run the private Diplomacy game from this single source tree."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid


ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "runtime"
GAME = RUNTIME / "game"
PROFILE = RUNTIME / "profile"
EXE_NAME = "Stronghold Crusader Definitive Edition.exe"
GAME_DATA = "Stronghold Crusader Definitive Edition_Data"
PLUGINS = GAME / "BepInEx" / "plugins"
MOD = PLUGINS / "StrongholdEuropeDE"
ASSEMBLY = "StrongholdEuropeDiplomacy.dll"
VERSION = "2.3.11"


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, data) -> None:
    temporary = path.with_name(path.name + ".incoming")
    temporary.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def check(condition, message: str) -> None:
    if not condition:
        raise ValueError(message)


def regular_path(path: Path) -> None:
    for component in (path, *path.parents):
        if component.exists():
            check(not (getattr(component.lstat(), "st_file_attributes", 0) & 0x400)
                  and not component.is_symlink(), f"Reparse point: {component}")


def inventory(root: Path) -> dict[str, str]:
    return {path.relative_to(root).as_posix(): sha(path)
            for path in sorted(root.rglob("*")) if path.is_file()}


def copy_tree(source: Path, target: Path) -> None:
    regular_path(source)
    for directory, children, files in os.walk(source, followlinks=False):
        for name in (*children, *files):
            path = Path(directory) / name
            check(not path.is_symlink() and not (getattr(path.lstat(), "st_file_attributes", 0) & 0x400),
                  f"Reparse point in game input: {path}")
    shutil.copytree(source, target, copy_function=shutil.copyfile,
                    ignore=shutil.ignore_patterns("*.pdb", "__pycache__"))


def prepare(args) -> None:
    check(not GAME.exists(), "The dedicated game is already prepared.")
    for p in (RUNTIME, args.template, args.developer_api, args.sdk, args.framework_refs):
        regular_path(p)
    template = args.template.resolve(strict=True)
    check(not template.is_relative_to(RUNTIME.resolve()), "Select an existing game copy as input.")
    expected = read_json(ROOT / "references/qa.json")["sha256"]
    checks = {
        f"{GAME_DATA}/Managed/Assembly-CSharp.dll": expected["AssemblyCSharpReference"],
        "BepInEx/core/BepInEx.dll": expected["BepInExReference"],
        "BepInEx/core/0Harmony.dll": expected["HarmonyReference"],
        "BepInEx/plugins/000shcdese/SHCDESE.dll": expected["SHCDESEReference"],
        EXE_NAME: "8acea6d11af81cf26b035a479ba633bdb0d09513742055f3f46330d72b2f203a",
    }
    for name, digest in checks.items():
        check(sha(template / name) == digest, f"Unsupported game file: {name}")
    api = args.developer_api.resolve(strict=True)
    check(sha(api / "APIShared.dll") == expected["APISharedReference"], "Developer APIShared mismatch.")
    locales = sorted((api / "Locales").glob("*.txt"))
    check(len(locales) == 21, "Incomplete developer APIShared locales.")
    overlay = ["APIShared.dll", "APIShared.Content.dll", "info.json",
               "Patches/Assets/GUI/XAMLResources/HUD_IngameMenu.xaml"]
    overlay += [f"Locales/{locale.name}" for locale in locales]
    for name in overlay:
        regular_path(api / name)
        check((api / name).is_file(), f"Missing developer APIShared file: {name}")
    RUNTIME.mkdir(parents=True, exist_ok=True)
    incoming = RUNTIME / ("game.incoming-" + uuid.uuid4().hex[:8])
    incoming.mkdir()
    for name in ("Assets", "MonoBleedingEdge", GAME_DATA, "BepInEx/core", "BepInEx/patchers"):
        source = template / name
        if source.is_dir():
            print(f"Preparing {name}", flush=True)
            copy_tree(source, incoming / name)
    for name in (EXE_NAME, "UnityPlayer.dll", "UnityCrashHandler64.exe", "msvcp140.dll",
                 "winhttp.dll", ".doorstop_version", "doorstop_config.ini", "steam_appid.txt"):
        source = template / name
        regular_path(source)
        if source.is_file():
            shutil.copyfile(source, incoming / name)
    for provider in ("000shcdese", "BugfixesAndQoL_Serp", "APIShared_Serp"):
        relative = Path("BepInEx/plugins") / provider
        copy_tree(template / relative, incoming / relative)
    config = incoming / "BepInEx/config"
    config.mkdir()
    shutil.copyfile(ROOT / "assets/config/000shcdese.cfg", config / "000shcdese.cfg")
    provider = incoming / "BepInEx/plugins/APIShared_Serp"
    for name in overlay:
        destination = provider / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(api / name, destination)
        check(sha(destination) == sha(api / name), f"Provider copy failed: {name}")
    for name, digest in checks.items():
        check(sha(incoming / name) == digest, f"Game copy failed: {name}")
    incoming.replace(GAME)
    write_json(RUNTIME / "prepared.json", {
        "game_files": checks,
        "providers": {provider: inventory(PLUGINS / provider)
                      for provider in ("000shcdese", "BugfixesAndQoL_Serp", "APIShared_Serp")},
        "sdk": str(args.sdk.resolve(strict=True)),
        "framework_refs": str(args.framework_refs.resolve(strict=True)),
    })
    print("Dedicated game prepared.", flush=True)


def index_sources() -> None:
    index = ROOT / "source-index.json"
    data = read_json(index)
    source = ROOT / "src"
    files = sorted(source.rglob("*.cs"), key=lambda path: path.as_posix())
    for path in files:
        regular_path(path)
    data["source_files"] = [
        {"path": path.relative_to(source).as_posix(),
         "stage_path": path.relative_to(ROOT).as_posix(),
         "sha256": sha(path)}
        for path in files
    ]
    write_json(index, data)
    index.with_suffix(".json.sha256").write_text(sha(index) + "\n", encoding="ascii")
    print(f"Indexed {len(files)} C# files.", flush=True)


def build() -> None:
    prepared = read_json(RUNTIME / "prepared.json")
    profile = "qa"
    api = PLUGINS / "APIShared_Serp/APIShared.dll"
    command = [sys.executable, "-B", str(ROOT / "build.py"),
               "--source-root", str(ROOT / "src"), "--source-index", str(ROOT / "source-index.json"),
               "--game-dir", str(GAME), "--loader-dir", str(GAME),
               "--extender-dir", str(PLUGINS / "000shcdese"), "--api-shared", str(api),
               "--sdk", prepared["sdk"], "--framework-refs", prepared["framework_refs"],
               "--dependency-lock", str(ROOT / "references" / (profile + ".json")),
               "--output-dir", str(ROOT / "build"), "--assembly-name", "StrongholdEuropeDiplomacy",
               "--root-namespace", "StrongholdEuropeDE.Runtime", "--version", VERSION,
               "--assembly-version", VERSION + ".0", "--file-version", VERSION + ".0",
               "--profile", profile, "--build"]
    subprocess.run(command, cwd=ROOT, check=True)

def module_id(path: Path) -> str:
    env = dict(os.environ, DIPLOMACY_METADATA_DLL=str(path))
    value = subprocess.check_output([
        "powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference='Stop'; "
        "$module = [Reflection.Assembly]::ReflectionOnlyLoadFrom($env:DIPLOMACY_METADATA_DLL); "
        "$module.ManifestModule.ModuleVersionId.ToString('D')"
    ], env=env, text=True).strip()
    return str(uuid.UUID(value))


def no_running_game() -> None:
    result = subprocess.run(["tasklist.exe", "/FI", f"IMAGENAME eq {EXE_NAME}", "/FO", "CSV", "/NH"],
                            capture_output=True, text=True, check=True, errors="replace")
    check(EXE_NAME.casefold() not in result.stdout.casefold(), "A Crusader process is already running.")


def install() -> None:
    no_running_game()
    regular_path(PLUGINS)
    prepared = read_json(RUNTIME / "prepared.json")
    check(sha(GAME / EXE_NAME) == prepared["game_files"][EXE_NAME], "Unexpected game install target.")
    result = read_json(ROOT / "build/qa/build-receipt.json")
    dll = ROOT / "build/qa/bin" / ASSEMBLY
    check(result["exit_code"] == 0 and result["artifact_sha256"] == sha(dll)
          and result["source_index_sha256"] == sha(ROOT / "source-index.json"), "Build receipt is stale.")
    stage = PLUGINS / (".StrongholdEuropeDE.install-" + uuid.uuid4().hex)
    check(not any(PLUGINS.glob(".StrongholdEuropeDE.install-*")), "An interrupted install needs inspection.")
    stage.mkdir()
    shutil.copyfile(dll, stage / ASSEMBLY)
    shutil.copyfile(ROOT / "assets/info.json", stage / "info.json")
    copy_tree(ROOT / "assets/Patches", stage / "Patches")
    manifest = {"source_index_sha256": result["source_index_sha256"], "module_id": module_id(dll),
                "plugin_files": inventory(stage)}
    write_json(stage / "build-manifest.json", manifest)
    backup = None
    if MOD.exists():
        check((MOD / "build-manifest.json").is_file(), "The install slot contains another mod.")
        check(PLUGINS.resolve().is_relative_to(GAME.resolve()) and MOD.resolve().parent == PLUGINS.resolve(),
              "Invalid install slot.")
        backup = RUNTIME / ("replaced-plugin-" + uuid.uuid4().hex[:8])
        MOD.replace(backup)
    try:
        stage.replace(MOD)
    except OSError:
        if backup is not None:
            backup.replace(MOD)
        raise
    print("Diplomacy installed in the dedicated game.", flush=True)


def verify_install() -> dict:
    regular_path(MOD)
    prepared = read_json(RUNTIME / "prepared.json")
    for relative, digest in prepared["game_files"].items():
        check(sha(GAME / relative) == digest, f"Game fingerprint changed: {relative}")
    check({p.name for p in PLUGINS.iterdir()} == {*prepared["providers"], MOD.name},
          "Unexpected plugin in the dedicated game.")
    for name, expected in prepared["providers"].items():
        check(inventory(PLUGINS / name) == expected, f"Provider changed: {name}")
    manifest = read_json(MOD / "build-manifest.json")
    actual = inventory(MOD)
    actual.pop("build-manifest.json")
    check(actual == manifest["plugin_files"], "Installed mod files do not match the build.")
    check(manifest["source_index_sha256"] == sha(ROOT / "source-index.json"), "Source and installed build differ.")
    assets = {"info.json": sha(ROOT / "assets/info.json")}
    assets.update({"Patches/" + path: digest
                   for path, digest in inventory(ROOT / "assets/Patches").items()})
    check({name: digest for name, digest in actual.items() if name != ASSEMBLY} == assets,
          "Authored assets have changed. Install the current build.")
    return manifest


def game_environment(profile: Path, manifest: dict) -> dict:
    regular_path(profile)
    check(profile.resolve().is_relative_to(RUNTIME.resolve()), "Profile must belong to the dedicated game.")
    for name in ("Saves", "Roaming", "Local", "Temp"):
        regular_path(profile / name)
        (profile / name).mkdir(parents=True, exist_ok=True)
    env = {k: v for k, v in os.environ.items() if not k.startswith("DIPLOMACY_")}
    env.update(DIPLOMACY_TEST_GAME_ROOT=str(GAME), DIPLOMACY_TEST_PROFILE_ROOT=str(profile),
               DIPLOMACY_EXPECTED_MODULE_ID=manifest["module_id"],
               DIPLOMACY_CANONICAL_TRADE="1", DIPLOMACY_ENABLE_AUTONOMOUS_AID="1",
               DIPLOMACY_INTERACTIVE_AUTO_BASELINE_SAVE="1",
               APPDATA=str(profile / "Roaming"), LOCALAPPDATA=str(profile / "Local"),
               USERPROFILE=str(profile), TEMP=str(profile / "Temp"), TMP=str(profile / "Temp"))
    return env


def play() -> int:
    no_running_game()
    manifest = verify_install()
    print("Diplomatie startet mit einem eigenen Spielprofil.", flush=True)
    return subprocess.run([str(GAME / EXE_NAME)], cwd=GAME,
                          env=game_environment(PROFILE, manifest)).returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    setup = commands.add_parser("prepare")
    for argument in ("template", "developer-api", "sdk", "framework-refs"):
        setup.add_argument("--" + argument, required=True, type=Path)
    for command in ("index", "build", "install", "check", "play"):
        commands.add_parser(command)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args)
    elif args.command == "index":
        index_sources()
    elif args.command == "build":
        build()
    elif args.command == "install":
        install()
    elif args.command == "check":
        result = verify_install()
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        return play()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
