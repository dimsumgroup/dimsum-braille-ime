import json
import os
from pathlib import Path
import re
import runpy
import time
from uuid import uuid4

import api
import braille
import config
import globalCommands
import globalPluginHandler
import inputCore
from keyboardHandler import KeyboardInputGesture
from NVDAObjects.behaviors import CandidateItem
from NVDAObjects.inputComposition import InputComposition
import speech
from speech import extensions
from scriptHandler import getLastScriptRepeatCount, script
import textInfos
import ui
import wx

try:
    from speech.commands import _CancellableSpeechCommand
except ImportError:
    _CancellableSpeechCommand = None


BASE = Path(os.environ["LOCALAPPDATA"]) / "DimsumInput"
ASSETS = Path(__file__).resolve().parent.parent
FILES = ("characterDescriptions.dic", "symbols.dic", "cldr.dic")
DICTIONARY_DATA = runpy.run_path(str(ASSETS / "dictionaryData.py"))
SCHEMAS = {"dimsum_cb", "dimsum_cb_std", "dimsum_full"}
# Native candidate speech normally follows the Rime update immediately.
NATIVE_SPEECH_WINDOW_NS = 500_000_000
REPORT_SLOTS = 3
CANDIDATE_SETTLE_NS = 80_000_000


def load_pronunciations(directory):
    pronunciations = {}
    for line in (directory / FILES[0]).read_text(encoding="utf-8-sig").splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split("\t")
        key = fields[0]
        if not DICTIONARY_DATA["is_described_character"](key) or key in pronunciations:
            continue
        groups = (part.strip() for field in fields[1:] for part in field.split(" "))
        groups = tuple(group for group in groups if group not in {"", "-"})
        if groups:
            pronunciations[key] = groups
    for name in FILES[1:]:
        for key, replacement in DICTIONARY_DATA["read_symbol_rows"](directory / name):
            if len(key) == 1 and replacement.strip() not in {"", "-"}:
                pronunciations.setdefault(key, (replacement.strip(),))
    return pronunciations


def describe(text, pronunciations, all_groups=False):
    return "，".join(
        "，".join(groups if all_groups else groups[:1]) if groups else character
        for character in text if not character.isspace()
        for groups in (pronunciations.get(character),)
    )


def parse_report(data):
    fields = data.decode("utf-8").split("\0")
    if len(fields) < 7 or len(fields) % 2 == 0:
        return None
    protocol, session, revision, schema, event, selected, singleton, *items = fields
    if protocol != "dimsum-win-1" or schema not in SCHEMAS or not revision.isdecimal() or not selected.isdecimal():
        return None
    if event not in {"page", "browse", "tone", "same", "clear"}:
        return None
    return (session, int(revision)), schema, event, int(selected), singleton == "1", list(zip(items[::2], items[1::2]))


def omit_candidate_numbers(preferences):
    return preferences.get("OmitCandidateNumbers", preferences.get("ReportingMode") == "compact")


def preedit_signature(text):
    data = text.strip(" \t\r\n").encode("utf-8")
    checksum = 5381
    for byte in data:
        checksum = (checksum * 33 + byte) & 0xffffffff
    return f"/p{len(data)}:{checksum:08x}"


def matching_inline_preedit(focus, report):
    composition = focus
    for _ in range(3):
        if isinstance(composition, InputComposition):
            break
        composition = getattr(composition, "parent", None)
    if not isinstance(composition, InputComposition) or composition.isReading:
        return None

    text = composition.compositionString.strip()
    if not text:
        return None
    report_id, _, _, _, _, items = report
    prefix, separator, preview = text.rpartition("  [")
    if not separator or not preview.endswith("]"):
        prefix, preview = text, ""
    if report_id[0].endswith(preedit_signature(prefix)):
        return prefix, preview

    # The actual candidate list can still identify an unsigned or changing
    # inline preview, including the one-item last page.
    if not preview or not items:
        return None
    remainder = preview[:-1]
    for _, word in items:
        if not word:
            return None
        _, found, remainder = remainder.partition(word)
        if not found:
            return None
    return prefix, preview


def inline_preedit_speech(sequence, preedit):
    strings = [part for part in sequence if isinstance(part, str)]
    text = "".join("".join(strings).split())
    if not text:
        return False
    prefix, preview = preedit
    raw = "".join(prefix.split())
    candidates = "".join(preview.split())
    if text in raw or (candidates and (text in candidates or text == raw + "[" + candidates)):
        return True
    if not raw or not candidates or not text.startswith(raw):
        return False
    # NVDA can emit the code and a flattened candidate list together,
    # dropping Weasel's brackets or changing label punctuation.
    spoken_candidates = re.sub(r"[\[\].*，,]", "", text[len(raw):])
    preview_text = re.sub(r"[\[\].*，,]", "", candidates)
    return bool(spoken_candidates) and spoken_candidates == preview_text


