"""Run against the checked-out dictionaries. Audio and NVDA are stubbed."""
import importlib.util
from pathlib import Path
from tempfile import TemporaryDirectory
import sys
import types

ROOT = Path(__file__).resolve().parents[1]
for module in ("api", "config", "inputCore", "keyboardHandler", "globalPluginHandler", "speech", "ui", "winsound", "wx", "braille", "globalCommands", "scriptHandler", "textInfos"):
    sys.modules[module] = types.ModuleType(module)
for module in ("NVDAObjects", "NVDAObjects.behaviors", "NVDAObjects.inputComposition"):
    sys.modules[module] = types.ModuleType(module)
sys.modules["globalPluginHandler"].GlobalPlugin = object
sys.modules["keyboardHandler"].KeyboardInputGesture = types.SimpleNamespace(
    fromName=lambda key: types.SimpleNamespace(identifiers=[f"kb:{key}"])
)
sys.modules["inputCore"].normalizeGestureIdentifier = lambda key: key
sys.modules["scriptHandler"].script = lambda **kwargs: lambda method: method
sys.modules["textInfos"].UNIT_CHARACTER = "character"
presses = 0
sys.modules["scriptHandler"].getLastScriptRepeatCount = lambda: presses
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
assert "、" in pronunciations and "😀" in pronunciations
character_processing = types.ModuleType("characterProcessing")
invalidations = []
character_processing.clearSpeechSymbols = lambda: invalidations.append("speech")
sys.modules["characterProcessing"] = character_processing

with TemporaryDirectory() as temporary:
    profile = Path(temporary)
    state = types.ModuleType("NVDAState")
    state.WritePaths = types.SimpleNamespace(configDir=str(profile))
    sys.modules["NVDAState"] = state
    logging = types.ModuleType("logHandler")
    logging.log = types.SimpleNamespace(warning=lambda *args, **kwargs: None)
    sys.modules["logHandler"] = logging
    character_processing.listAvailableSymbolDictionaryDefinitions = lambda: [
        types.SimpleNamespace(name=name, symbols=types.SimpleNamespace(
            invalidateAllData=lambda name=name: invalidations.append(name)
        ))
        for name in ("user", "cldr")
    ]
    symbols_file = profile / "symbols-zh_TW.dic"
    personal = b"symbols:\r\n# personal entry\r\nX\tmy choice\r\n"
    merged = personal + b"# Dimsum Group: managed symbols begin\r\nY\told Dimsum\r\n# Dimsum Group: managed symbols end\r\n"
    symbols_file.write_bytes(merged)
    (profile / "nvda.ini").write_bytes(b"[general]\r\nlanguage = zh_TW\r\n")
    reporter.remove_legacy_native_symbols()
    assert (profile / "nvda.ini").read_bytes() == b"[general]\r\nlanguage = zh_TW\r\n"
    assert symbols_file.read_bytes() == personal
    backups = profile / "DimsumPronunciations" / "symbol-migration-backups"
    assert [path.read_bytes() for path in backups.iterdir()] == [merged]
    assert "user" in invalidations and "cldr" not in invalidations
    reporter.remove_legacy_native_symbols()
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

browse = (("dimsum", 3), "dimsum_cb", "browse", 2, False, [("1", "過"), ("2", "日本")])
for mode in ("standard", "compact", "tone"):
    plugin = direct_plugin(mode, browse)
    plugin.preferences["PronounceCandidates"] = True
    plugin.pronunciations = pronunciations
    plugin.refresh_pronunciations = lambda: None
    composition.compositionString = "raw  [ 1. 過  *2. 日本  ]"
    spoken.clear()
    full = reporter.describe("日本", pronunciations, all_groups=True)
    assert full != "日子，本來"
    assert plugin.filter_native_speech(["2. 日本"]) == [full]
    plugin.poll()
    assert not spoken, "Native speech replacement should not repeat through the timer."
    plugin = direct_plugin(mode, browse)
    plugin.preferences["PronounceCandidates"] = True
    plugin.pronunciations = pronunciations
    plugin.refresh_pronunciations = lambda: None
    plugin.poll()
    assert spoken == [(full, "next")], "Navigation must speak even without a native speech event."

plugin = direct_plugin("off")
assert plugin.filter_native_speech(native_page) == native_page
spoken.clear()
plugin.poll()
assert not spoken

review = types.SimpleNamespace(text="過", expand=lambda unit: None)
sys.modules["api"].getReviewPosition = lambda: types.SimpleNamespace(copy=lambda: review)
sys.modules["braille"].handler = types.SimpleNamespace(handleReviewMove=lambda **kwargs: None)
sys.modules["globalCommands"].commands = types.SimpleNamespace(
    script_review_currentCharacter=lambda gesture: (
        spoken.append(presses), plugin.capture(["原有解釋"] if presses else ["過"])
    )
)
sys.modules["ui"].reviewMessage = spoken.append
plugin.pronunciations = pronunciations
plugin.refresh_pronunciations = lambda: None
plugin.recent_candidate_speech = []
plugin.say = lambda text, **kwargs: spoken.append(text)
full = reporter.describe("過", pronunciations, all_groups=True)
for presses in range(3):
    plugin.script_reviewCharacter(None)
assert spoken == [0, full, 2]
assert plugin.last_speech == "過"
presses = 1
review.text = "𰻞"
plugin.script_reviewCharacter(None)
assert spoken[-1] == 1, "Use the native description when Dimsum has no entry."
review.text = "過"
plugin.preferences["UseDimsumReviewDescription"] = False
plugin.script_reviewCharacter(None)
assert spoken[-1] == 1
assert plugin.last_speech == "過", "Keep the original character across a native double press."
plugin.script_lookup(None)
assert spoken[-1] == full, "Custom lookup is independent of both switches."
plugin.last_speech = "日本"
plugin.script_lookup(None)
assert spoken[-1] == "日子，本來"
plugin.last_speech = "𰻞"
plugin.script_lookup(None)
assert spoken[-1] == "呢段文字喺點心配詞表入面搵唔到配詞。"

with TemporaryDirectory() as temporary:
    reporter.BASE = Path(temporary)
    config_path = reporter.BASE / "settings.json"
    plugin.settings_stamp = None
    bindings = {}
    plugin.clearGestureBindings = bindings.clear
    plugin.bindGestures = bindings.update
    plugin.bindGesture = bindings.__setitem__
    config_path.write_text('{"ReportingMode":"off", "UseDimsumReviewDescription":false, "LookupGesture":"Control+Alt+D"}')
    reporter.GlobalPlugin.refresh_settings(plugin)
    assert bindings == {
        "kb:numpad2": "reviewCharacter", "kb(laptop):NVDA+.": "reviewCharacter", "kb:control+alt+d": "lookup"
    }
    config_path.write_text('{"UseDimsumReviewDescription":false}')
    plugin.settings_stamp = None
    reporter.GlobalPlugin.refresh_settings(plugin)
    assert bindings == {}, "With both features off, the native shortcuts are left entirely to NVDA."
    config_path.unlink()
    reporter.GlobalPlugin.refresh_settings(plugin)
    assert bindings == {"kb:numpad2": "reviewCharacter", "kb(laptop):NVDA+.": "reviewCharacter"}

print("NVDA reporting, pronunciation migration, review gestures and independent lookup passed")
