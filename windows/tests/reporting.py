"""Run against the checked-out dictionaries. Audio and NVDA are stubbed."""
import importlib.util
from pathlib import Path
from tempfile import TemporaryDirectory
import sys
import types

ROOT = Path(__file__).resolve().parents[1]
for module in ("api", "config", "speech.commands", "inputCore", "keyboardHandler", "globalPluginHandler", "speech", "ui", "winsound", "wx", "braille", "globalCommands", "scriptHandler", "textInfos"):
    sys.modules[module] = types.ModuleType(module)
for module in ("NVDAObjects", "NVDAObjects.behaviors", "NVDAObjects.inputComposition"):
    sys.modules[module] = types.ModuleType(module)
sys.modules["globalPluginHandler"].GlobalPlugin = object
class KeyboardInputGesture:
    def __init__(self, key, modifiers=()):
        self.mainKeyName = key
        self.modifierNames = modifiers
        self.sent = 0

    def send(self):
        self.sent += 1

    @staticmethod
    def fromName(key):
        return types.SimpleNamespace(identifiers=[f"kb:{key}"])

sys.modules["keyboardHandler"].KeyboardInputGesture = KeyboardInputGesture
sys.modules["inputCore"].manager = types.SimpleNamespace(isInputHelpActive=False)
sys.modules["wx"].CallAfter = lambda action: action()
sys.modules["inputCore"].normalizeGestureIdentifier = lambda key: key
sys.modules["scriptHandler"].script = lambda **kwargs: lambda method: method
sys.modules["textInfos"].UNIT_CHARACTER = "character"
presses = 0
sys.modules["scriptHandler"].getLastScriptRepeatCount = lambda: presses
sys.modules["speech"].extensions = None
sys.modules["config"].conf = {"featureFlag": {"cancelExpiredFocusSpeech": 2}}

class CancellableSpeechCommand:
    def is_current(self):
        return self._checkIfValid()

sys.modules["speech.commands"]._CancellableSpeechCommand = CancellableSpeechCommand
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

sys.modules["api"].getFocusObject = InputComposition
spoken = []

native_page = ["1 分, 2 芬, "]
ordinary_page = (("dimsum", 1), "dimsum_cb", "page", 1, False, [("1", "分"), ("2", "芬")])
later_same = (("dimsum", 2), *ordinary_page[1:2], "same", *ordinary_page[3:])


def report_time():
    return reporter.time.time_ns() - 2 * reporter.CANDIDATE_SETTLE_NS


def direct_plugin(omit_numbers=False, report=ordinary_page):
    plugin = reporter.GlobalPlugin.__new__(reporter.GlobalPlugin)
    plugin.preferences = {"OmitCandidateNumbers": omit_numbers, "PronounceCandidates": False}
    plugin.own_speech = False
    plugin.speaking_directly = False
    plugin.delivered_window = None
    plugin.latest_stamp = 0
    plugin.last_report = None
    plugin.refresh_settings = lambda: None
    plugin.read_report = lambda: (report_time(), report)
    return plugin


spoken.clear()
sys.modules["ui"].message = lambda text, speechPriority=None: spoken.append((text, speechPriority))
plugin = direct_plugin()
assert plugin.filter_native_speech(native_page) == []
assert plugin.last_report is None, "Filtering a native utterance must not acknowledge the Rime report."
sys.modules["api"].getFocusObject = CandidateItem
assert plugin.filter_native_speech(["1 分, 分開"]) == []
assert plugin.filter_native_speech(["Word 文件內容"]) == ["Word 文件內容"]
plugin.poll()
plugin.poll()
assert spoken == [("1 分，2 芬", "next")], "The timer speaks once even if the native utterance would be canceled."
assert plugin.filter_native_speech(["1 分, 分開"]) == []

sys.modules["api"].getFocusObject = InputComposition
plugin = direct_plugin(omit_numbers=True)
assert plugin.filter_native_speech(["分"]) == []

composition = InputComposition()
composition.compositionString = "f$  [ *1. 分  2. 芬  ]"
sys.modules["api"].getFocusObject = lambda: composition
plugin = direct_plugin(omit_numbers=True)
assert plugin.filter_native_speech(["2. 芬"]) == []
assert plugin.filter_native_speech(["記事本"]) == ["記事本"]