def native_candidate_speech(sequence, focus, items, selected):
    text = " ".join(part.strip() for part in sequence if isinstance(part, str)).rstrip(" ,，")
    if not text or not 0 < selected <= len(items):
        return False
    words = [word for _, word in items]
    names = [CandidateItem.getFormattedCandidateName(focus, number, word) for number, word in enumerate(words, 1)]
    if len(names) > 1 and text == ", ".join(names):
        return True
    word = words[selected - 1]
    name = names[selected - 1]
    if isinstance(focus, CandidateItem):
        return getattr(focus, "basicText", None) == word and (
            text in {word, name} or text.startswith((name + ", ", name + "，"))
        )
    return isinstance(focus, InputComposition) and not focus.isReading and focus.compositionString.strip() == text == word


def record_nvda_profile():
    """Share this NVDA process's current interface language and profile path."""
    try:
        import languageHandler
        from NVDAState import WritePaths

        profile = {
            "Language": languageHandler.getLanguage(),
            "DictionaryLanguage": speech.getCurrentLanguage(),
            "ConfigDirectory": str(Path(WritePaths.configDir).resolve()),
        }
        BASE.mkdir(parents=True, exist_ok=True)
        marker = BASE / f"nvda-running-{os.getpid()}.json"
        temporary = marker.with_suffix(".tmp")
        temporary.write_text(json.dumps(profile), encoding="utf-8")
        temporary.replace(marker)
        return marker
    except (ImportError, AttributeError, OSError, TypeError, ValueError):
        return None


def write_atomic(path, contents):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid4().hex + ".tmp")
    try:
        temporary.write_bytes(contents)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def remove_legacy_native_symbols():
    import characterProcessing
    from logHandler import log
    from NVDAState import WritePaths

    profile = Path(WritePaths.configDir)
    changed = False
    for locale in DICTIONARY_DATA["LOCALES"]:
        try:
            user_symbols = profile / f"symbols-{locale}.dic"
            if not user_symbols.is_file() or user_symbols.is_symlink():
                continue
            original = user_symbols.read_bytes()
            cleaned = DICTIONARY_DATA["remove_legacy_symbol_block"](original)
            if cleaned != original:
                backup = profile / "DimsumPronunciations" / "symbol-migration-backups"
                write_atomic(backup / f"{locale}-{uuid4().hex}.dic", original)
                write_atomic(user_symbols, cleaned)
                changed = True
        except OSError:
            log.warning("Could not clean the old Dimsum symbol block for %s", locale, exc_info=True)
    if changed:
        for definition in getattr(characterProcessing, "listAvailableSymbolDictionaryDefinitions", lambda: ())():
            if definition.name == "user":
                definition.symbols.invalidateAllData()
        characterProcessing.clearSpeechSymbols()


if _CancellableSpeechCommand is not None:
    class CandidateSpeech(_CancellableSpeechCommand):
        def __init__(self, is_current):
            super().__init__()
            self._is_current = is_current

        def _checkIfValid(self):
            return self._is_current()

        def _getDevInfo(self):
            return "Dimsum input candidate"


def can_cancel_candidate_speech():
    if _CancellableSpeechCommand is None:
        return False
    try:
        return config.conf["featureFlag"]["cancelExpiredFocusSpeech"] != 2
    except KeyError:
        return False


