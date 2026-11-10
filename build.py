#!/usr/bin/env python3
"""Deterministic, offline C# build entry point; never installs or starts the game.

The source index fixes the selected inputs and their compile order.
All dependency locations are explicit configuration. Build outputs contain
no developer-specific paths or packaged game/reference assemblies.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

PROJECT_DIR = Path(__file__).resolve().parent
HEX64 = re.compile(r"^[a-f0-9]{64}$")
IDENTITY = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")
VERSION = re.compile(r"^[0-9]+(?:\.[0-9]+){2,3}(?:[-+][A-Za-z0-9._-]+)?$")
EXPECTED_REFERENCES = (
    "BepInExReference", "HarmonyReference", "SHCDESEReference",
    "SystemMemoryReference", "R3Reference", "AssemblyCSharpReference",
    "NoesisReference", "UnityEngineReference", "UnityEngineCoreModuleReference",
    "UnityEngineAssetBundleModuleReference", "UnityEngineAudioModuleReference",
    "UnityEngineImageConversionModuleReference", "SteamworksReference",
    "APISharedReference", "NetStandardReference",
)


class BuildContractError(ValueError):
    pass


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as reader:
        for block in iter(lambda: reader.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def resolve_path(value: str | None, relative_to: Path) -> Path | None:
    if not value:
        return None
    p = Path(value).expanduser()
    return (p if p.is_absolute() else relative_to / p).resolve()


def under(child: Path, parent: Path) -> bool:
    return child == parent or parent in child.parents


def relative_fragment(raw: str, *, expected_suffix: str = ".cs") -> Path:
    if not isinstance(raw, str) or not raw or "\\" in raw:
        raise BuildContractError("Index paths must use portable forward slashes.")
    path = PurePosixPath(raw)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise BuildContractError("Index paths must be safe, relative components.")
    if ":" in raw or raw.startswith(("//", "~")):
        raise BuildContractError("Absolute or drive-based source paths are prohibited.")
    if not raw.lower().endswith(expected_suffix):
        raise BuildContractError("The selected input is not a C# source.")
    return Path(*path.parts)


def verify_source_index(source_root: Path, index_path: Path, profile: str):
    if not source_root.is_dir() or not index_path.is_file():
        raise BuildContractError("Source directory or source index unavailable.")
    data = json.loads(index_path.read_text(encoding="utf-8-sig"))
    items = data.get("source_files")
    if not isinstance(items, list) or not items:
        raise BuildContractError("Source index must list an explicit nonempty set.")
    keys = set()
    destinations = set()
    verified = []
    for record in items:
        if not isinstance(record, dict):
            raise BuildContractError("Invalid source index entry.")
        src = relative_fragment(record.get("path"))
        dest = relative_fragment(record.get("stage_path"))
        if not dest.parts or dest.parts[0] != "src":
            raise BuildContractError("Staged sources must be under src/.")
        expected = record.get("sha256")
        if not isinstance(expected, str) or not HEX64.fullmatch(expected):
            raise BuildContractError("Invalid mandatory SHA-256 source pin.")
        key, dest_key = str(src).casefold(), str(dest).casefold()
        if key in keys or dest_key in destinations:
            raise BuildContractError("Duplicate/colliding source or destination.")
        keys.add(key)
        destinations.add(dest_key)
        source = (source_root / src).resolve()
        if not under(source, source_root) or not source.is_file() or source.is_symlink():
            raise BuildContractError("Source escapes the selected source root.")
        if digest(source) != expected:
            raise BuildContractError("Source digest differs from the frozen source index.")
        verified.append((source, dest, expected))
    defines = data.get("profiles", {}).get(profile)
    if not isinstance(defines, list) or not defines or not all(
        isinstance(value, str) and IDENTITY.fullmatch(value) for value in defines
    ) or len(defines) != len(set(defines)):
        raise BuildContractError("Missing or malformed explicit feature defines.")
    return verified, defines, digest(index_path)


def stage_inputs(verified, output: Path, identity_hash: str) -> Path:
    stage = output / ("sources-" + identity_hash[:20])
    if stage.exists():
        actual = sorted(str(p.relative_to(stage)).replace("\\", "/")
                        for p in stage.rglob("*.cs"))
        expected = sorted(str(relative).replace("\\", "/") for _, relative, _ in verified)
        if actual != expected:
            raise BuildContractError("Existing staged input tree differs from selection.")
        for _, name, expected_sha in verified:
            if digest(stage / name) != expected_sha:
                raise BuildContractError("A staged source was changed.")
        return stage
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="selected-", dir=str(output)) as temp:
        directory = Path(temp)
        for source, relative, expected_sha in verified:
            destination = directory / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination, follow_symlinks=False)
            if digest(destination) != expected_sha:
                raise BuildContractError("Staged copy verification failed.")
        directory.replace(stage)
    return stage


def require_file(path: Path | None, role: str) -> Path:
    if path is None or not path.is_file():
        raise BuildContractError("An explicit read-only dependency is missing: " + role)
    return path


def resolved_dependencies(args, defaults: Path):
    game = resolve_path(args.game_dir or os.getenv("EUROPE_GAME_DIR"),
                        PROJECT_DIR) or defaults / "game"
    loader = resolve_path(args.loader_dir or os.getenv("EUROPE_LOADER_DIR"),
                          PROJECT_DIR) or defaults / "loader"
    extender = resolve_path(args.extender_dir or os.getenv("EUROPE_EXTENDER_DIR"),
                            PROJECT_DIR) or defaults / "extender"
    api = resolve_path(args.api_shared or os.getenv("EUROPE_API_SHARED"),
                       PROJECT_DIR) or defaults / "api" / "APIShared.dll"
    framework = resolve_path(args.framework_refs or os.getenv("EUROPE_FRAMEWORK_REFS"),
                             PROJECT_DIR) or defaults / "net481"
    if api.is_dir():
        api = api / "APIShared.dll"
    loader_core = next(
        (p for p in (loader / "BepInEx" / "core", loader / "core", loader)
         if (p / "BepInEx.dll").is_file()),
        loader / "BepInEx" / "core")
    managed = game / "Stronghold Crusader Definitive Edition_Data" / "Managed"
    dependencies = {
        "BepInExReference": loader_core / "BepInEx.dll",
        "HarmonyReference": loader_core / "0Harmony.dll",
        "SHCDESEReference": extender / "SHCDESE.dll",
        "SystemMemoryReference": extender / "System.Memory.dll",
        "R3Reference": extender / "R3.dll",
        "AssemblyCSharpReference": managed / "Assembly-CSharp.dll",
        "NoesisReference": managed / "Noesis.NoesisGUI.dll",
        "UnityEngineReference": managed / "UnityEngine.dll",
        "UnityEngineCoreModuleReference": managed / "UnityEngine.CoreModule.dll",
        "UnityEngineAssetBundleModuleReference": managed / "UnityEngine.AssetBundleModule.dll",
        "UnityEngineAudioModuleReference": managed / "UnityEngine.AudioModule.dll",
        "UnityEngineImageConversionModuleReference": managed / "UnityEngine.ImageConversionModule.dll",
        "SteamworksReference": managed / "com.rlabrecque.steamworks.net.dll",
        "APISharedReference": api,
        "NetStandardReference": extender / "netstandard.dll",
    }
    if set(dependencies) != set(EXPECTED_REFERENCES):
        raise BuildContractError("Internal reference list does not match project contract.")
    for name, value in dependencies.items():
        dependencies[name] = require_file(value.resolve(), name)
    require_file((framework / "mscorlib.dll").resolve(), "offline framework mscorlib")
    return dependencies, framework.resolve(), (game, loader, extender, api.parent, framework)


def verify_dependency_lock(lock_path: Path | None, dependencies: dict):
    require_file(lock_path, "external dependency checksum lock")
    data = json.loads(lock_path.read_text(encoding="utf-8-sig"))
    locked = data.get("sha256")
    if not isinstance(locked, dict) or set(locked) != set(dependencies):
        raise BuildContractError("Dependency lock must include every reference exactly once.")
    for role, path in dependencies.items():
        expected = locked[role]
        if not isinstance(expected, str) or not HEX64.fullmatch(expected):
            raise BuildContractError("Malformed external dependency pin.")
        if digest(path) != expected:
            raise BuildContractError("An external dependency changed: " + role)


def check_identity(args):
    values = {
        "PluginAssemblyName": args.assembly_name or os.getenv("EUROPE_ASSEMBLY_NAME"),
        "PluginRootNamespace": args.root_namespace or os.getenv("EUROPE_ROOT_NAMESPACE"),
        "ProductVersion": args.version or os.getenv("EUROPE_PRODUCT_VERSION"),
        "ProductAssemblyVersion": args.assembly_version or os.getenv("EUROPE_ASSEMBLY_VERSION"),
        "ProductFileVersion": args.file_version or os.getenv("EUROPE_FILE_VERSION"),
    }
    if not all(values.values()):
        raise BuildContractError("Explicit reviewed assembly identity and product versions required.")
    if not IDENTITY.fullmatch(values["PluginAssemblyName"]) or not IDENTITY.fullmatch(values["PluginRootNamespace"]):
        raise BuildContractError("Invalid reviewed external identity.")
    if not all(VERSION.fullmatch(values[key]) for key in
               ("ProductVersion", "ProductAssemblyVersion", "ProductFileVersion")):
        raise BuildContractError("Invalid product or assembly version.")
    return values


def public_build_manifest(diagnostic: bool):
    manifest_path = PROJECT_DIR / "assets/release/info.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    matches = [dependency for dependency in manifest["Dependencies"]
               if dependency.get("GUID") == "APIShared_Serp"]
    if len(matches) != 1:
        raise BuildContractError("The public manifest must declare one APIShared dependency.")
    dependency = matches[0]
    minimum = dependency.get("MinimumVersion")
    if not isinstance(minimum, str) or not re.fullmatch(r"[0-9]+(?:\.[0-9]+){2,3}", minimum):
        raise BuildContractError("The public APIShared minimum must be a numeric version.")
    if diagnostic and tuple(map(int, minimum.split("."))) < (0, 42, 0):
        # The managed-save diagnostic uses the API introduced in 0.42.0.
        minimum = "0.42.0"
        dependency["MinimumVersion"] = minimum
    return manifest, minimum


def build(args, selected, defines, index_sha, source_root):
    options = check_identity(args)
    defaults = PROJECT_DIR / "dependencies"
    deps, framework, dep_roots = resolved_dependencies(args, defaults)
    lock = resolve_path(args.dependency_lock or os.getenv("EUROPE_DEPENDENCY_LOCK"),
                        PROJECT_DIR)
    verify_dependency_lock(lock, deps)
    sdk_value = args.sdk or os.getenv("EUROPE_DOTNET")
    sdk = resolve_path(sdk_value, PROJECT_DIR) if sdk_value else None
    if sdk is None:
        sdk = resolve_path(shutil.which("dotnet"), PROJECT_DIR)
    require_file(sdk, "offline dotnet SDK executable")
    output = resolve_path(args.output_dir or os.getenv("EUROPE_OUTPUT_DIR"),
                          PROJECT_DIR) or PROJECT_DIR / "out"
    forbidden = [source_root.resolve(), sdk.parent, *[root.resolve() for root in dep_roots]]
    if output == PROJECT_DIR or any(under(output, root) or under(root, output) for root in forbidden):
        raise BuildContractError("Output must not overlap source, game, loader, SDK, extender or project root.")
    diagnostic_source = resolve_path(args.devdiag_source, PROJECT_DIR)
    diagnostic_hash = None
    if diagnostic_source is not None:
        if args.profile != "release" or diagnostic_source.name != "DevDiag.cs":
            raise BuildContractError("The diagnostic build requires the public profile and Shared DevDiag.cs.")
        require_file(diagnostic_source, "Shared DevDiag source")
        if under(output, diagnostic_source.parent) or under(diagnostic_source.parent, output):
            raise BuildContractError("Diagnostic source and output must be disjoint.")
        diagnostic_hash = digest(diagnostic_source)
        defines = [*defines, "SERP_DEVDIAG"]
    profile_out = output / ("devdiag" if diagnostic_source else args.profile)
    source_pin = hashlib.sha256(
        (";".join(str(relative).replace("\\", "/") + ":" + sha
                  for _, relative, sha in selected) + ":" + index_sha).encode("utf-8")
    ).hexdigest()
    staged = stage_inputs(selected, profile_out, source_pin)
    binary = profile_out / "bin"
    obj = profile_out / "obj"
    binary.mkdir(parents=True, exist_ok=True)
    obj.mkdir(parents=True, exist_ok=True)
    selected_items = profile_out / "selected-compile-items.props"
    items_project = ET.Element("Project")
    item_group = ET.SubElement(items_project, "ItemGroup")
    for _, relative, _ in selected:
        logical = str(relative).replace("\\", "/")
        ET.SubElement(item_group, "Compile", {
            "Include": "$(CanonicalSourceTree)/" + logical.removeprefix("src/"),
            "Link": logical,
        })
    manifest = None
    if args.profile == "release":
        manifest, minimum = public_build_manifest(diagnostic_source is not None)
        generated = obj / "GeneratedPublicBuildDependencies.cs"
        generated.write_text(
            "namespace StrongholdEuropeDE.Runtime {\n"
            "    internal static class GeneratedPublicBuildDependencies {\n"
            f"        internal const string ApiSharedMinimum = \"{minimum}\";\n"
            "    }\n"
            "}\n", encoding="utf-8", newline="\r\n"
        )
        ET.SubElement(item_group, "Compile", {"Include": str(generated)})
    ET.ElementTree(items_project).write(selected_items, encoding="utf-8", xml_declaration=True)
    if diagnostic_source is not None:
        diagnostic_copy = profile_out / "DevDiag.cs"
        shutil.copyfile(diagnostic_source, diagnostic_copy)
        if digest(diagnostic_copy) != diagnostic_hash:
            raise BuildContractError("Shared diagnostic source changed while copying.")
    offline_config = profile_out / "NuGet.Config"
    offline_config.write_text("<configuration><packageSources><clear /></packageSources></configuration>\n", encoding="utf-8")
    properties = dict(options)
    properties.update(deps)
    properties.update({
        "CanonicalSourceTree": staged / "src",
        "SelectedCompileItemsFile": selected_items,
        "AutomaticallyUseReferenceAssemblyPackages": "false",
        "RestoreConfigFile": offline_config,
        "RestoreSources": "",
        "FrameworkPathOverride": framework,
        "TargetFrameworkRootPath": framework.parent.parent,
        "BaseIntermediateOutputPath": str(obj) + os.sep,
        "MSBuildProjectExtensionsPath": str(obj) + os.sep,
        "IntermediateOutputPath": str(obj / "Release") + os.sep,
        "OutputPath": str(binary) + os.sep,
        "BuildCompileDefines": "%3B".join(defines),
        "NuGetAudit": "false",
        "RestoreIgnoreFailedSources": "true",
        "ImportDirectoryBuildProps": "false",
        "ImportDirectoryBuildTargets": "false",
    })
    if diagnostic_source is not None:
        properties["DevDiagSource"] = diagnostic_copy
    command = [
        str(sdk), "build", "--no-restore", "--nologo",
        "--configuration", "Release", "--verbosity", "minimal",
        str(PROJECT_DIR / "StrongholdEuropeDiplomatie.csproj")
    ]
    command.extend("-p:" + key + "=" + str(value) for key, value in sorted(properties.items()))
    env = os.environ.copy()
    env.update({
        "DOTNET_ROOT": str(sdk.parent),
        "DOTNET_CLI_HOME": str(profile_out),
        "DOTNET_NOLOGO": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "MSBUILDDISABLENODEREUSE": "1",
    })
    # This generates SDK assets from the already supplied framework refs.
    # Package sources are empty: it cannot download or resolve a package.
    prepare = [str(sdk), "msbuild", str(PROJECT_DIR / "StrongholdEuropeDiplomatie.csproj"),
               "-nologo", "-verbosity:quiet", "-target:Restore"]
    prepare.extend("-p:" + key + "=" + str(value) for key, value in sorted(properties.items()))
    prepared = subprocess.run(prepare, cwd=str(PROJECT_DIR), env=env,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, errors="replace", check=False, timeout=120)
    if prepared.returncode:
        raise BuildContractError("Offline SDK asset generation failed; no package sources were enabled.")
    result = subprocess.run(command, cwd=str(PROJECT_DIR), env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, errors="replace", check=False)
    transcript = result.stdout
    for root in (source_root, PROJECT_DIR, output, staged, sdk.parent, *dep_roots):
        variants = (str(root), str(root).replace("\\", "/"))
        for value in variants:
            transcript = transcript.replace(value, "<local>")
    (profile_out / "build.log").write_text(transcript, encoding="utf-8")
    artifact = binary / (options["PluginAssemblyName"] + ".dll")
    after_pins = all(digest(source) == expected for source, _, expected in selected)
    if not after_pins:
        raise BuildContractError("Inputs changed while building; output is not acceptable.")
    if manifest is not None and result.returncode == 0:
        (binary / "info.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8", newline="\r\n"
        )
    receipt = {
        "schema": "portable-offline-build-receipt",
        "profile": "devdiag" if diagnostic_source else args.profile,
        "source_count": len(selected),
        "source_index_sha256": index_sha,
        "source_selection_sha256": source_pin,
        "source_unchanged_after_build": after_pins,
        "dependency_lock_sha256": digest(lock),
        "exit_code": result.returncode,
        "artifact_sha256": digest(artifact) if result.returncode == 0 and artifact.is_file() else None,
        "native_execution": False,
        "external_installation_modified": False,
        "native_normal_release": False,
        "native_mp_release": False,
    }
    if diagnostic_source is not None:
        receipt["diagnostic_source_sha256"] = diagnostic_hash
    (profile_out / "build-receipt.json").write_text(
        json.dumps(receipt, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(receipt, sort_keys=True))
    return result.returncode


def main(argv=None):
    p = argparse.ArgumentParser(description="Offline, source-pinned build; no installs or game launches")
    p.add_argument("--source-root", help="Selected read-only source tree")
    p.add_argument("--source-index", help="Pinned source selection manifest")
    p.add_argument("--game-dir")
    p.add_argument("--loader-dir")
    p.add_argument("--extender-dir")
    p.add_argument("--api-shared")
    p.add_argument("--sdk")
    p.add_argument("--framework-refs")
    p.add_argument("--dependency-lock", help="SHA-256 list for every referenced DLL")
    p.add_argument("--output-dir")
    p.add_argument("--assembly-name")
    p.add_argument("--root-namespace")
    p.add_argument("--version")
    p.add_argument("--assembly-version")
    p.add_argument("--file-version")
    p.add_argument("--profile", choices=("release", "qa"), default="release")
    p.add_argument("--devdiag-source", help=argparse.SUPPRESS)
    p.add_argument("--build", action="store_true", help="Compile offline (never executes game)")
    arguments = list(sys.argv[1:] if argv is None else argv)
    devdiag = bool(arguments and arguments[0].casefold() == "/devdiag")
    args = p.parse_args(arguments[1:] if devdiag else arguments)
    if bool(args.devdiag_source) != devdiag or (devdiag and not args.build):
        p.error("Use build.bat /devdiag --devdiag-source <Shared DevDiag.cs> to build diagnostics.")
    try:
        source_root = resolve_path(args.source_root or os.getenv("EUROPE_SOURCE_DIR"),
                                   PROJECT_DIR) or PROJECT_DIR / "src"
        source_index = resolve_path(args.source_index or os.getenv("EUROPE_SOURCE_INDEX"),
                                    PROJECT_DIR) or PROJECT_DIR / "source-index.json"
        selected, defines, index_sha = verify_source_index(source_root, source_index, args.profile)
        if not args.build:
            print(json.dumps({
                "status": "PINNED_SOURCE_VALIDATION_OK",
                "source_count": len(selected),
                "source_index_sha256": index_sha,
                "profile": args.profile,
                "compiled": False,
                "game_launched": False,
            }, sort_keys=True))
            return 0
        return build(args, selected, defines, index_sha, source_root)
    except (BuildContractError, ValueError, OSError, json.JSONDecodeError) as exc:
        print("BUILD_REFUSED: " + type(exc).__name__ + ": " + str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
