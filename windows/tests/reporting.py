"""Run against the checked-out dictionaries. Audio and NVDA are stubbed."""
import importlib.util
from pathlib import Path
from tempfile import TemporaryDirectory
import shutil
import sys
import types

ROOT = Path(__file__).resolve().parents[1]
for module in ("api", "config", "inputCore", "keyboardHandler", "globalPluginHandler", "speech", "ui", "winsound", "wx"):
    sys.modules[module] = types.ModuleType(module)
for module in ("NVDAObjects", "NVDAObjects.behaviors", "NVDAObjects.inputComposition"):
    sys.modules[module] = types.ModuleType(module)
sys.modules["globalPluginHandler"].GlobalPlugin = object
sys.modules["keyboardHandler"].KeyboardInputGesture = object
sys.modules["speech"].extensions = None
sys.modules["speech"].SpeechMode = types.SimpleNamespace(talk="talk")
sys.modules["speech"].getState = lambda: types.SimpleNamespace(speechMode="talk")
sys.modules["speech"].Spri = types.SimpleNamespace(NEXT="next")


class CandidateItem:
    windowHandle = 42
    basicText = "分"

    def getFormattedCandidateName(self, index, word):
        return f"{index} {word}"


class InputComposition:
    windowHandle = 42
    compositionString = "分"
    isReading = False


sys.modules["NVDAObjects.behaviors"].CandidateItem = CandidateItem
sys.modules["NVDAObjects.inputComposition"].InputComposition = InputComposition

spec = importlib.util.spec_from_file_location("reporter", ROOT / "reporting/globalPlugins/dimsumCandidates.py")
reporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reporter)
pronunciations = reporter.load_pronunciations(Path(sys.argv[1]) / "NVDA dic")
source = Path(sys.argv[1]) / "NVDA dic"
split = reporter.DICTIONARY_DATA["split_symbols"](source)
punctuation, emoji = reporter.DICTIONARY_DATA["PUNCTUATION"], reporter.DICTIONARY_DATA["EMOJI"]
assert "、" in split[punctuation] and "、" not in split[emoji]
assert "😀" in split[emoji] and "😀" not in split[punctuation]
assert set(split[punctuation]).isdisjoint(split[emoji])
for enabled, includes, excludes in (
    ((reporter.CHARACTERS,), "過", "、😀"),
    ((punctuation,), "、", "過😀"),
    ((emoji,), "😀", "過、"),
):
    selected = reporter.load_pronunciations(source, enabled)
    assert includes in selected and all(character not in selected for character in excludes)
assert not reporter.load_pronunciations(source, ())

character_processing = types.ModuleType("characterProcessing")
invalidations = []
character_processing.clearSpeechSymbols = lambda: invalidations.append("speech")
sys.modules["characterProcessing"] = character_processing
cfg = {
    "speech": {"symbolDictionaries": ["cldr", "personal"]},
    reporter.CONFIG_SECTION: {"useCharacterDescriptions": True},
}
sys.modules["config"].conf = cfg
reporter.initialize_symbol_defaults()
assert set(cfg["speech"]["symbolDictionaries"]) == {"cldr", "personal", punctuation, emoji}
assert reporter.enabled_pronunciations() == (reporter.CHARACTERS, emoji, punctuation)
cfg["speech"]["symbolDictionaries"].remove(punctuation)
reporter.initialize_symbol_defaults()
assert reporter.enabled_pronunciations() == (reporter.CHARACTERS, emoji)
cfg[reporter.CONFIG_SECTION]["useCharacterDescriptions"] = False
assert reporter.enabled_pronunciations() == (emoji,)

with TemporaryDirectory() as temporary:
    profile = Path(temporary)
    state = types.ModuleType("NVDAState")
    state.WritePaths = types.SimpleNamespace(configDir=str(profile))
    sys.modules["NVDAState"] = state
    locale_module = types.ModuleType("languageHandler")
    locale_module.getLanguage = lambda: "zh_TW"
    sys.modules["languageHandler"] = locale_module
    logging = types.ModuleType("logHandler")
    logging.log = types.SimpleNamespace(warning=lambda *args, **kwargs: None)
    sys.modules["logHandler"] = logging
    character_processing.listAvailableSymbolDictionaryDefinitions = lambda: [
        types.SimpleNamespace(name=name, symbols=types.SimpleNamespace(
            invalidateAllData=lambda name=name: invalidations.append(name)
        ))
        for name in ("user", emoji, punctuation, "cldr")
    ]
    reporter.ASSETS = profile / "addons" / "dimsumCandidates"
    copied = profile / "DimsumPronunciations" / "zh_TW"
    revision = "a" * 32
    (copied / revision).mkdir(parents=True)
    (copied / "active.txt").write_text(revision)
    for filename in reporter.FILES:
        shutil.copyfile(source / filename, copied / revision / filename)
    symbols_file = profile / "symbols-zh_TW.dic"
    personal = b"symbols:\r\n# personal entry\r\nX\tmy choice\r\n"
    merged = personal + b"# Dimsum Group: managed symbols begin\r\nY\told Dimsum\r\n# Dimsum Group: managed symbols end\r\n"
    symbols_file.write_bytes(merged)
    (profile / "nvda.ini").write_bytes(b"[general]\r\nlanguage = zh_TW\r\n")
    reporter.update_native_dictionaries()
    assert (reporter.ASSETS / "locale" / "zh_TW" / f"symbols-{emoji}.dic").is_file()
    assert (profile / "nvda.ini").read_bytes() == b"[general]\r\nlanguage = zh_TW\r\n"
    assert symbols_file.read_bytes() == personal
    backups = copied.parent / "symbol-migration-backups"
    assert [path.read_bytes() for path in backups.iterdir()] == [merged]
    assert "user" in invalidations and "cldr" not in invalidations
    reporter.update_native_dictionaries()
    assert len(list(backups.iterdir())) == 1