class GlobalPlugin(globalPluginHandler.GlobalPlugin):
    def __init__(self):
        super().__init__()
        self.last_speech = ""
        self.own_speech = False
        self.speaking_directly = False
        self.delivered_window = None
        self.observed_report = None
        self.pronunciation_revision = None
        self.pronunciations = {}
        self.preferences = {}
        self.settings_stamp = None
        self.last_report = None
        self.latest_stamp = time.time_ns()
        remove_legacy_native_symbols()
        self.refresh_settings()
        extensions.filter_speechSequence.register(self.filter_native_speech)
        extensions.pre_speech.register(self.capture)
        self.gesture_decider = getattr(inputCore, "decide_executeGesture", None)
        if self.gesture_decider is not None:
            self.gesture_decider.register(self.route_candidate_navigation)
        self.timer = wx.PyTimer(self.poll)
        self.timer.Start(40)
        self.profile_marker = record_nvda_profile()

    def terminate(self):
        self.timer.Stop()
        extensions.filter_speechSequence.unregister(self.filter_native_speech)
        extensions.pre_speech.unregister(self.capture)
        if self.gesture_decider is not None:
            self.gesture_decider.unregister(self.route_candidate_navigation)
        self.last_speech = ""
        if self.profile_marker is not None:
            try:
                self.profile_marker.unlink(missing_ok=True)
            except OSError:
                pass
        super().terminate()

    def capture(self, speechSequence, originalSpeechSequence=None, **kwargs):
        if self.own_speech:
            return
        original = speechSequence if originalSpeechSequence is None else originalSpeechSequence
        utterance = " ".join(item.strip() for item in original if isinstance(item, str) and item.strip())
        if utterance:
            self.last_speech = utterance

    def say(self, text, *, lookup_reply=False):
        self.own_speech = lookup_reply
        try:
            ui.message(text)
        finally:
            self.own_speech = False

    def refresh_settings(self):
        settings = BASE / "settings.json"
        try:
            stamp = settings.stat().st_mtime_ns
        except FileNotFoundError:
            stamp = 0
        if stamp == self.settings_stamp:
            return
        preferences = json.loads(settings.read_text(encoding="utf-8")) if stamp else {}
        self.preferences = preferences
        self.settings_stamp = stamp
        self.clearGestureBindings()
        shortcut = self.preferences.get("LookupGesture", "")
        if self.preferences.get("UseDimsumReviewDescription", False) or shortcut:
            self.bindGestures({"kb:numpad2": "reviewCharacter", "kb(laptop):NVDA+.": "reviewCharacter"})
        if shortcut:
            try:
                for identifier in KeyboardInputGesture.fromName(shortcut.lower()).identifiers:
                    self.bindGesture(inputCore.normalizeGestureIdentifier(identifier), "lookup")
            except (LookupError, ValueError):
                pass

    def refresh_pronunciations(self):
        root = BASE / "pronunciations"
        pointer = root / "active.txt"
        revision = pointer.read_text(encoding="utf-8").strip() if pointer.is_file() else ""
        if not re.fullmatch("[0-9a-f]{40}(?:-[0-9a-f]{32})?", revision):
            revision = ""
        directory = root / revision if revision else ASSETS
        if directory == self.pronunciation_revision:
            return
        try:
            self.pronunciations = load_pronunciations(directory)
        except (OSError, UnicodeError):
            self.pronunciations = load_pronunciations(ASSETS)
        self.pronunciation_revision = directory

    def script_lookup(self, gesture):
        original = self.last_speech.strip()
        if not original:
            self.say("暫時未取得 NVDA 最近嘅報讀。", lookup_reply=True)
            return
        try:
            self.refresh_pronunciations()
            if not any(character in self.pronunciations for character in original):
                self.say("呢段文字喺點心配詞表入面搵唔到配詞。", lookup_reply=True)
                return
            text = describe(original, self.pronunciations, all_groups=len(original) == 1)
            self.say(text, lookup_reply=True)
        except (OSError, UnicodeError):
            self.say("未能讀取點心配詞表。", lookup_reply=True)

    @script(description="讀出檢閱游標所在字，連按兩下查點心配詞", speakOnDemand=True)
    def script_reviewCharacter(self, gesture):
        # Use NVDA's repeat counter and call the native script directly so its
        # single- and triple-press paths see the same count as this wrapper.
        native = globalCommands.commands.script_review_currentCharacter
        repeat = getLastScriptRepeatCount()
        if repeat == 0:
            native(gesture)
            return
        text = None
        if repeat == 1 and self.preferences.get("UseDimsumReviewDescription", False):
            try:
                info = api.getReviewPosition().copy()
                info.expand(textInfos.UNIT_CHARACTER)
                self.refresh_pronunciations()
                if any(character in self.pronunciations for character in info.text):
                    text = describe(info.text, self.pronunciations, all_groups=True)
            except (OSError, UnicodeError, RuntimeError, NotImplementedError):
                pass
        # Keep the first press's text for lookup when the next press explains
        # that character or speaks its code point, including native replies.
        self.own_speech = True
        try:
            if text is None:
                native(gesture)
            else:
                braille.handler.handleReviewMove(shouldAutoTether=True)
                ui.reviewMessage(text)
        finally:
            self.own_speech = False

    def read_report(self):
        found = []
        for slot in range(1, REPORT_SLOTS + 1):
            file = BASE / f"candidate-report-{slot}"
            try:
                with file.open("rb") as stream:
                    stat = os.fstat(stream.fileno())
                    if stat.st_size > 65536 or stat.st_mtime_ns < self.latest_stamp:
                        continue
                    report = parse_report(stream.read(65537))
                    if report and not (stat.st_mtime_ns == self.latest_stamp and self.last_report
                                       and report[0][1] < self.last_report[1]):
                        found.append((stat.st_mtime_ns, report))
            except (OSError, UnicodeError, ValueError):
                continue
        return max(found, key=lambda entry: (entry[0], entry[1][0][1]), default=None)

    def direct_candidate_text(self, report):
        _, schema, event, selected, _, items = report
        if event == "clear" or not items:
            return None
        omit_numbers = omit_candidate_numbers(self.preferences)
        use_descriptions = self.preferences.get("PronounceCandidates", True)
        if use_descriptions:
            try:
                self.refresh_pronunciations()
            except (OSError, UnicodeError):
                pass
        if event == "browse" and 0 < selected <= len(items):
            word = items[selected - 1][1]
            text = (describe(word, self.pronunciations, all_groups=True) or word) if use_descriptions else word
        else:
            spoken = []
            for label, candidate in items:
                word = (describe(candidate, self.pronunciations) or candidate) if use_descriptions else candidate
                spoken.append(f"{label} {word}" if not omit_numbers and schema != "dimsum_full" else word)
            text = "，".join(spoken)

        return text or None

    def filter_native_speech(self, value):
        if self.speaking_directly or self.own_speech:
            return value
        if speech.getState().speechMode != speech.SpeechMode.talk:
            return value
        snapshot = self.read_report()
        if snapshot is None:
            return value
        stamp, report = snapshot
        _, _, event, selected, _, items = report
        focus = api.getFocusObject()
        handle = getattr(focus, "windowHandle", None)
        if handle is None or event == "clear" or (self.last_report == report[0] and self.delivered_window != handle):
            return value
        preedit = matching_inline_preedit(focus, report)
        if preedit is not None and inline_preedit_speech(value, preedit):
            return []
        if not 0 <= time.time_ns() - stamp <= NATIVE_SPEECH_WINDOW_NS:
            return value
        if not native_candidate_speech(value, focus, items, selected):
            return value
        # A native utterance can be canceled while NVDA is still processing
        # the key. Leave candidate delivery to the timer at NEXT priority.
        return []

    def route_candidate_navigation(self, gesture):
        if (not isinstance(gesture, KeyboardInputGesture)
                or gesture.mainKeyName.lower() not in {"uparrow", "downarrow", "pageup", "pagedown"}
                or gesture.modifierNames or inputCore.manager.isInputHelpActive):
            return True
        snapshot = self.read_report()
        if snapshot is None:
            return True
        _, report = snapshot
        report_id, _, event, selected, _, items = report
        focus = api.getFocusObject()
        window = getattr(focus, "windowHandle", None)
        if (event == "clear" or window is None or not 0 < selected <= len(items)
                or (self.last_report == report_id and self.delivered_window not in (None, window))
                or (matching_inline_preedit(focus, report) is None
                    and not native_candidate_speech([items[selected - 1][1]], focus, items, selected))):
            return True

        # Send candidate navigation to Rime instead of running a document script.
        # Send outside the keyboard hook so the injected key can reach the IME.
        def send_if_focused():
            if getattr(api.getFocusObject(), "windowHandle", None) == window:
                gesture.send()

        wx.CallAfter(send_if_focused)
        return False

    def speak_candidate(self, text, report_id, window):
        self.speaking_directly = True
        try:
            if can_cancel_candidate_speech() and speech.getState().speechMode == speech.SpeechMode.talk:
                # NVDA checks this command before starting the queued utterance.
                # It only invalidates our candidate speech, leaving other speech alone.
                command = CandidateSpeech(lambda: self.observed_report == report_id and
                    getattr(api.getFocusObject(), "windowHandle", None) == window)
                speech.speak([command, text], priority=speech.Spri.NEXT)
                braille.handler.message(text)
            else:
                ui.message(text, speechPriority=speech.Spri.NEXT)
        finally:
            self.speaking_directly = False

    def poll(self):
        try:
            self.refresh_settings()
        except (OSError, UnicodeError, ValueError):
            pass  # Keep the last usable settings if a refresh fails.

        try:
            snapshot = self.read_report()
            if snapshot is None:
                return
            stamp, report = snapshot
            self.observed_report = report[0]
            if self.last_report == report[0]:
                return
            # Wait for a short gap before adding a typing update to the speech queue.
            # One-by-one candidate browsing remains immediate.
            if report[2] in {"page", "tone", "same"} and time.time_ns() - stamp < CANDIDATE_SETTLE_NS:
                return
            text = self.direct_candidate_text(report)
            handle = getattr(api.getFocusObject(), "windowHandle", None)
            if text:
                self.speak_candidate(text, report[0], handle)
            self.latest_stamp = stamp
            self.last_report = report[0]
            self.delivered_window = handle
        except (OSError, RuntimeError, UnicodeError, ValueError):
            return
