"""Copy the release's Rime data and assemble the NVDA add-on."""
import argparse
import hashlib
from pathlib import Path
import os
import re
import runpy
from shutil import copy2
import struct
import subprocess
from tempfile import TemporaryDirectory
from zipfile import ZIP_DEFLATED, ZipFile

HERE = Path(__file__).resolve().parent
SCHEMAS = ("dimsum_cb", "dimsum_cb_std", "dimsum_full")
PRONUNCIATIONS = ("characterDescriptions.dic", "symbols.dic", "cldr.dic")
ENGINE_SHA256 = "cf509534a8f5f8af9c98ed7cbb8f135439f145a8cbe7e50ede42bb5b5ab45c29"
RUNTIME_SETTINGS = ("default.yaml", "weasel.yaml", "punctuation.yaml", "key_bindings.yaml", "symbols.yaml")


def pe_machine(path):
    with path.open("rb") as executable:
        executable.seek(60)
        pe_offset = struct.unpack("<I", executable.read(4))[0]
        executable.seek(pe_offset)
        magic, machine = struct.unpack("<4sH", executable.read(6))
        if magic != b"PE\0\0":
            raise ValueError(f"Invalid PE file: {path.name}")
        return machine


def build_engine(official, output, seven_zip, nsis):
    with TemporaryDirectory() as directory:
        work = Path(directory)
        vendor = work / "vendor"
        stage = work / "engine"
        (stage / "data").mkdir(parents=True)
        subprocess.run([seven_zip, "x", "-aou", "-y", f"-o{vendor}", str(official.resolve())],
                       check=True, stdout=subprocess.DEVNULL)
        binaries = {
            "WeaselServer.exe": 0x8664,
            "WeaselDeployer.exe": 0x8664,
            "rime.dll": 0x8664,
            "WinSparkle.dll": 0x8664,
            "WeaselSetup.exe": 0x014C,
        }
        for name, machine in binaries.items():
            target = Path(name)
            candidates = [vendor / name, *vendor.glob(f"{target.stem}_[0-9]*{target.suffix}")]
            matches = [path for path in candidates if path.is_file() and pe_machine(path) == machine]
            if len(matches) != 1:
                raise ValueError(f"Could not identify the upstream binary {name} ({machine:04X})")
            copy2(matches[0], stage / name)
        for name in ("LICENSE.txt", "weasel.dll", "weaselx64.dll", "weaselARM.dll",
                     "weaselARM64.dll", "weaselARM64X.dll"):
            copy2(vendor / name, stage / name)
        for name in RUNTIME_SETTINGS:
            copy2(vendor / "data" / name, stage / "data" / name)
        default = stage / "data" / "default.yaml"
        text, count = re.subn(rb"(?m)^schema_list:\r?\n(?:[ \t]+-[^\r\n]*\r?\n)+", b"schema_list: []\n", default.read_bytes())
        if count != 1:
            raise ValueError("Could not replace the engine's default schema list")
        default.write_bytes(text)
        copy2(HERE / "installer" / "engine.nsi", stage / "engine.nsi")
        verbosity = "/V2" if os.name == "nt" else "-V2"
        subprocess.run([nsis, verbosity, "engine.nsi"], cwd=stage, check=True, stdout=subprocess.DEVNULL)
        copy2(stage / "weasel-installer.exe", output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="Checkout of dimsum-braille-ime")
    parser.add_argument("destination", type=Path, help="Manager assets directory")
    parser.add_argument("--engine", type=Path, required=True, help="Official engine installer")
    parser.add_argument("--seven-zip", default="7z")
    parser.add_argument("--nsis", default="makensis")
    arguments = parser.parse_args()
    source = arguments.source
    assets = arguments.destination
    rime = assets / "rime"
    (rime / "lua").mkdir(parents=True, exist_ok=True)
    if hashlib.sha256(arguments.engine.read_bytes()).hexdigest() != ENGINE_SHA256:
        parser.error("Unexpected engine installer SHA-256")
    engine = assets / "weasel-installer.exe"
    build_engine(arguments.engine, engine, arguments.seven_zip, arguments.nsis)
    copy2(HERE / "audio" / "only.wav", assets / "only.wav")
    for name in ("engine-notices.txt", "engine-license.txt"):
        copy2(HERE / name, assets / name)
    (assets / "pronunciations").mkdir(exist_ok=True)
    for filename in PRONUNCIATIONS:
        copy2(source / "NVDA dic" / filename, assets / "pronunciations" / filename)
    for filename in (
        *(f"{schema}.schema.yaml" for schema in SCHEMAS),
        "dimsum_cb.dict.yaml", "default.custom.yaml", "weasel.custom.yaml",
    ):
        copy2(source / "Dimsum dic and schema" / filename, rime / filename)
    (rime / "dimsum_cb.user.dict.yaml").write_text(
        '---\nname: dimsum_cb.user\nversion: "1.0"\nsort: by_weight\n...\n',
        encoding="utf-8",
    )
    reporting = HERE / "reporting"
    dictionary_data = runpy.run_path(str(reporting / "dictionaryData.py"))
    native_dictionaries = dictionary_data["native_symbol_files"](source / "NVDA dic")
    copy2(reporting / "rime/lua/dimsum_windows_reporter.lua", rime / "lua")
    addon = assets / "dimsumCandidates.nvda-addon"
    with ZipFile(addon, "w", compression=ZIP_DEFLATED) as archive:
        archive.write(reporting / "manifest.ini", "manifest.ini")
        archive.write(reporting / "installTasks.py", "installTasks.py")
        archive.write(reporting / "dictionaryData.py", "dictionaryData.py")
        for filename in ("emoji-data.txt", "LICENSE.txt"):
            archive.write(reporting / "unicode" / filename, "unicode/" + filename)
        archive.write(reporting / "globalPlugins/dimsumCandidates.py", "globalPlugins/dimsumCandidates.py")
        archive.write(HERE / "LICENSE", "LICENSE")
        for filename in PRONUNCIATIONS:
            archive.write(source / "NVDA dic" / filename, filename)
        for locale in dictionary_data["LOCALES"]:
            for name, contents in native_dictionaries.items():
                archive.writestr(f"locale/{locale}/symbols-{name}.dic", contents.encode("utf-8"))
    print(hashlib.sha256(engine.read_bytes()).hexdigest())


if __name__ == "__main__":
    main()