spoken.clear()
plugin = direct_plugin(True, later_same)
plugin.poll()
assert spoken == [("分，芬", "next")], "An unread page must still be spoken after a later identical update."
plugin.poll()
assert len(spoken) == 1

spoken.clear()
plugin = direct_plugin(omit_numbers=True)
plugin.poll()
plugin.read_report = lambda: (report_time(), later_same)
plugin.poll()
assert spoken == [("分，芬", "next")] * 2, "A fresh input event should speak even when it yields the same candidates."

plugin = direct_plugin()
plugin.read_report = lambda: (reporter.time.time_ns() - reporter.NATIVE_SPEECH_WINDOW_NS - 1, ordinary_page)
assert plugin.filter_native_speech(native_page) == native_page
for output in ("Off", "Jaws", "Narrator", "WindowsVoice"):
    plugin.preferences["CandidateSpeechOutput"] = output
    assert reporter.GlobalPlugin.read_report(plugin) is None, "The NVDA add-on must leave other output modes alone."
plugin.preferences.pop("CandidateSpeechOutput")

assert reporter.omit_candidate_numbers({"ReportingMode": "compact"})
assert not reporter.omit_candidate_numbers({"OmitCandidateNumbers": False, "ReportingMode": "compact"})
assert not reporter.omit_candidate_numbers({})

browse = (("dimsum", 3), "dimsum_cb", "browse", 2, False, [("1", "過"), ("2", "日本")])
for omit_numbers in (False, True):
    plugin = direct_plugin(omit_numbers, browse)
    plugin.preferences["PronounceCandidates"] = True
    plugin.pronunciations = pronunciations
    plugin.refresh_pronunciations = lambda: None
    composition.compositionString = "raw  [ 1. 過  *2. 日本  ]"
    spoken.clear()
    full = reporter.describe("日本", pronunciations, all_groups=True)
    assert full != "日子，本來"
    expected = full if omit_numbers else f"2 {full}"
    assert plugin.filter_native_speech(["2. 日本"]) == []
    plugin.poll()
    plugin.poll()
    assert spoken == [(expected, "next")], "The highlighted word should be read only once."
    spoken.clear()
    plugin = direct_plugin(omit_numbers, browse)
    plugin.preferences["PronounceCandidates"] = True
    plugin.pronunciations = pronunciations
    plugin.refresh_pronunciations = lambda: None
    plugin.poll()
    assert spoken == [(expected, "next")], "Navigation must speak even without a native speech event."

spoken.clear()
tone = (("dimsum", 2), "dimsum_cb", "tone", 1, False, [("1", "分"), ("2", "份")])
plugin = direct_plugin(report=tone)
plugin.poll()
assert spoken == [("1 分，2 份", "next")], "Tone input must speak the updated page without a native event."

plugin = direct_plugin()
for key in ("upArrow", "downArrow", "pageUp", "pageDown"):
    arrow = KeyboardInputGesture(key)
    sys.modules["api"].getFocusObject = InputComposition
    assert plugin.route_candidate_navigation(arrow) is False and arrow.sent == 1
    sys.modules["api"].getFocusObject = lambda: types.SimpleNamespace(windowHandle=42)
    assert plugin.route_candidate_navigation(arrow) is True and arrow.sent == 1
    sys.modules["api"].getFocusObject = InputComposition
    modified = KeyboardInputGesture(key, ("shift",))
    assert plugin.route_candidate_navigation(modified) is True and modified.sent == 0
sys.modules["inputCore"].manager.isInputHelpActive = True
assert plugin.route_candidate_navigation(KeyboardInputGesture("upArrow")) is True
sys.modules["inputCore"].manager.isInputHelpActive = False
assert plugin.route_candidate_navigation(KeyboardInputGesture("g")) is True

cleared = (("dimsum", 2), "dimsum_cb", "clear", 0, False, [])
plugin.read_report = lambda: (report_time(), cleared)
assert plugin.route_candidate_navigation(KeyboardInputGesture("pageDown")) is True
plugin = direct_plugin()
plugin.last_report = ordinary_page[0]
plugin.delivered_window = 43
assert plugin.route_candidate_navigation(KeyboardInputGesture("pageUp")) is True