reporter.ASSETS = ROOT / "reporting"
assert reporter.describe("日本", pronunciations) == "日子，本來"
assert pronunciations["過"][:3] == ("過去，戈先生，戈小姐", "過火", "過錯")
assert reporter.describe("過", pronunciations, all_groups=True).startswith("過去，戈先生，戈小姐，過火，過錯")
report = "\0".join(("dimsum-win-1", "test", "1", "dimsum_cb", "tone", "1", "0", "a", "不過")).encode()
assert reporter.parse_report(report) == (("test", 1), "dimsum_cb", "tone", 1, False, [("a", "不過")])
assert reporter.parse_report(report.rsplit(b"\x00", 1)[0]) is None

sys.modules["api"].getFocusObject = lambda: types.SimpleNamespace(
    windowHandle=42, getFormattedCandidateName=lambda index, word: f"{index} {word}"
)
settings = {"autoReportAllCandidates": True, "announceSelectedCandidate": True}
sys.modules["config"].conf = {"inputComposition": settings}
plugin = reporter.GlobalPlugin.__new__(reporter.GlobalPlugin)
plugin.preferences = {"ReportingMode": "tone", "PronounceCandidates": False}
spoken = []
plugin.say = spoken.append
items = [("1", "分"), ("2", "份")]
plugin.recent_candidate_speech = [(999, 42, ("1 分",))]
plugin.supplement(items, 1, 42, 1000)
assert spoken == ["1 分，2 份"]
spoken.clear()

plugin.recent_candidate_speech = [(1001, 42, ("1 分, 2 份,",))]
plugin.supplement(items, 1, 42, 1000)
assert not spoken, "If NVDA already read the new page, skip the supplement."

settings["autoReportAllCandidates"] = False
plugin.recent_candidate_speech = [(1001, 42, ("1 分, 分開",))]
plugin.supplement(items, 1, 42, 1000)
assert not spoken, "Native speech may include candidate descriptions."
plugin.recent_candidate_speech = [(1001, 84, ("1 分",))]
plugin.supplement(items, 1, 42, 1000)
assert spoken == ["分"]

native_page = ["1 分, 2 芬, "]
ordinary_page = (("dimsum", 1), "dimsum_cb", "page", 1, False, [("1", "分"), ("2", "芬")])
later_same = (("dimsum", 2), *ordinary_page[1:2], "same", *ordinary_page[3:])


def direct_plugin(mode, report=ordinary_page):
    plugin = reporter.GlobalPlugin.__new__(reporter.GlobalPlugin)
    plugin.preferences = {"ReportingMode": mode, "PronounceCandidates": False}
    plugin.own_speech = False
    plugin.speaking_directly = False
    plugin.direct_delivery = None
    plugin.last_report = None
    plugin.delayed = None
    plugin.refresh_settings = lambda: None
    plugin.read_report = lambda: (reporter.time.time_ns(), report)
    return plugin


spoken.clear()
sys.modules["ui"].message = lambda text, speechPriority=None: spoken.append((text, speechPriority))
plugin = direct_plugin("standard")
assert plugin.filter_native_speech(native_page) == ["1 分，2 芬"]
sys.modules["api"].getFocusObject = CandidateItem
assert plugin.filter_native_speech(["1 分, 分開"]) == [], "The native highlighted candidate must not repeat the page."
assert plugin.filter_native_speech(["Word 文件內容"]) == ["Word 文件內容"]
plugin.poll()
assert spoken == [], "A page already delivered through the speech filter must not be repeated by the timer."

sys.modules["api"].getFocusObject = InputComposition
plugin = direct_plugin("compact")
assert plugin.filter_native_speech(["分"]) == ["分，芬"]

composition = InputComposition()
composition.compositionString = "f$  [ *1. 分  2. 芬  ]"
sys.modules["api"].getFocusObject = lambda: composition
plugin = direct_plugin("compact")
assert plugin.filter_native_speech(["2. 芬"]) == ["分，芬"]
assert plugin.filter_native_speech(["記事本"]) == ["記事本"]

plugin = direct_plugin("compact", later_same)
plugin.poll()
assert spoken == [("分，芬", "next")], "An unread page must still be spoken after a later identical update."
plugin.poll()
assert len(spoken) == 1

plugin = direct_plugin("standard")
plugin.read_report = lambda: (reporter.time.time_ns() - reporter.NATIVE_SPEECH_WINDOW_NS - 1, ordinary_page)
assert plugin.filter_native_speech(native_page) == native_page
for mode in ("off", "tone"):
    assert direct_plugin(mode).filter_native_speech(native_page) == native_page

print("NVDA report parsing, pronunciations, mode routing, and tone supplementation passed")