# Notepad may expose only the preedit (including its raw code), or a
# previous Weasel inline preview while Rime has already changed pages.
assert reporter.preedit_signature("bp") == "/p2:00597757"
raw = "f$"
chosen = [("1", "過"), ("2", "日本")]
for event, key_name in (("page", "pageDown"), ("page", "pageUp"), ("browse", "upArrow"), ("browse", "downArrow")):
    new_page = (("dimsum" + reporter.preedit_signature(raw), 4), "dimsum_cb", event, 1, False, chosen)
    plugin = direct_plugin(report=new_page)
    plugin.preferences["PronounceCandidates"] = True
    plugin.pronunciations = pronunciations
    plugin.refresh_pronunciations = lambda: None
    plugin.read_report = lambda: (report_time(), new_page)
    sys.modules["api"].getFocusObject = lambda: composition
    for preedit in (raw, "f$  [ *1. 分  2. 芬  ]", "f$  [ *1. 過  2. 日本  ]"):
        composition.compositionString = preedit
        key = KeyboardInputGesture(key_name)
        assert plugin.route_candidate_navigation(key) is False and key.sent == 1
        assert plugin.filter_native_speech(["f", "$"]) == []
        assert plugin.filter_native_speech([preedit]) == []
        assert plugin.filter_native_speech(["已確認的內容"]) == ["已確認的內容"]
    spoken.clear()
    plugin.poll()
    if event == "page":
        assert spoken == [("1 過去，戈先生，戈小姐，2 日子，本來", "next")]
    else:
        assert spoken == [("1 " + reporter.describe("過", pronunciations, all_groups=True), "next")]

composition.compositionString = "f$  [ *1. 過  2. 日本  ]"
assert plugin.filter_native_speech(["f$", "1 過, 2 日本,"]) == []
composition.compositionString = "other"
assert plugin.filter_native_speech(["other"]) == ["other"]
assert plugin.route_candidate_navigation(KeyboardInputGesture("downArrow")) is True

# Alternate arrows while the native inline display lags the candidate snapshot.
composition.compositionString = "f$  [ *1. 分  2. 芬  ]"
spoken.clear()
for revision, (key_name, selected) in enumerate((("downArrow", 2), ("upArrow", 1), ("downArrow", 2), ("upArrow", 1)), start=11):
    current = (new_page[0][:1] + (revision,), "dimsum_cb", "browse", selected, False, chosen)
    plugin.read_report = lambda: (report_time(), current)
    gesture = KeyboardInputGesture(key_name)
    assert plugin.route_candidate_navigation(gesture) is False and gesture.sent == 1
    assert plugin.filter_native_speech(["f$"]) == []
    plugin.poll()
assert [text for text, _ in spoken] == [
    f"{chosen[i][0]} {reporter.describe(chosen[i][1], pronunciations, all_groups=True)}" for i in (1, 0, 1, 0)
]
one_finger = (new_page[0], "dimsum_full", "browse", 1, False, chosen)
assert plugin.direct_candidate_text(one_finger) == reporter.describe("過", pronunciations, all_groups=True)
plugin.preferences["PronounceCandidates"] = False
assert plugin.direct_candidate_text(("dimsum", "dimsum_cb", "browse", 1, False, chosen)) == "1 過"
plugin.preferences["PronounceCandidates"] = True

single = (("older-reporter", 10), "dimsum_cb", "page", 1, False, [("1", "過")])
plugin = direct_plugin(report=single)
composition.compositionString = "f$  [ *1. 過  ]"
assert plugin.filter_native_speech(["f$"]) == []
assert plugin.route_candidate_navigation(KeyboardInputGesture("pageUp")) is False

# Restore native edit gestures and speech when there is no matching Dimsum composition.
plugin.read_report = lambda: (report_time(), cleared)
assert plugin.filter_native_speech(["f$"]) == ["f$"]
assert plugin.route_candidate_navigation(KeyboardInputGesture("pageDown")) is True
plugin = direct_plugin(report=new_page)
sys.modules["api"].getFocusObject = lambda: types.SimpleNamespace(windowHandle=42)
assert plugin.filter_native_speech(["f$"]) == ["f$"]
assert plugin.route_candidate_navigation(KeyboardInputGesture("downArrow")) is True
plugin.delivered_window = 42
assert plugin.filter_native_speech(["f", "$"]) == [], "Suppress an exact raw-code echo in the known editor during the composition."
assert plugin.filter_native_speech(["f"]) == ["f"], "Do not suppress part of an unrelated document line."
plugin.read_report = lambda: (reporter.time.time_ns() - reporter.NATIVE_SPEECH_WINDOW_NS - 1, new_page)
assert plugin.filter_native_speech(["f$"]) == ["f$"]

spoken.clear()
next_page = (("dimsum", 3), "dimsum_cb", "page", 1, False, [("1", "過"), ("2", "日本")])
plugin = direct_plugin(report=next_page)
plugin.refresh_settings = lambda: (_ for _ in ()).throw(OSError("temporary settings read"))
original_message = sys.modules["ui"].message

def temporarily_unavailable(text, speechPriority=None):
    raise RuntimeError("speech temporarily unavailable")

sys.modules["ui"].message = temporarily_unavailable
plugin.poll()
assert plugin.last_report is None
sys.modules["ui"].message = original_message
plugin.poll()
plugin.poll()
assert spoken == [("1 過，2 日本", "next")], "A transient failure should leave the latest page pending."

spoken.clear()
plugin = direct_plugin(report=ordinary_page)
plugin.read_report = lambda: (reporter.time.time_ns(), ordinary_page)
plugin.poll()
assert not spoken and plugin.last_report is None, "A fresh typing update waits briefly."
plugin.read_report = lambda: (report_time(), next_page)
plugin.poll()
assert spoken == [("1 過，2 日本", "next")], "A burst should only announce the latest candidates."

queues = []
sys.modules["config"].conf["featureFlag"]["cancelExpiredFocusSpeech"] = 1
sys.modules["speech"].speak = lambda sequence, priority: queues.append((sequence, priority))
sys.modules["braille"].handler = types.SimpleNamespace(message=lambda text: None)
plugin = direct_plugin()
plugin.poll()
command, first = queues[0][0]
assert command.is_current() and first == "1 分，2 芬"
plugin.read_report = lambda: (reporter.time.time_ns(), next_page)
plugin.poll()
assert not command.is_current() and len(queues) == 1, "The older queued candidate expires even before the new one is ready."
plugin.read_report = lambda: (report_time(), next_page)
plugin.poll()
command, latest = queues[-1][0]
assert command.is_current() and latest == "1 過，2 日本"
sys.modules["config"].conf["featureFlag"]["cancelExpiredFocusSpeech"] = 2

with TemporaryDirectory() as temporary:
    reporter.BASE = Path(temporary)
    def snapshot_file(slot, revision):
        file = reporter.BASE / f"candidate-report-{slot}"
        file.write_bytes("\0".join(("dimsum-win-1", "test", str(revision), "dimsum_cb", "page", "1", "0", "1", "你")).encode())
        reporter.os.utime(file, ns=(100, 100))
        return file

    snapshot_file(1, 8)
    newer = snapshot_file(3, 9)
    plugin = direct_plugin()
    plugin.read_report = types.MethodType(reporter.GlobalPlugin.read_report, plugin)
    assert plugin.read_report()[1][0] == ("test", 9), "Read the alternate slot when file timestamps tie."
    plugin.poll()
    newer.unlink()
    assert plugin.read_report() is None, "Do not fall back to an older report with the same timestamp."

plugin = direct_plugin()
plugin.pronunciations = pronunciations
plugin.refresh_pronunciations = lambda: None
plugin.preferences["PronounceCandidates"] = True
assert plugin.direct_candidate_text(browse[:2] + ("page",) + browse[3:]).endswith("2 日子，本來")
spoken.clear()
plugin.preferences["UseDimsumReviewDescription"] = True

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
    assert bindings == {}, "The character-description override is off by default."

print("NVDA reporting, pronunciation migration, review gestures and independent lookup passed")
